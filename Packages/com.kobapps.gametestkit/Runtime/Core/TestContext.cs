using System;
using System.Collections;
using UnityEngine;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// Everything a step is handed: the virtual user driving input, the locator engine, the artifact
    /// store, the run options, and hooks back into the runner for composite steps.
    /// </summary>
    public sealed class TestContext
    {
        public GameTest Test { get; internal set; }

        public RunOptions Options { get; }

        /// <summary>Simulated input — the only way a step should touch the game.</summary>
        public VirtualUser User { get; }

        /// <summary>Selector resolution with implicit waiting.</summary>
        public LocatorEngine Locate { get; }

        public ArtifactStore Artifacts { get; }

        public LogCapture Logs { get; internal set; }

        /// <summary>The record for the step currently executing — attach artifacts and notes to it.</summary>
        public StepRecord CurrentStep { get; internal set; }

        /// <summary>Deterministic randomness, seeded per run so a flaky order can be reproduced.</summary>
        public System.Random Random { get; }

        /// <summary>Seconds since this test started (wall clock).</summary>
        public float Elapsed => Time.realtimeSinceStartup - _startedAt;

        internal Func<TestStep, StepRecord, IEnumerator> NestedStepRunner;

        private readonly float _startedAt;

        public TestContext(RunOptions options, VirtualUser user, LocatorEngine locate, ArtifactStore artifacts, int seed)
        {
            Options = options ?? new RunOptions();
            User = user;
            Locate = locate;
            Artifacts = artifacts;
            Random = new System.Random(seed);
            _startedAt = Time.realtimeSinceStartup;
        }

        /// <summary>Fails the current step (and the test, unless the step is marked continue-on-failure).</summary>
        public void Fail(string message) => throw new TestFailureException(message);

        /// <summary>Writes a line into the report next to the running step.</summary>
        public void Log(string message)
        {
            Debug.Log($"[GameTest] {message}");
        }

        /// <summary>Resolves a selector, waiting for it to become usable. Shorthand used by most steps.</summary>
        public IEnumerator Resolve(string selector, ResolvedTarget result,
            LocatorEngine.Requirement requirement = LocatorEngine.Requirement.Exists, float? timeout = null)
        {
            return Locate.Resolve(selector, result, requirement, timeout ?? Options.LocatorTimeout);
        }

        /// <summary>Captures a screenshot and files it under the current step.</summary>
        public IEnumerator Screenshot(string name)
        {
            if (Artifacts == null) yield break;
            yield return Artifacts.Screenshot(name, path =>
            {
                if (path != null) CurrentStep?.Artifacts.Add(path);
            });
        }

        /// <summary>Runs a child step and records it under <paramref name="parent"/> (composite steps).</summary>
        public IEnumerator RunNested(TestStep step, StepRecord parent)
        {
            if (NestedStepRunner == null)
                throw new InvalidOperationException("Nested steps are only available while a runner is active.");
            return NestedStepRunner(step, parent);
        }
    }
}
