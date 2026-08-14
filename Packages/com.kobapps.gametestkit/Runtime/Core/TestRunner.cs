using System;
using System.Collections;
using System.Collections.Generic;
using Kobapps.GameTestKit.Scripting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// Executes tests against the live game.
    /// </summary>
    /// <remarks>
    /// The runner drives each step's enumerator itself rather than handing it to Unity's coroutine
    /// scheduler. That is what makes it possible to attribute an exception to the exact step that threw
    /// it, enforce a per-step time budget, and keep a structured record of a run — none of which works
    /// if the steps are just fire-and-forget coroutines.
    /// </remarks>
    public sealed class TestRunner
    {
        public RunOptions Options { get; }
        public RunReport Report { get; }

        /// <summary>Raised after each test so a UI can stream results while the batch is still running.</summary>
        public event Action<TestRecord> TestFinished;

        /// <summary>Raised for progress lines: "3/12 Buy a sword".</summary>
        public event Action<string> Progress;

        private readonly ArtifactStore _artifacts;
        private readonly LogCapture _logs;
        private readonly PerformanceSampler _performance = new PerformanceSampler();

        private IInputBackend _backend;
        private TestContext _context;
        private Exception _pendingFailure;
        private float _testDeadline;

        /// <summary>Suite-wide fixtures, parsed once per run from <see cref="RunOptions.BeforeEachJson"/>.</summary>
        private List<TestStep> _beforeEach = new List<TestStep>();
        private List<TestStep> _afterEach = new List<TestStep>();
        private int _stepCounter;
        private float _originalTimeScale = 1f;

        public TestRunner(RunOptions options, string runName = null)
        {
            Options = options ?? new RunOptions();
            if (Options.Seed == 0) Options.Seed = Environment.TickCount & 0x7fffffff;

            _artifacts = new ArtifactStore(Options.ArtifactRoot, runName ?? DefaultRunName());
            _logs = new LogCapture(Options.IgnoredLogPatterns);

            Report = new RunReport
            {
                Suite = runName ?? "GameTestKit",
                Seed = Options.Seed,
                UnityVersion = Application.unityVersion,
                Platform = Application.platform.ToString(),
                Device = SystemInfo.deviceModel,
                StartedAtUtc = DateTime.UtcNow.ToString("O"),
            };
        }

        public ArtifactStore Artifacts => _artifacts;

        private static string DefaultRunName() => "run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");

        // ================================================================ batch

        /// <summary>Runs a batch. Yields until everything has finished; results land in <see cref="Report"/>.</summary>
        public IEnumerator Run(IList<GameTest> tests)
        {
            var started = Time.realtimeSinceStartup;
            var selection = Select(tests);

            Debug.Log($"[GameTestKit] Running {selection.Count} test(s), seed {Options.Seed}, " +
                      $"artifacts → {_artifacts.RunFolder}");

            // Parsed once, here, so a broken fixture is one error at the start of the run rather than
            // the same error repeated against every test in the batch.
            EventSteps.ResetForRun();

            try
            {
                _beforeEach = TestScriptParser.ParseFixture(Options.BeforeEachJson, "beforeEach");
                _afterEach = TestScriptParser.ParseFixture(Options.AfterEachJson, "afterEach");
            }
            catch (Exception e)
            {
                Debug.LogError($"[GameTestKit] {e.Message}");

                foreach (var test in selection)
                    Report.Tests.Add(new TestRecord
                    {
                        Name = test.Name,
                        Category = test.Category,
                        Status = TestStatus.Error,
                        Message = e.Message,
                    });

                Finish(started);
                yield break;
            }

            LiveStatus.BeginRun(Report.Suite, selection.Count);

            _originalTimeScale = Time.timeScale;
            if (Options.TimeScale > 0f) Time.timeScale = Options.TimeScale;

            _logs.Attach();

            try
            {
                _backend = InputBackendFactory.Create(Options);
                _backend.Begin(Options);
            }
            catch (Exception e)
            {
                _logs.Detach();
                Time.timeScale = _originalTimeScale;
                Debug.LogError($"[GameTestKit] Could not start input backend: {e.Message}");

                foreach (var test in selection)
                    Report.Tests.Add(new TestRecord
                    {
                        Name = test.Name,
                        Status = TestStatus.Error,
                        Message = $"Input backend unavailable: {e.Message}",
                    });

                Finish(started);
                yield break;
            }

            Debug.Log($"[GameTestKit] Input backend: {_backend.Name}, pointer: {Options.Pointer}, " +
                      $"capabilities: {_backend.Capabilities}");

            // Said once, at the top of the run, because every problem it catches otherwise shows up as
            // a step that passed followed by a wait that timed out somewhere else entirely.
            InputReadiness.Report();

            InputOverlay.BeginSession(Report.Suite, Options.ShowInputOverlay);

            var user = new VirtualUser(_backend, Options);
            var locate = new LocatorEngine(Options);

            for (int repetition = 0; repetition < Math.Max(1, Options.RunRepeat); repetition++)
            {
                for (int i = 0; i < selection.Count; i++)
                {
                    var test = selection[i];
                    Progress?.Invoke($"{i + 1}/{selection.Count} {test.Name}");
                    LiveStatus.BeginTest(test.Name, i + 1);

                    if (test.Skip)
                    {
                        var skipped = new TestRecord
                        {
                            Name = test.Name,
                            Description = test.Description,
                            SourcePath = test.SourcePath,
                            Category = test.Category,
                            Status = TestStatus.Skipped,
                            Message = test.SkipReason ?? "Marked skip in the script.",
                        };
                        skipped.Tags.AddRange(test.Tags);
                        Report.Tests.Add(skipped);
                        TestFinished?.Invoke(skipped);

                        // Streamed like any other verdict, or a skipped test would sit unmarked in a
                        // watching UI until the whole batch finished.
                        LiveStatus.EndTest(skipped);
                        continue;
                    }

                    TestRecord record = null;
                    int attempts = 1 + Math.Max(test.Retries, Options.Retries);

                    for (int attempt = 1; attempt <= attempts; attempt++)
                    {
                        record = new TestRecord
                        {
                            Name = test.Name,
                            Description = test.Description,
                            SourcePath = test.SourcePath,
                            Category = test.Category,
                            Attempt = attempt,
                            StartedAtUtc = DateTime.UtcNow.ToString("O"),
                        };
                        record.Tags.AddRange(test.Tags);

                        yield return RunOne(test, record, user, locate);

                        if (!record.IsFailure) break;

                        if (attempt < attempts)
                            Debug.LogWarning(
                                $"[GameTestKit] '{test.Name}' failed on attempt {attempt}/{attempts}, retrying: {record.Message}");
                    }

                    Report.Tests.Add(record);
                    TestFinished?.Invoke(record);
                    LiveStatus.EndTest(record);

                    if (record.IsFailure && Options.StopOnFirstFailure)
                    {
                        Debug.LogWarning("[GameTestKit] Stopping after first failure (stopOnFirstFailure).");
                        repetition = int.MaxValue - 1;
                        break;
                    }
                }
            }

            try { _backend.End(); }
            catch (Exception e) { Debug.LogWarning($"[GameTestKit] Input teardown: {e.Message}"); }

            InputOverlay.EndSession();
            _logs.Detach();
            Time.timeScale = _originalTimeScale;

            Finish(started);
        }

        private void Finish(float started)
        {
            Report.FinishedAtUtc = DateTime.UtcNow.ToString("O");
            Report.DurationSeconds = Time.realtimeSinceStartup - started;
            Debug.Log($"[GameTestKit] {Report.Summary()}");
        }

        private List<GameTest> Select(IList<GameTest> tests)
        {
            var selection = new List<GameTest>();
            foreach (var test in tests)
                if (Options.Matches(test)) selection.Add(test);

            if (Options.Shuffle)
            {
                var random = new System.Random(Options.Seed);
                for (int i = selection.Count - 1; i > 0; i--)
                {
                    int j = random.Next(i + 1);
                    (selection[i], selection[j]) = (selection[j], selection[i]);
                }
            }

            return selection;
        }

        // ================================================================ one test

        private IEnumerator RunOne(GameTest test, TestRecord record, VirtualUser user, LocatorEngine locate)
        {
            var started = Time.realtimeSinceStartup;
            _testDeadline = started + Math.Max(1f, test.TimeoutSeconds);
            _stepCounter = 0;
            _pendingFailure = null;

            _artifacts.BeginTest($"{test.Name}{(record.Attempt > 1 ? $"-attempt{record.Attempt}" : "")}");
            _logs.Reset();
            _performance.Reset();

            _context = new TestContext(Options, user, locate, _artifacts, Options.Seed + test.Name.GetHashCode())
            {
                Test = test,
                Logs = _logs,
            };
            _context.NestedStepRunner = (step, parent) => RunStep(step, parent, record, "steps");

            record.Status = TestStatus.Passed;

            // --- scene ---------------------------------------------------
            if (!string.IsNullOrEmpty(test.Scene))
            {
                var loadStep = new LoadSceneStep { SceneName = test.Scene };
                yield return RunStep(loadStep, null, record, "setup");
                if (_pendingFailure != null)
                {
                    Conclude(record, TestStatus.Error, $"Could not load scene '{test.Scene}': {_pendingFailure.Message}");
                    yield return FinishTest(record, started);
                    yield break;
                }
            }

            // --- beforeEach ----------------------------------------------
            // Before the test's own setup, so a suite-wide fixture establishes the world that a test's
            // setup then adjusts. A failure here is an error for the same reason setup's is: the test
            // never ran, and reporting it as a failure sends someone looking for a bug in the product.
            foreach (var step in _beforeEach)
            {
                yield return RunStep(step, null, record, "setup");
                if (_pendingFailure != null) break;
            }

            if (_pendingFailure != null)
            {
                Conclude(record, TestStatus.Error, "beforeEach failed: " + _pendingFailure.Message);
                yield return RunAfterEach(record);
                yield return FinishTest(record, started);
                yield break;
            }

            // --- setup ---------------------------------------------------
            foreach (var step in test.Setup)
            {
                yield return RunStep(step, null, record, "setup");
                if (_pendingFailure != null) break;
            }

            if (_pendingFailure != null)
            {
                Conclude(record, StatusFor(_pendingFailure, TestStatus.Error), "Setup failed: " + _pendingFailure.Message);
            }
            else
            {
                // --- body ------------------------------------------------
                for (int iteration = 0; iteration < Math.Max(1, test.Repeat) && _pendingFailure == null; iteration++)
                {
                    foreach (var step in test.Steps)
                    {
                        yield return RunStep(step, null, record, "steps");
                        if (_pendingFailure != null) break;
                    }
                }

                if (_pendingFailure != null)
                    Conclude(record, StatusFor(_pendingFailure, TestStatus.Failed), _pendingFailure.Message);
            }

            // --- failure evidence ---------------------------------------
            if (record.IsFailure && Options.ScreenshotOnFailure)
            {
                yield return _artifacts.Screenshot("FAILED", path =>
                {
                    if (path != null) record.Artifacts.Add(path);
                });
            }

            // --- teardown always ----------------------------------------
            if (test.Teardown.Count > 0)
            {
                var carried = _pendingFailure;
                _pendingFailure = null;

                foreach (var step in test.Teardown)
                {
                    yield return RunStep(step, null, record, "teardown");
                    if (_pendingFailure != null)
                    {
                        Debug.LogWarning($"[GameTestKit] Teardown step failed: {_pendingFailure.Message}");
                        _pendingFailure = null;
                    }
                }

                _pendingFailure = carried;
            }

            yield return RunAfterEach(record);
            yield return FinishTest(record, started);
        }

        /// <summary>
        /// The suite-wide fixture that always runs, after the test's own teardown.
        /// </summary>
        /// <remarks>
        /// Best-effort like teardown: a fixture that cannot clean up is worth a warning, but turning a
        /// green test red because the reset afterwards stumbled tells you nothing about the test.
        /// </remarks>
        private IEnumerator RunAfterEach(TestRecord record)
        {
            if (_afterEach.Count == 0) yield break;

            var carried = _pendingFailure;
            _pendingFailure = null;

            foreach (var step in _afterEach)
            {
                yield return RunStep(step, null, record, "teardown");

                if (_pendingFailure != null)
                {
                    Debug.LogWarning($"[GameTestKit] afterEach step failed: {_pendingFailure.Message}");
                    _pendingFailure = null;
                }
            }

            _pendingFailure = carried;
        }

        private IEnumerator FinishTest(TestRecord record, float started)
        {
            record.DurationSeconds = Time.realtimeSinceStartup - started;
            record.Logs.AddRange(_logs.Drain());
            record.EventProofs.AddRange(EventSteps.TakeClosed());
            if (Options.CollectPerformance) record.Performance = _performance.Summarize();
            _context = null;
            yield break;
        }

        private static TestStatus StatusFor(Exception exception, TestStatus fallback)
        {
            if (exception is TestTimeoutException) return TestStatus.Timeout;
            if (exception is TestFailureException) return fallback == TestStatus.Error ? TestStatus.Error : TestStatus.Failed;
            return TestStatus.Error;
        }

        private static void Conclude(TestRecord record, TestStatus status, string message)
        {
            record.Status = status;
            record.Message = message;
        }

        // ================================================================ one step

        private IEnumerator RunStep(TestStep step, StepRecord parent, TestRecord testRecord, string phase)
        {
            var record = new StepRecord
            {
                Index = _stepCounter++,
                Description = step.DisplayName,
                Phase = phase,
            };

            if (parent != null) parent.Children.Add(record);
            else testRecord.Steps.Add(record);

            var previousStep = _context.CurrentStep;
            _context.CurrentStep = record;
            _logs.CurrentStepIndex = record.Index;
            LiveStatus.BeginStep(record.Description, record.Index);
            InputOverlay.Step(record.Description, record.Index);

            float started = Time.realtimeSinceStartup;
            float budget = step.TimeoutSeconds ?? Options.DefaultStepTimeout;
            float stepDeadline = Math.Min(started + Math.Max(0.1f, budget), _testDeadline);

            Exception failure = null;
            var stack = new Stack<IEnumerator>();

            IEnumerator body = null;
            try { body = step.Execute(_context); }
            catch (Exception e) { failure = e; }

            if (body != null) stack.Push(body);

            while (failure == null && stack.Count > 0)
            {
                var top = stack.Peek();
                bool moved;

                try { moved = top.MoveNext(); }
                catch (Exception e) { failure = e; break; }

                if (!moved) { stack.Pop(); continue; }

                var current = top.Current;

                if (current is IEnumerator nested) { stack.Push(nested); continue; }

                if (Options.CollectPerformance) _performance.Tick();

                if (Time.realtimeSinceStartup > stepDeadline)
                {
                    failure = Time.realtimeSinceStartup > _testDeadline
                        ? new TestTimeoutException($"The test exceeded its {(_testDeadline - started):0.#}s budget during '{step.DisplayName}'.")
                        : new TestTimeoutException($"Step '{step.DisplayName}' did not finish within {budget:0.#}s.");
                    break;
                }

                // Pass Unity yield instructions (WaitForSeconds, AsyncOperation, WaitForEndOfFrame…)
                // through to the engine; everything else means "wait one frame".
                yield return current;
            }

            // A game-side error is a defect even when the step itself succeeded.
            if (failure == null && Options.FailOnLogError && _logs.FirstError != null)
            {
                var error = _logs.FirstError;
                failure = new TestFailureException(
                    $"The game logged an error during '{step.DisplayName}': {error.Message}");
                record.StackTrace = error.StackTrace;

                // Consume it, or the same error would also fail teardown and every later step.
                _logs.ClearPendingError();
            }

            record.DurationSeconds = Time.realtimeSinceStartup - started;

            if (failure != null)
            {
                record.Status = failure is TestTimeoutException ? StepStatus.Timeout
                    : failure is TestFailureException ? StepStatus.Failed
                    : StepStatus.Error;
                record.Message = failure.Message;
                record.StackTrace ??= failure is TestFailureException ? null : failure.StackTrace;

                Debug.LogWarning($"[GameTestKit] ✗ {step.DisplayName} — {failure.Message}");

                if (Options.ScreenshotOnFailure && parent == null)
                {
                    yield return _artifacts.Screenshot($"failed-{ArtifactStore.Sanitize(step.DisplayName)}", path =>
                    {
                        if (path != null) record.Artifacts.Add(path);
                    });
                }

                if (!step.ContinueOnFailure)
                    _pendingFailure = failure;
                else
                    _logs.ClearPendingError();
            }
            else
            {
                record.Status = StepStatus.Passed;

                if (Options.ScreenshotEveryStep)
                {
                    yield return _artifacts.Screenshot(ArtifactStore.Sanitize(step.DisplayName), path =>
                    {
                        if (path != null) record.Artifacts.Add(path);
                    });
                }
            }

            LiveStatus.EndStep(record.Status.ToString(), record.Message);

            _context.CurrentStep = previousStep;
            _logs.CurrentStepIndex = previousStep?.Index ?? -1;
        }
    }
}
