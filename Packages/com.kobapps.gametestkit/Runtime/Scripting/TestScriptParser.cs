using System;
using System.Collections.Generic;
using System.IO;

namespace Kobapps.GameTestKit.Scripting
{
    /// <summary>A suite: which tests to run and with what options.</summary>
    public sealed class TestSuiteScript
    {
        public string Name = "Suite";
        public string Description;
        public string SourcePath;
        public readonly List<string> Include = new List<string>();
        public RunOptions Options = new RunOptions();
    }

    /// <summary>
    /// Reads <c>.gametest.json</c> and <c>.gamesuite.json</c> files.
    /// </summary>
    /// <remarks>
    /// The format is deliberately declarative and small: a name, some tags, and a list of steps whose
    /// verb <em>is</em> the JSON key. That keeps scripts readable by a designer, diffable in review,
    /// writable by an agent without a compile step, and runnable in a shipped player.
    /// </remarks>
    public static class TestScriptParser
    {
        public const string TestExtension = ".gametest.json";
        public const string SuiteExtension = ".gamesuite.json";

        // ---------------------------------------------------------------- tests

        public static GameTest ParseTest(string json, string sourcePath = null)
        {
            JsonValue root;
            try { root = JsonValue.Parse(json); }
            catch (JsonParseException e)
            {
                throw new TestFailureException($"{Describe(sourcePath)} is not valid JSON: {e.Message}");
            }

            return FromJson(root, sourcePath);
        }

        public static GameTest FromJson(JsonValue root, string sourcePath = null)
        {
            var test = new GameTest
            {
                SourcePath = sourcePath,
                Name = DefaultName(sourcePath),
                Category = TestCategory.FromSourcePath(sourcePath),
            };

            // Shorthand: a bare array of steps is a whole test named after its file.
            if (root.IsArray)
            {
                AddSteps(root, test.Steps, sourcePath);
                return test;
            }

            if (!root.IsObject)
                throw new TestFailureException($"{Describe(sourcePath)} must contain a JSON object or an array of steps.");

            if (root.Has("name")) test.Name = root["name"].AsString(test.Name);

            // An explicit category overrides the folder. Worth having for files that cannot be moved
            // into the folder that describes them — a package sample, or the flattened Resources mirror
            // that ships inside a player build.
            if (root.Has("category")) test.Category = TestCategory.Normalize(root["category"].AsString(""));

            test.Description = StringOrNull(root["description"]);
            test.Scene = StringOrNull(root["scene"]);
            if (root.Has("timeout")) test.TimeoutSeconds = root["timeout"].AsFloat(test.TimeoutSeconds);
            if (root.Has("repeat")) test.Repeat = Math.Max(1, root["repeat"].AsInt(1));
            if (root.Has("retries")) test.Retries = Math.Max(0, root["retries"].AsInt(0));
            if (root.Has("skip")) test.Skip = root["skip"].AsBool();
            test.SkipReason = StringOrNull(root["skipReason"]);

            foreach (var tag in root["tags"].AsStringList())
                if (!string.IsNullOrWhiteSpace(tag)) test.Tags.Add(tag.Trim());

            AddSteps(root["setup"], test.Setup, sourcePath);
            AddSteps(root["steps"], test.Steps, sourcePath);
            AddSteps(root["teardown"], test.Teardown, sourcePath);

            if (test.Steps.Count == 0 && test.Setup.Count == 0)
                throw new TestFailureException($"{Describe(sourcePath)} has no \"steps\".");

            return test;
        }

        private static void AddSteps(JsonValue array, List<TestStep> target, string sourcePath)
        {
            if (array == null || array.IsNull) return;
            if (!array.IsArray)
                throw new TestFailureException($"{Describe(sourcePath)}: expected an array of steps.");

            int index = 0;
            foreach (var item in array)
            {
                try { target.Add(StepRegistry.Create(item)); }
                catch (TestFailureException e)
                {
                    throw new TestFailureException($"{Describe(sourcePath)} step #{index + 1}: {e.Message}");
                }
                index++;
            }
        }

