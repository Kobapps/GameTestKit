using System;
using System.Collections.Generic;
using System.IO;
using Kobapps.GameTestKit.Scripting;
using UnityEditor;
using UnityEngine;

namespace Kobapps.GameTestKit.Editor
{
    /// <summary>
    /// Batch entry points for CI and for agents.
    /// </summary>
    /// <remarks>
    /// <code>
    /// Unity -batchmode -projectPath . \
    ///       -executeMethod Kobapps.GameTestKit.Editor.GameTesterCLI.Run \
    ///       -gtk-tags smoke -gtk-report Artifacts/gametests -gtk-stop-on-failure
    /// </code>
    /// Do <b>not</b> pass <c>-quit</c>: the run needs play mode, which outlives the
    /// <c>-executeMethod</c> call. The process exits by itself with 0 (all passed), 1 (failures) or
    /// 2 (the run could not complete).
    /// </remarks>
    [InitializeOnLoad]
    public static class GameTesterCLI
    {
        private const string CliModeKey = "GameTestKit.CliMode";
        private const string DeadlineKey = "GameTestKit.CliDeadline";

        public const int ExitPassed = 0;
        public const int ExitFailed = 1;
        public const int ExitBroken = 2;

        static GameTesterCLI()
        {
            // Re-attach after the domain reload that entering play mode causes.
            if (SessionState.GetBool(CliModeKey, false)) Attach();
        }

        /// <summary>Runs a batch and exits the Editor with a CI-friendly code.</summary>
        public static void Run()
        {
            var options = ParseCommandLine(out var maxMinutes);

            Debug.Log("[GameTestKit] CLI run starting: " +
                      $"filter='{options.NameFilter}' tags=[{string.Join(",", options.Tags)}] " +
                      $"paths={options.Paths.Count} report='{options.ArtifactRoot}'");

            var errors = new List<string>();
            var discovered = GameTestCatalog.DiscoverTests(options, errors);

            if (errors.Count > 0)
            {
                Debug.LogError("[GameTestKit] Scripts failed to parse:\n  " + string.Join("\n  ", errors));
                Exit(ExitBroken);
                return;
            }

            if (discovered.Count == 0)
            {
                Debug.LogError("[GameTestKit] No tests matched. Nothing to run.");
                Exit(ExitBroken);
                return;
            }

            SessionState.SetBool(CliModeKey, true);
            SessionState.SetString(DeadlineKey,
                (EditorApplication.timeSinceStartup + maxMinutes * 60.0).ToString("R"));
            Attach();

            if (!EditorTestRunner.Run(options, true, out var problem))
            {
                Debug.LogError($"[GameTestKit] Could not start the run: {problem}");
                Exit(ExitBroken);
            }
        }

        /// <summary>Prints every discovered test as JSON and exits. Used by tooling and agents.</summary>
        public static void List()
        {
            var errors = new List<string>();
            var tests = GameTestCatalog.DiscoverTests(new RunOptions(), errors);

            var root = JsonValue.NewObject();
            var array = JsonValue.NewArray();

            foreach (var test in tests)
            {
                var item = JsonValue.NewObject()
                    .Set("name", test.Name)
                    .Set("source", test.SourcePath ?? "")
                    .Set("steps", test.Steps.Count)
                    .Set("scene", test.Scene ?? "");

                var tags = JsonValue.NewArray();
                foreach (var tag in test.Tags) tags.Add(JsonValue.New(tag));
                item.Set("tags", tags);

                array.Add(item);
            }

            root.Set("tests", array);

            var problems = JsonValue.NewArray();
            foreach (var error in errors) problems.Add(JsonValue.New(error));
            root.Set("errors", problems);

            Emit(root, "-gtk-out");
            Exit(errors.Count > 0 ? ExitBroken : ExitPassed);
        }

