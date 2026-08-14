using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Kobapps.GameTestKit.Scripting;
using UnityEngine;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// The public entry point: run tests from code, from the Editor, from CI, or from inside a shipped
    /// build. Everything else in the package is reachable from here.
    /// </summary>
    public static class GameTester
    {
        /// <summary>Where the Editor leaves a run request for play mode to pick up after the domain reload.</summary>
        public static string PendingRunPath
        {
            get
            {
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
                return Path.Combine(projectRoot, "Library", "GameTestKit", "pending-run.json");
            }
        }

        public static bool IsRunning { get; private set; }

        /// <summary>Result of the most recent run in this session.</summary>
        public static RunReport LastReport { get; private set; }

        /// <summary>
        /// Clears the run flag at the start of every play session.
        /// </summary>
        /// <remarks>
        /// <see cref="IsRunning"/> is cleared by <see cref="RunAsync"/>'s <c>finally</c>, but a run that is
        /// cancelled — or a play session stopped mid-run — never reaches it: exiting play mode destroys the
        /// coroutine host without unwinding the iterator. Normally the domain reload would reset the static
        /// anyway, but under Project Settings ▸ Editor ▸ Enter Play Mode Options with *Reload Domain*
        /// disabled it survives, and every later run then fails with "a run is already in progress" until
        /// the Editor restarts. <see cref="RuntimeInitializeLoadType.SubsystemRegistration"/> runs on entry
        /// to every play session whether or not the domain reloaded, which is exactly the guarantee needed.
        /// </remarks>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRunState()
        {
            IsRunning = false;
        }

        /// <summary>Raised as each test finishes — used by the GameTester window to stream results.</summary>
        public static event Action<TestRecord> TestFinished;

        /// <summary>Raised when a whole run finishes.</summary>
        public static event Action<RunReport> RunFinished;

        // ---------------------------------------------------------------- running

        /// <summary>Runs tests and returns the report through <paramref name="onFinished"/>.</summary>
        public static Coroutine Run(IList<GameTest> tests, RunOptions options = null,
            Action<RunReport> onFinished = null, string runName = null)
        {
            return Host.Instance.StartCoroutine(RunAsync(tests, options, onFinished, runName));
        }

        /// <summary>Coroutine form — yield this from your own test or bootstrap code.</summary>
        public static IEnumerator RunAsync(IList<GameTest> tests, RunOptions options = null,
            Action<RunReport> onFinished = null, string runName = null)
        {
            if (IsRunning)
                throw new InvalidOperationException("A GameTestKit run is already in progress.");

            options ??= GameTesterSettings.Instance.CreateRunOptions();
            tests ??= new List<GameTest>();

            IsRunning = true;
            var runner = new TestRunner(options, runName);
            runner.TestFinished += record => TestFinished?.Invoke(record);

            Host.Instance.Attach(runner);

            try
            {
                yield return runner.Run(tests);
            }
            finally
            {
                Host.Instance.Detach();
                IsRunning = false;
            }

            LastReport = runner.Report;
            ReportWriter.WriteAll(runner.Report, options, runner.Artifacts.RunFolder);
            WriteEditorPointer(runner);
            LiveStatus.EndRun(runner.Artifacts.RunFolder, runner.Report.Success);

            onFinished?.Invoke(runner.Report);
            RunFinished?.Invoke(runner.Report);

            if (options.QuitWhenDone) Quit(runner.Report.Success ? 0 : 1);
        }

        /// <summary>Discovers scripts, applies the filters in <paramref name="options"/>, and runs them.</summary>
        public static IEnumerator RunDiscoveredAsync(RunOptions options = null, Action<RunReport> onFinished = null)
        {
            options ??= GameTesterSettings.Instance.CreateRunOptions();

            var errors = new List<string>();
            var tests = GameTestCatalog.DiscoverTests(options, errors);

            if (errors.Count > 0)
                Debug.LogError($"[GameTestKit] {errors.Count} script(s) failed to parse:\n  " + string.Join("\n  ", errors));

            if (tests.Count == 0)
                Debug.LogWarning("[GameTestKit] No tests found. Check GameTesterSettings ▸ Test Folders, " +
                                 "or that your files end in .gametest.json.");

            yield return RunAsync(tests, options, onFinished);
        }

        /// <summary>Runs one test built in code (fluent API) or parsed from a string.</summary>
        public static IEnumerator RunSingleAsync(GameTest test, RunOptions options = null,
            Action<RunReport> onFinished = null)
        {
            return RunAsync(new List<GameTest> { test }, options, onFinished, test?.Name);
        }

        // ---------------------------------------------------------------- inspection

        /// <summary>Snapshot of what is on screen — selectors, states, bindings.</summary>
        public static SceneSnapshot Inspect(int maxElements = 250, bool includeHidden = false) =>
            SceneInspector.Capture(maxElements, includeHidden);

        /// <summary>Parses a script string into a test without running it.</summary>
        public static GameTest Parse(string json, string sourcePath = null) =>
            TestScriptParser.ParseTest(json, sourcePath);

        // ---------------------------------------------------------------- bootstrap

        /// <summary>
        /// Runs automatically at startup when a run was requested — by the Editor before entering play
        /// mode, or on a player's command line. This is what makes on-device batch runs work with no
        /// glue code in the game.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            var request = ReadPendingRequest();

            if (request != null && request.Command == "inspect")
            {
                Host.Instance.StartCoroutine(InspectRoutine(request));
                return;
            }

            var options = request?.Options ?? ReadCommandLine();
            if (options == null) return;

            Host.Instance.StartCoroutine(DelayedStart(options));
        }

        /// <summary>
        /// Loads a scene, lets it settle, and writes a scene snapshot to disk — the "what is on screen
        /// right now" call an agent makes before writing selectors into a test.
        /// </summary>
        private static IEnumerator InspectRoutine(PendingRequest request)
        {
            if (!string.IsNullOrEmpty(request.Scene))
            {
                var step = new LoadSceneStep { SceneName = request.Scene };
                var enumerator = step.Execute(null);
                while (true)
                {
                    object current;
                    try { if (!enumerator.MoveNext()) break; current = enumerator.Current; }
                    catch (Exception e)
                    {
                        Debug.LogError($"[GameTestKit] Inspect could not load '{request.Scene}': {e.Message}");
                        break;
                    }
                    yield return current;
                }
            }

            float end = Time.realtimeSinceStartup + Mathf.Max(0f, request.Delay);
            while (Time.realtimeSinceStartup < end) yield return null;
            yield return null;

            var snapshot = SceneInspector.Capture(request.MaxElements, request.IncludeHidden);

            try
            {
                var path = string.IsNullOrEmpty(request.OutputPath) ? DefaultInspectPath : request.OutputPath;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                File.WriteAllText(path, snapshot.ToJson().ToJson());
                Debug.Log($"[GameTestKit] Scene snapshot written to {path}\n{snapshot.ToText()}");
            }
            catch (Exception e)
            {
                Debug.LogError($"[GameTestKit] Could not write the snapshot: {e.Message}");
            }

            Quit(0);
        }

        /// <summary>Default location of the snapshot produced by an inspect request.</summary>
        public static string DefaultInspectPath
        {
            get
            {
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
                return Path.Combine(projectRoot, "Library", "GameTestKit", "scene-snapshot.json");
            }
        }

        private sealed class PendingRequest
        {
            public string Command = "run";
            public RunOptions Options;
            public string Scene;
            public float Delay = 1f;
            public int MaxElements = 250;
            public bool IncludeHidden;
            public string OutputPath;
        }

        private static IEnumerator DelayedStart(RunOptions options)
        {
            // Give the first scene a couple of frames to finish waking up before touching it.
            yield return null;
            yield return null;
            yield return RunDiscoveredAsync(options);
        }

        /// <summary>Where play mode tells the Editor a run has finished (survives the domain reload).</summary>
        public static string LastReportPointerPath
        {
            get
            {
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
                return Path.Combine(projectRoot, "Library", "GameTestKit", "last-report.json");
            }
        }

        private static void WriteEditorPointer(TestRunner runner)
        {
            if (!Application.isEditor) return;
            try
            {
                var path = LastReportPointerPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                var pointer = JsonValue.NewObject()
                    .Set("folder", runner.Artifacts.RunFolder.Replace('\\', '/'))
                    .Set("results", Path.Combine(runner.Artifacts.RunFolder, "results.json").Replace('\\', '/'))
                    .Set("summary", runner.Report.Summary())
                    .Set("success", runner.Report.Success)
                    .Set("finishedAt", runner.Report.FinishedAtUtc ?? "");

                File.WriteAllText(path, pointer.ToJson());
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GameTestKit] Could not write the report pointer: {e.Message}");
            }
        }

        private static PendingRequest ReadPendingRequest()
        {
            try
            {
                var requestPath = PendingRunPath;
                if (!File.Exists(requestPath)) return null;

                var json = JsonValue.Parse(File.ReadAllText(requestPath));
                File.Delete(requestPath);   // one-shot: never replay on the next Play press

                var request = new PendingRequest
                {
                    Command = json["command"].AsString("run"),
                    Scene = json["scene"].AsString(null),
                    Delay = json["delay"].AsFloat(1f),
                    MaxElements = json["maxElements"].AsInt(250),
                    IncludeHidden = json["includeHidden"].AsBool(false),
                    OutputPath = json["output"].AsString(null),
                };

                var options = TestScriptParser.ParseOptions(json["options"],
                    GameTesterSettings.Instance.CreateRunOptions());

                foreach (var scriptPath in json["paths"].AsStringList()) options.Paths.Add(scriptPath);
                foreach (var tag in json["tags"].AsStringList()) options.Tags.Add(tag);
                foreach (var category in json["categories"].AsStringList()) options.Categories.Add(category);
                foreach (var category in json["excludeCategories"].AsStringList())
                    options.ExcludeCategories.Add(category);
                if (json.Has("nameFilter")) options.NameFilter = json["nameFilter"].AsString("");

                request.Options = options;
                return request;
            }
            catch (Exception e)
            {
                Debug.LogError($"[GameTestKit] Could not read the pending run request: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Player command line:
        /// <c>MyGame.exe -gametests -gametest-categories Shop,Onboarding -gametest-report C:\out -gametest-quit</c>
        /// </summary>
        private static RunOptions ReadCommandLine()
        {
            string[] args;
            try { args = Environment.GetCommandLineArgs(); }
            catch { return null; }

            bool requested = false;
            var options = GameTesterSettings.Instance.CreateRunOptions();

            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : null;

                switch (args[i].ToLowerInvariant())
                {
                    case "-gametests":
                    case "-gametest":
                        requested = true;
                        break;
                    case "-gametest-filter":
                        options.NameFilter = Next() ?? "";
                        requested = true;
                        break;
                    case "-gametest-tags":
                        options.Tags.AddRange((Next() ?? "").Split(','));
                        requested = true;
                        break;
                    case "-gametest-categories":
                        options.Categories.AddRange((Next() ?? "").Split(','));
                        requested = true;
                        break;
                    case "-gametest-exclude-categories":
                        options.ExcludeCategories.AddRange((Next() ?? "").Split(','));
                        break;
                    case "-gametest-file":
                        options.Paths.Add(Next());
                        requested = true;
                        break;
                    case "-gametest-report":
                        options.ArtifactRoot = Next() ?? options.ArtifactRoot;
                        break;
                    case "-gametest-repeat":
                        if (int.TryParse(Next(), out var repeat)) options.RunRepeat = Math.Max(1, repeat);
                        break;
                    case "-gametest-quit":
                        options.QuitWhenDone = true;
                        break;
                }
            }

            if (!requested) return null;

            options.Tags.RemoveAll(string.IsNullOrWhiteSpace);
            options.Categories.RemoveAll(string.IsNullOrWhiteSpace);
            options.ExcludeCategories.RemoveAll(string.IsNullOrWhiteSpace);
            options.Paths.RemoveAll(string.IsNullOrWhiteSpace);
            return options;
        }

        private static void Quit(int exitCode)
        {
            Debug.Log($"[GameTestKit] Quitting with exit code {exitCode}.");
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit(exitCode);
#endif
        }

        // ---------------------------------------------------------------- host

        /// <summary>
        /// The hidden object that owns the run's coroutines and ticks the performance sampler.
        /// It survives scene loads so a test can drive the game across them.
        /// </summary>
        internal sealed class Host : MonoBehaviour
        {
            private static Host _instance;

            public static Host Instance
            {
                get
                {
                    if (_instance != null) return _instance;

                    // HideInHierarchy rather than HideAndDontSave: the latter survives leaving play
                    // mode in the Editor and leaks an orphan object into the next session.
                    var go = new GameObject("~GameTestKit")
                    {
                        hideFlags = HideFlags.HideInHierarchy,
                    };
                    DontDestroyOnLoad(go);
                    _instance = go.AddComponent<Host>();
                    return _instance;
                }
            }

            /// <summary>Kept so the object stays alive (and named) for the duration of a run.</summary>
            public void Attach(TestRunner runner) => gameObject.name = $"~GameTestKit ({runner.Report.Suite})";

            public void Detach() => gameObject.name = "~GameTestKit";

            private void OnDestroy()
            {
                if (_instance == this) _instance = null;
            }
        }
    }
}
