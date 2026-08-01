using System;
using System.Collections.Generic;
using System.IO;
using Kobapps.GameTestKit.Scripting;
using UnityEditor;
using UnityEngine;

namespace Kobapps.GameTestKit.Editor
{
    /// <summary>
    /// The API an agent drives a run through: start, poll, stop. Every call returns a JSON string.
    /// </summary>
    /// <remarks>
    /// A run happens inside play mode, which outlives the call that started it, so the natural shape for
    /// an agent is start-then-poll rather than one blocking call. <see cref="Start"/> returns as soon as
    /// the request is queued; <see cref="Status"/> answers the only three questions that matter while
    /// waiting — is it alive, what is it doing, and is it done — including the one a plain report file
    /// cannot answer: <c>stale</c>, meaning the run has stopped making progress and should be stopped
    /// rather than waited on.
    /// <para>
    /// From a Unity MCP client this is one <c>script-execute</c> call per operation, e.g.
    /// <c>GameTestKitAgent.Start("smoke")</c> then <c>GameTestKitAgent.Status()</c> on a timer.
    /// </para>
    /// </remarks>
    public static class GameTestKitAgent
    {
        /// <summary>A run with no step boundary for this long is reported as <c>stale</c>.</summary>
        public const float DefaultStaleAfterSeconds = 90f;

        /// <summary>
        /// Queues a run and enters play mode. <paramref name="tags"/> / <paramref name="nameFilter"/> /
        /// <paramref name="paths"/> filter the discovered scripts; all empty runs everything.
        /// <paramref name="suitePath"/> takes its options from a <c>.gamesuite.json</c> instead of the
        /// project defaults.
        /// </summary>
        public static string Start(string tags = null, string nameFilter = null, string paths = null,
            string suitePath = null, int retries = 0)
        {
            try
            {
                if (EditorApplication.isCompiling)
                    return Fail("Unity is still compiling — retry once it settles.");
                if (EditorApplication.isPlaying)
                    return Fail("Already in play mode. Call Stop() first, or wait for the current run.");

                // Unity refuses to enter play mode while the project has a compile error, and
                // EnterPlaymode() reports that by doing nothing at all — the run is queued, play mode never
                // starts, and a caller polling the heartbeat waits forever on a file that will never appear.
                if (EditorUtility.scriptCompilationFailed)
                    return Fail("The project has compile errors, so Unity will not enter play mode. " +
                                "Fix them and retry (the Console holds the details).");

                RunOptions options;
                if (!string.IsNullOrEmpty(suitePath))
                {
                    if (!File.Exists(suitePath)) return Fail($"No suite at '{suitePath}'.");
                    options = TestScriptParser.ParseSuite(File.ReadAllText(suitePath), suitePath).Options;
                }
                else
                {
                    options = GameTesterSettings.Instance.CreateRunOptions();
                }

                foreach (var tag in Split(tags)) options.Tags.Add(tag);
                foreach (var path in Split(paths)) options.Paths.Add(path);
                if (!string.IsNullOrEmpty(nameFilter)) options.NameFilter = nameFilter;
                options.Retries = retries;

                if (!EditorTestRunner.Run(options, true, out var problem))
                    return Fail(problem);

                return Status();
            }
            catch (Exception e)
            {
                return Fail(e.Message);
            }
        }

        /// <summary>
        /// What the run is doing right now. <c>state</c> is idle / running / finished / aborted;
        /// <c>stale</c> is true when nothing has advanced for <paramref name="staleAfterSeconds"/>.
        /// </summary>
        public static string Status(float staleAfterSeconds = DefaultStaleAfterSeconds)
        {
            var json = JsonValue.NewObject()
                .Set("ok", true)
                .Set("editorPending", EditorTestRunner.IsRunning)
                .Set("isPlaying", EditorApplication.isPlaying)
                .Set("isCompiling", EditorApplication.isCompiling)
                .Set("compileFailed", EditorUtility.scriptCompilationFailed);

            var live = ReadLive();
            if (live == null)
            {
                json.Set("state", "idle").Set("note", "No run has been started in this project yet.");
                return json.ToJson();
            }

            foreach (var key in new[]
                     {
                         "state", "heartbeatUtc", "elapsedSeconds", "run", "test", "testIndex", "testCount",
                         "step", "stepIndex", "lastStepStatus", "lastMessage", "passed", "failed", "scene",
                         "runFolder",
                     })
            {
                if (live.Has(key)) json[key] = live[key];
            }

            // The question a report file cannot answer: alive, or wedged?
            var sinceHeartbeat = SecondsSince(live["heartbeatUtc"].AsString(null));
            json.Set("secondsSinceHeartbeat", sinceHeartbeat);
            json.Set("stale", live["state"].AsString("") == "running" && sinceHeartbeat > staleAfterSeconds);

            // A queued run that play mode never picked up looks identical to a slow one from the outside.
            json.Set("startedButNotRunning",
                EditorTestRunner.IsRunning && live["state"].AsString("") != "running");

            var folder = EditorTestRunner.LastRunFolder();
            if (!string.IsNullOrEmpty(folder))
            {
                json.Set("lastRunFolder", folder);
                var report = EditorTestRunner.LoadLastReport();
                if (report != null) json.Set("lastRunSummary", report.Summary());
            }

            return json.ToJson();
        }

        /// <summary>Cancels the run and leaves play mode.</summary>
        public static string Stop()
        {
            try
            {
                EditorTestRunner.Cancel();
                return JsonValue.NewObject().Set("ok", true).Set("state", "aborted").ToJson();
            }
            catch (Exception e)
            {
                return Fail(e.Message);
            }
        }

        /// <summary>The full <c>results.json</c> of the most recent run, or an error when there is none.</summary>
        public static string LastResults()
        {
            var folder = EditorTestRunner.LastRunFolder();
            if (string.IsNullOrEmpty(folder)) return Fail("No run has finished yet.");

            var path = Path.Combine(folder, "results.json");
            if (!File.Exists(path)) return Fail($"No results.json under '{folder}'.");
            return File.ReadAllText(path);
        }

        // ---------------------------------------------------------------- helpers

        private static JsonValue ReadLive()
        {
            try
            {
                var path = LiveStatus.Path;
                if (!File.Exists(path)) return null;
                return JsonValue.Parse(File.ReadAllText(path));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static double SecondsSince(string isoUtc)
        {
            if (string.IsNullOrEmpty(isoUtc)) return -1;
            return DateTime.TryParse(isoUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var when)
                ? Math.Round((DateTime.UtcNow - when.ToUniversalTime()).TotalSeconds, 1)
                : -1;
        }

        private static IEnumerable<string> Split(string csv)
        {
            if (string.IsNullOrWhiteSpace(csv)) yield break;
            foreach (var part in csv.Split(','))
                if (!string.IsNullOrWhiteSpace(part)) yield return part.Trim();
        }

        private static string Fail(string message)
        {
            Debug.LogWarning($"[GameTestKit] agent: {message}");
            return JsonValue.NewObject().Set("ok", false).Set("error", message ?? "unknown").ToJson();
        }
    }
}