        // ---------------------------------------------------------------- suites

        public static TestSuiteScript ParseSuite(string json, string sourcePath = null)
        {
            JsonValue root;
            try { root = JsonValue.Parse(json); }
            catch (JsonParseException e)
            {
                throw new TestFailureException($"{Describe(sourcePath)} is not valid JSON: {e.Message}");
            }

            var suite = new TestSuiteScript
            {
                SourcePath = sourcePath,
                Name = root["name"].AsString(DefaultName(sourcePath)),
                Description = StringOrNull(root["description"]),
                Options = ParseOptions(root["options"], GameTesterSettings.CreateRunOptionsOrDefaults()),
            };

            foreach (var include in root["include"].AsStringList())
                if (!string.IsNullOrWhiteSpace(include)) suite.Include.Add(include.Trim());

            foreach (var tag in root["tags"].AsStringList())
                if (!string.IsNullOrWhiteSpace(tag)) suite.Options.Tags.Add(tag.Trim());

            foreach (var tag in root["excludeTags"].AsStringList())
                if (!string.IsNullOrWhiteSpace(tag)) suite.Options.ExcludeTags.Add(tag.Trim());

            foreach (var category in root["categories"].AsStringList())
            {
                var normalized = TestCategory.Normalize(category);
                if (normalized.Length > 0) suite.Options.Categories.Add(normalized);
            }

            foreach (var category in root["excludeCategories"].AsStringList())
            {
                var normalized = TestCategory.Normalize(category);
                if (normalized.Length > 0) suite.Options.ExcludeCategories.Add(normalized);
            }

            // Fixtures at the suite root rather than inside "options": they are the suite's own steps,
            // and reading them beside "include" is how a person understands what the suite does.
            if (root.Has("beforeEach")) suite.Options.BeforeEachJson = root["beforeEach"].ToJson(false);
            if (root.Has("afterEach")) suite.Options.AfterEachJson = root["afterEach"].ToJson(false);

            return suite;
        }

        /// <summary>Applies an <c>options</c> object onto a base <see cref="RunOptions"/>.</summary>
        public static RunOptions ParseOptions(JsonValue json, RunOptions options)
        {
            options ??= new RunOptions();
            if (json == null || !json.IsObject) return options;

            if (json.Has("nameFilter")) options.NameFilter = json["nameFilter"].AsString("");
            if (json.Has("repeat")) options.RunRepeat = Math.Max(1, json["repeat"].AsInt(1));
            if (json.Has("retries")) options.Retries = Math.Max(0, json["retries"].AsInt(0));
            if (json.Has("stopOnFirstFailure")) options.StopOnFirstFailure = json["stopOnFirstFailure"].AsBool();
            if (json.Has("shuffle")) options.Shuffle = json["shuffle"].AsBool();
            if (json.Has("seed")) options.Seed = json["seed"].AsInt();
            if (json.Has("stepTimeout")) options.DefaultStepTimeout = json["stepTimeout"].AsFloat();
            if (json.Has("locatorTimeout")) options.LocatorTimeout = json["locatorTimeout"].AsFloat();
            if (json.Has("inputSpeed")) options.InputSpeedScale = json["inputSpeed"].AsFloat(1f);
            if (json.Has("timeScale")) options.TimeScale = json["timeScale"].AsFloat();
            if (json.Has("isolateRealDevices")) options.IsolateRealDevices = json["isolateRealDevices"].AsBool();
            if (json.Has("showInputOverlay")) options.ShowInputOverlay = json["showInputOverlay"].AsBool();
            if (json.Has("failOnLogError")) options.FailOnLogError = json["failOnLogError"].AsBool();
            if (json.Has("screenshotOnFailure")) options.ScreenshotOnFailure = json["screenshotOnFailure"].AsBool();
            if (json.Has("screenshotEveryStep")) options.ScreenshotEveryStep = json["screenshotEveryStep"].AsBool();
            if (json.Has("collectPerformance")) options.CollectPerformance = json["collectPerformance"].AsBool();
            if (json.Has("artifactRoot")) options.ArtifactRoot = json["artifactRoot"].AsString("");
            if (json.Has("beforeEach")) options.BeforeEachJson = json["beforeEach"].ToJson(false);
            if (json.Has("afterEach")) options.AfterEachJson = json["afterEach"].ToJson(false);

            if (json.Has("pointer"))
            {
                var pointer = json["pointer"].AsString("mouse");
                options.Pointer = string.Equals(pointer, "touch", StringComparison.OrdinalIgnoreCase)
                    ? PointerMode.Touch
                    : PointerMode.Mouse;
            }

            if (json.Has("backend"))
            {
                var backend = (json["backend"].AsString("auto") ?? "auto").Trim().ToLowerInvariant();
                switch (backend)
                {
                    case "inputsystem": options.Backend = InputBackendKind.InputSystem; break;
                    case "eventsystem": options.Backend = InputBackendKind.EventSystem; break;
                    case "auto": options.Backend = InputBackendKind.Auto; break;
                    default:
                        throw new TestFailureException(
                            $"Unknown backend '{backend}'. Use auto, inputSystem or eventSystem.");
                }
            }

            if (json.Has("ignoreLogs"))
            {
                options.IgnoredLogPatterns.Clear();
                options.IgnoredLogPatterns.AddRange(json["ignoreLogs"].AsStringList());
            }

            if (json.Has("reportFormats"))
            {
                options.ReportFormats.Clear();
                options.ReportFormats.AddRange(json["reportFormats"].AsStringList());
            }

            return options;
        }

