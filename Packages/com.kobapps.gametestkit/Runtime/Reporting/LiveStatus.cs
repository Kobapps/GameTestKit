using System;
using System.IO;
using Kobapps.GameTestKit.Scripting;
using UnityEngine;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// A heartbeat file an agent can poll while a run is in flight.
    /// </summary>
    /// <remarks>
    /// A run lives inside play mode, so the process that started it cannot see what it is doing until it
    /// finishes — and a run that wedges looks exactly like a run that is merely slow. This writes
    /// <c>Library/GameTestKit/live-status.json</c> on every step boundary, so a caller can tell the two
    /// apart: <c>heartbeatUtc</c> moving means progress, <c>heartbeatUtc</c> frozen on the same
    /// <c>step</c> means stuck, and <c>state == "finished"</c> means the report is ready. Nothing in the
    /// run depends on it — every write is best-effort and a failure is swallowed.
    /// </remarks>
    public static class LiveStatus
    {
        /// <summary>Where the heartbeat is written. Under <c>Library/</c> so it is never imported or committed.</summary>
        public static string Path
        {
            get
            {
                var projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? ".";
                return System.IO.Path.Combine(projectRoot, "Library", "GameTestKit", "live-status.json");
            }
        }

        private static string _state = "idle";
        private static string _runName;
        private static string _test;
        private static string _step;
        private static string _lastStepStatus;
        private static string _lastMessage;
        private static string _runFolder;
        private static int _testIndex, _testCount, _stepIndex, _passed, _failed;
        private static DateTime _startedUtc;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            // Enter Play Mode Options can disable the domain reload, which would otherwise carry a
            // half-finished run's state into the next session and report a stale "running".
            _state = "idle";
            _runName = _test = _step = _lastStepStatus = _lastMessage = _runFolder = null;
            _testIndex = _testCount = _stepIndex = _passed = _failed = 0;
        }

        public static void BeginRun(string runName, int testCount)
        {
            _state = "running";
            _runName = runName;
            _testCount = testCount;
            _testIndex = _stepIndex = _passed = _failed = 0;
            _test = _step = _lastStepStatus = _lastMessage = _runFolder = null;
            _startedUtc = DateTime.UtcNow;
            Write();
        }

        public static void BeginTest(string testName, int index)
        {
            _test = testName;
            _testIndex = index;
            _step = null;
            _stepIndex = 0;
            Write();
        }

        public static void BeginStep(string description, int index)
        {
            _step = description;
            _stepIndex = index;
            Write();
        }

        public static void EndStep(string status, string message)
        {
            _lastStepStatus = status;
            _lastMessage = message;
            Write();
        }

        public static void EndTest(bool passed)
        {
            if (passed) _passed++; else _failed++;
            Write();
        }

        public static void EndRun(string runFolder, bool success)
        {
            _state = "finished";
            _runFolder = runFolder;
            _step = null;
            _lastStepStatus = success ? "passed" : "failed";
            Write();
        }

        /// <summary>Marks the heartbeat as aborted — used when a run is cancelled from the Editor.</summary>
        public static void Abort(string reason)
        {
            _state = "aborted";
            _lastMessage = reason;
            Write();
        }

        private static void Write()
        {
            try
            {
                var path = Path;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));

                var json = JsonValue.NewObject()
                    .Set("state", _state)
                    .Set("heartbeatUtc", DateTime.UtcNow.ToString("O"))
                    .Set("startedUtc", _startedUtc == default ? null : _startedUtc.ToString("O"))
                    .Set("elapsedSeconds", _startedUtc == default ? 0.0 : (DateTime.UtcNow - _startedUtc).TotalSeconds)
                    .Set("run", _runName)
                    .Set("test", _test)
                    .Set("testIndex", _testIndex)
                    .Set("testCount", _testCount)
                    .Set("step", _step)
                    .Set("stepIndex", _stepIndex)
                    .Set("lastStepStatus", _lastStepStatus)
                    .Set("lastMessage", _lastMessage)
                    .Set("passed", _passed)
                    .Set("failed", _failed)
                    .Set("scene", UnityEngine.SceneManagement.SceneManager.GetActiveScene().name)
                    .Set("runFolder", _runFolder);

                File.WriteAllText(path, json.ToJson());
            }
            catch (Exception)
            {
                // A heartbeat that cannot be written must never break the run it is reporting on.
            }
        }
    }
}