        private static void Attach()
        {
            EditorTestRunner.RunCompleted -= OnCompleted;
            EditorTestRunner.RunCompleted += OnCompleted;
            EditorTestRunner.RunFailedToStart -= OnFailed;
            EditorTestRunner.RunFailedToStart += OnFailed;
            EditorApplication.update -= WatchDeadline;
            EditorApplication.update += WatchDeadline;
        }

        private static void OnCompleted(RunReport report)
        {
            if (report == null)
            {
                Debug.LogError("[GameTestKit] The run finished but no report could be read.");
                Exit(ExitBroken);
                return;
            }

            Debug.Log($"[GameTestKit] {report.Summary()}");
            foreach (var test in report.Tests)
                if (test.IsFailure)
                    Debug.LogError($"[GameTestKit] {test.Status}: {test.Name} — {test.Message}");

            Exit(report.Success ? ExitPassed : ExitFailed);
        }

        private static void OnFailed(string reason)
        {
            Debug.LogError($"[GameTestKit] {reason}");
            Exit(ExitBroken);
        }

        private static void WatchDeadline()
        {
            if (!SessionState.GetBool(CliModeKey, false)) return;

            double deadline = double.TryParse(SessionState.GetString(DeadlineKey, "0"), out var parsed) ? parsed : 0;
            if (deadline <= 0 || EditorApplication.timeSinceStartup < deadline) return;

            Debug.LogError("[GameTestKit] The run exceeded its wall-clock budget (-gtk-timeout).");
            Exit(ExitBroken);
        }

        private static void Exit(int code)
        {
            SessionState.EraseBool(CliModeKey);
            SessionState.EraseString(DeadlineKey);
            EditorApplication.update -= WatchDeadline;

            if (Application.isBatchMode)
            {
                EditorApplication.Exit(code);
            }
            else
            {
                Debug.Log($"[GameTestKit] CLI finished with exit code {code} " +
                          "(the Editor stays open because this is not batch mode).");
            }
        }

        // ---------------------------------------------------------------- arguments

        private static RunOptions ParseCommandLine(out double maxMinutes)
        {
            var options = GameTesterSettings.Instance.CreateRunOptions();
            options.ReportFormats = new List<string> { "junit", "json", "html" };
            maxMinutes = 60;

            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : null;

                switch (args[i].ToLowerInvariant())
                {
                    case "-gtk-filter": options.NameFilter = Next() ?? ""; break;
                    case "-gtk-tags": AddAll(options.Tags, Next()); break;
                    case "-gtk-exclude-tags": AddAll(options.ExcludeTags, Next()); break;
                    case "-gtk-test": options.Paths.Add(Next()); break;
                    case "-gtk-suite": ApplySuite(options, Next()); break;
                    case "-gtk-report": options.ArtifactRoot = Next() ?? options.ArtifactRoot; break;
                    case "-gtk-formats":
                        options.ReportFormats.Clear();
                        AddAll(options.ReportFormats, Next());
                        break;
                    case "-gtk-retries": options.Retries = ParseInt(Next(), options.Retries); break;
                    case "-gtk-repeat": options.RunRepeat = Math.Max(1, ParseInt(Next(), 1)); break;
                    case "-gtk-seed": options.Seed = ParseInt(Next(), 0); break;
                    case "-gtk-shuffle": options.Shuffle = true; break;
                    case "-gtk-stop-on-failure": options.StopOnFirstFailure = true; break;
                    case "-gtk-screenshot-every-step": options.ScreenshotEveryStep = true; break;
                    case "-gtk-allow-log-errors": options.FailOnLogError = false; break;
                    case "-gtk-isolate-devices": options.IsolateRealDevices = true; break;
                    case "-gtk-no-overlay": options.ShowInputOverlay = false; break;
                    case "-gtk-pointer":
                        options.Pointer = string.Equals(Next(), "touch", StringComparison.OrdinalIgnoreCase)
                            ? PointerMode.Touch : PointerMode.Mouse;
                        break;
                    case "-gtk-backend":
                        var backend = (Next() ?? "auto").ToLowerInvariant();
                        options.Backend = backend == "inputsystem" ? InputBackendKind.InputSystem
                            : backend == "eventsystem" ? InputBackendKind.EventSystem
                            : InputBackendKind.Auto;
                        break;
                    case "-gtk-timescale": options.TimeScale = ParseFloat(Next(), 0f); break;
                    case "-gtk-speed": options.InputSpeedScale = ParseFloat(Next(), 1f); break;
                    case "-gtk-timeout": maxMinutes = ParseFloat(Next(), 60f); break;
                }
            }