        /// <summary>The inverse of <see cref="ParseOptions"/> — used to hand a run request to play mode.</summary>
        public static JsonValue WriteOptions(RunOptions options)
        {
            var json = JsonValue.NewObject()
                .Set("nameFilter", options.NameFilter ?? "")
                .Set("repeat", options.RunRepeat)
                .Set("retries", options.Retries)
                .Set("stopOnFirstFailure", options.StopOnFirstFailure)
                .Set("shuffle", options.Shuffle)
                .Set("seed", options.Seed)
                .Set("stepTimeout", options.DefaultStepTimeout)
                .Set("locatorTimeout", options.LocatorTimeout)
                .Set("inputSpeed", options.InputSpeedScale)
                .Set("timeScale", options.TimeScale)
                .Set("isolateRealDevices", options.IsolateRealDevices)
                .Set("showInputOverlay", options.ShowInputOverlay)
                .Set("failOnLogError", options.FailOnLogError)
                .Set("screenshotOnFailure", options.ScreenshotOnFailure)
                .Set("screenshotEveryStep", options.ScreenshotEveryStep)
                .Set("collectPerformance", options.CollectPerformance)
                .Set("artifactRoot", options.ArtifactRoot ?? "")
                .Set("beforeEach", Steps(options.BeforeEachJson))
                .Set("afterEach", Steps(options.AfterEachJson))
                .Set("pointer", options.Pointer == PointerMode.Touch ? "touch" : "mouse")
                .Set("backend", options.Backend == InputBackendKind.InputSystem ? "inputSystem"
                    : options.Backend == InputBackendKind.EventSystem ? "eventSystem" : "auto");

            var ignore = JsonValue.NewArray();
            foreach (var pattern in options.IgnoredLogPatterns) ignore.Add(JsonValue.New(pattern));
            json.Set("ignoreLogs", ignore);

            var formats = JsonValue.NewArray();
            foreach (var format in options.ReportFormats) formats.Add(JsonValue.New(format));
            json.Set("reportFormats", formats);

            return json;
        }

