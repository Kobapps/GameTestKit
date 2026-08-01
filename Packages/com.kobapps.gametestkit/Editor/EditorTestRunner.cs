using System;
using System.IO;
using Kobapps.GameTestKit.Scripting;
using UnityEditor;
using UnityEngine;

namespace Kobapps.GameTestKit.Editor
{
    /// <summary>
    /// Runs tests from the Editor by entering play mode and letting the runtime take over.
    /// </summary>
    /// <remarks>
    /// Entering play mode triggers a domain reload, which destroys every static field and event
    /// subscription in the Editor. So the request and the result are passed as files under
    /// <c>Library/GameTestKit/</c> rather than in memory, and this class re-attaches its poller from
    /// <see cref="SessionState"/> after each reload. That is the difference between a runner that works
    /// and one that mysteriously does nothing every second time.
    /// </remarks>
    [InitializeOnLoad]
    public static class EditorTestRunner
    {
        private const string PendingKey = "GameTestKit.RunPending";
        private const string StartedKey = "GameTestKit.RunStartedAt";
        private const string StopWhenDoneKey = "GameTestKit.StopPlayModeWhenDone";

        /// <summary>How long to wait for play mode to produce a report before declaring the run lost.</summary>
        private const double StartupGraceSeconds = 60;

        /// <summary>Raised on the Editor side when a run finishes (after the domain reload).</summary>
        public static event Action<RunReport> RunCompleted;

        /// <summary>Raised when a run could not be completed, with the reason.</summary>
        public static event Action<string> RunFailedToStart;

        static EditorTestRunner()
        {
            if (SessionState.GetBool(PendingKey, false))
                EditorApplication.update += Poll;
        }

        public static bool IsRunning => SessionState.GetBool(PendingKey, false);

        /// <summary>The report of the most recent run, read back from disk.</summary>
        public static RunReport LoadLastReport()
        {
            try
            {
                var pointerPath = GameTester.LastReportPointerPath;
                if (!File.Exists(pointerPath)) return null;

                var pointer = JsonValue.Parse(File.ReadAllText(pointerPath));
                return ReportReader.FromFile(pointer["results"].AsString(""));
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string LastRunFolder()
        {
            try
            {
                var pointerPath = GameTester.LastReportPointerPath;
                if (!File.Exists(pointerPath)) return null;
                return JsonValue.Parse(File.ReadAllText(pointerPath))["folder"].AsString(null);
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// Starts a run. Returns false (with a reason) when the Editor is in a state where it cannot —
        /// compiling, already playing, or already running a batch.
        /// </summary>
        public static bool Run(RunOptions options, bool stopPlayModeWhenDone, out string problem)
        {
            problem = null;

            if (IsRunning) { problem = "A GameTestKit run is already in progress."; return false; }
            if (EditorApplication.isCompiling) { problem = "Unity is still compiling. Try again in a moment."; return false; }
            if (EditorApplication.isPlaying) { problem = "Exit play mode before starting a run."; return false; }

            options ??= GameTesterSettings.Instance.CreateRunOptions();

            try
            {
                var requestPath = GameTester.PendingRunPath;
                Directory.CreateDirectory(Path.GetDirectoryName(requestPath));
                File.WriteAllText(requestPath, TestScriptParser.WriteRunRequest(options).ToJson());

                if (File.Exists(GameTester.LastReportPointerPath))
                    File.Delete(GameTester.LastReportPointerPath);
            }
            catch (Exception e)
            {
                problem = $"Could not write the run request: {e.Message}";
                return false;
            }

            SessionState.SetBool(PendingKey, true);
            SessionState.SetBool(StopWhenDoneKey, stopPlayModeWhenDone);
            SessionState.SetString(StartedKey, EditorApplication.timeSinceStartup.ToString("R"));

            EditorApplication.update -= Poll;
            EditorApplication.update += Poll;
            EditorApplication.EnterPlaymode();
            return true;
        }

        /// <summary>Cancels a run in progress and leaves play mode.</summary>
        public static void Cancel()
        {
            Finish();
            LiveStatus.Abort("Cancelled from the Editor.");
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;

            try
            {
                if (File.Exists(GameTester.PendingRunPath)) File.Delete(GameTester.PendingRunPath);
            }
            catch (Exception) { /* the request is one-shot anyway */ }
        }

        private static void Poll()
        {
            if (!SessionState.GetBool(PendingKey, false))
            {
                EditorApplication.update -= Poll;
                return;
            }

            if (File.Exists(GameTester.LastReportPointerPath))
            {
                var report = LoadLastReport();
                bool stop = SessionState.GetBool(StopWhenDoneKey, true);
                Finish();

                if (stop && EditorApplication.isPlaying)
                    EditorApplication.isPlaying = false;

                RunCompleted?.Invoke(report);
                return;
            }

            // Still waiting. If play mode never started (or was stopped by hand), give up rather than
            // polling forever.
            double startedAt = double.TryParse(SessionState.GetString(StartedKey, "0"), out var parsed) ? parsed : 0;
            double elapsed = EditorApplication.timeSinceStartup - startedAt;

            if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode &&
                elapsed > StartupGraceSeconds)
            {
                Finish();
                RunFailedToStart?.Invoke(
                    "Play mode ended before the run reported any results. Check the Console for script errors.");
            }
        }

        private static void Finish()
        {
            SessionState.EraseBool(PendingKey);
            SessionState.EraseString(StartedKey);
            SessionState.EraseBool(StopWhenDoneKey);
            EditorApplication.update -= Poll;
        }
    }
}