            options.Tags.RemoveAll(string.IsNullOrWhiteSpace);
            options.Paths.RemoveAll(string.IsNullOrWhiteSpace);
            return options;
        }

        private static void ApplySuite(RunOptions options, string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Debug.LogError($"[GameTestKit] Suite file not found: {path}");
                return;
            }

            var suite = TestScriptParser.ParseSuite(File.ReadAllText(path), path);
            var artifactRoot = options.ArtifactRoot;

            var merged = suite.Options;
            if (!string.IsNullOrEmpty(artifactRoot)) merged.ArtifactRoot = artifactRoot;
            foreach (var include in suite.Include) merged.Paths.Add(include);

            CopyInto(merged, options);
        }

        private static void CopyInto(RunOptions from, RunOptions to)
        {
            to.NameFilter = from.NameFilter;
            to.Tags.Clear(); to.Tags.AddRange(from.Tags);
            to.ExcludeTags.Clear(); to.ExcludeTags.AddRange(from.ExcludeTags);
            to.Paths.Clear(); to.Paths.AddRange(from.Paths);
            to.RunRepeat = from.RunRepeat;
            to.Retries = from.Retries;
            to.StopOnFirstFailure = from.StopOnFirstFailure;
            to.Shuffle = from.Shuffle;
            to.Seed = from.Seed;
            to.DefaultStepTimeout = from.DefaultStepTimeout;
            to.LocatorTimeout = from.LocatorTimeout;
            to.InputSpeedScale = from.InputSpeedScale;
            to.TimeScale = from.TimeScale;
            to.Pointer = from.Pointer;
            to.Backend = from.Backend;
            to.IsolateRealDevices = from.IsolateRealDevices;
            to.ShowInputOverlay = from.ShowInputOverlay;
            to.FailOnLogError = from.FailOnLogError;
            to.IgnoredLogPatterns.Clear(); to.IgnoredLogPatterns.AddRange(from.IgnoredLogPatterns);
            to.ScreenshotOnFailure = from.ScreenshotOnFailure;
            to.ScreenshotEveryStep = from.ScreenshotEveryStep;
            to.CollectPerformance = from.CollectPerformance;
            to.ArtifactRoot = from.ArtifactRoot;
            to.ReportFormats.Clear(); to.ReportFormats.AddRange(from.ReportFormats);
        }

        private static void AddAll(List<string> target, string commaSeparated)
        {
            if (string.IsNullOrEmpty(commaSeparated)) return;
            foreach (var part in commaSeparated.Split(','))
                if (!string.IsNullOrWhiteSpace(part)) target.Add(part.Trim());
        }

        private static int ParseInt(string value, int fallback) =>
            int.TryParse(value, out var parsed) ? parsed : fallback;

        private static float ParseFloat(string value, float fallback) =>
            float.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : fallback;

        /// <summary>Writes JSON to the path given by <paramref name="argName"/>, or to the log.</summary>
        internal static void Emit(JsonValue json, string argName)
        {
            var text = json.ToJson();
            var args = Environment.GetCommandLineArgs();

            for (int i = 0; i < args.Length - 1; i++)
            {
                if (!string.Equals(args[i], argName, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    var path = args[i + 1];
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
                    File.WriteAllText(path, text);
                    Debug.Log($"[GameTestKit] Wrote {path}");
                    return;
                }
                catch (Exception e)
                {
                    Debug.LogError($"[GameTestKit] Could not write output file: {e.Message}");
                }
            }

            Debug.Log(text);
        }
    }
}