        /// <summary>Serialises a whole run request (filters + options) for the play-mode handoff.</summary>
        public static JsonValue WriteRunRequest(RunOptions options)
        {
            var request = JsonValue.NewObject()
                .Set("nameFilter", options.NameFilter ?? "")
                .Set("options", WriteOptions(options));

            var tags = JsonValue.NewArray();
            foreach (var tag in options.Tags) tags.Add(JsonValue.New(tag));
            request.Set("tags", tags);

            var categories = JsonValue.NewArray();
            foreach (var category in options.Categories) categories.Add(JsonValue.New(category));
            request.Set("categories", categories);

            var excludeCategories = JsonValue.NewArray();
            foreach (var category in options.ExcludeCategories) excludeCategories.Add(JsonValue.New(category));
            request.Set("excludeCategories", excludeCategories);

            var paths = JsonValue.NewArray();
            foreach (var path in options.Paths) paths.Add(JsonValue.New(path));
            request.Set("paths", paths);

            return request;
        }

        // ---------------------------------------------------------------- validation

        /// <summary>
        /// Parses without running and returns human-readable problems. This is what
        /// <c>GameTestKit.Editor.AICommands.Validate</c> exposes, so an agent gets its script checked
        /// before entering play mode.
        /// </summary>
        public static List<string> Validate(string json, string sourcePath = null)
        {
            var problems = new List<string>();
            try
            {
                var test = ParseTest(json, sourcePath);
                if (string.IsNullOrWhiteSpace(test.Name)) problems.Add("The test has no name.");
                if (test.Steps.Count == 0) problems.Add("The test has no steps.");
            }
            catch (TestFailureException e) { problems.Add(e.Message); }
            catch (Exception e) { problems.Add($"{e.GetType().Name}: {e.Message}"); }
            return problems;
        }

        // ---------------------------------------------------------------- helpers

        private static string StringOrNull(JsonValue value) => value.IsNull ? null : value.AsString();

        /// <summary>
        /// A stored fixture back as JSON. Fixtures travel as text, so writing them into the run request
        /// means re-parsing them rather than quoting them into a string field.
        /// </summary>
        private static JsonValue Steps(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return JsonValue.NewArray();

            try
            {
                var parsed = JsonValue.Parse(json);
                return parsed.IsArray ? parsed : JsonValue.NewArray();
            }
            catch (JsonParseException)
            {
                // Reported properly when the runner parses it; silently dropping it here would hide it.
                return JsonValue.NewArray();
            }
        }

        /// <summary>
        /// Parses a fixture into steps. Returns an empty list for an empty fixture, and throws with the
        /// fixture named when it does not parse.
        /// </summary>
        public static List<TestStep> ParseFixture(string json, string what)
        {
            var steps = new List<TestStep>();
            if (string.IsNullOrWhiteSpace(json)) return steps;

            JsonValue root;
            try { root = JsonValue.Parse(json); }
            catch (JsonParseException e)
            {
                throw new TestFailureException($"The {what} fixture is not valid JSON: {e.Message}");
            }

            if (!root.IsArray)
                throw new TestFailureException($"The {what} fixture must be an array of steps.");

            int index = 0;
            foreach (var item in root)
            {
                try { steps.Add(StepRegistry.Create(item)); }
                catch (TestFailureException e)
                {
                    throw new TestFailureException($"The {what} fixture, step #{index + 1}: {e.Message}");
                }
                index++;
            }

            return steps;
        }

        /// <summary>Derives a readable test name from a file path, e.g. <c>buy-a-sword.gametest.json</c> → "buy a sword".</summary>
        public static string DefaultName(string sourcePath)
        {
            if (string.IsNullOrEmpty(sourcePath)) return "Untitled test";

            var fileName = Path.GetFileName(sourcePath);
            if (fileName.EndsWith(TestExtension, StringComparison.OrdinalIgnoreCase))
                fileName = fileName.Substring(0, fileName.Length - TestExtension.Length);
            else if (fileName.EndsWith(SuiteExtension, StringComparison.OrdinalIgnoreCase))
                fileName = fileName.Substring(0, fileName.Length - SuiteExtension.Length);
            else
                fileName = Path.GetFileNameWithoutExtension(fileName);

            return fileName.Replace('-', ' ').Replace('_', ' ').Trim();
        }

        private static string Describe(string sourcePath) =>
            string.IsNullOrEmpty(sourcePath) ? "The test script" : $"'{sourcePath}'";
    }
}
