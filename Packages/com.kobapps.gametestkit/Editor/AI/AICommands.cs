using System;
using System.Collections.Generic;
using System.IO;
using Kobapps.GameTestKit.Scripting;
using UnityEditor;
using UnityEngine;

namespace Kobapps.GameTestKit.Editor
{
    /// <summary>
    /// The machine-facing surface: four commands an agent can call to author and debug tests without a
    /// human in the loop — learn the vocabulary, see the screen, check a script, run it and read results.
    /// </summary>
    /// <remarks>
    /// Each is an <c>-executeMethod</c> entry point that emits JSON to <c>-gtk-out &lt;path&gt;</c> (or the
    /// log). They are also on the <c>Tools ▸ GameTestKit ▸ AI</c> menu for interactive use.
    /// <code>
    /// Unity -batchmode -projectPath . -executeMethod Kobapps.GameTestKit.Editor.AICommands.Catalogue -gtk-out cat.json -quit
    /// Unity -batchmode -projectPath . -executeMethod Kobapps.GameTestKit.Editor.AICommands.Validate  -gtk-test Assets/GameTests/x.gametest.json -quit
    /// Unity -projectPath .            -executeMethod Kobapps.GameTestKit.Editor.AICommands.Inspect   -gtk-scene MainMenu -gtk-delay 2 -gtk-out screen.json
    /// </code>
    /// </remarks>
    [InitializeOnLoad]
    public static class AICommands
    {
        private const string InspectPendingKey = "GameTestKit.InspectPending";
        private const string InspectOutputKey = "GameTestKit.InspectOutput";

        static AICommands()
        {
            if (SessionState.GetBool(InspectPendingKey, false))
                EditorApplication.update += PollInspect;
        }

        // ================================================================ catalogue

        /// <summary>
        /// Everything the script format supports, generated from the live registry: step verbs and
        /// their parameters, the locator language, the expression language, and the game's own bindings.
        /// An agent that reads this can write a valid script without guessing.
        /// </summary>
        public static JsonValue BuildCatalogue()
        {
            var root = JsonValue.NewObject()
                .Set("format", "gametest/1")
                .Set("testFileExtension", TestScriptParser.TestExtension)
                .Set("suiteFileExtension", TestScriptParser.SuiteExtension);

            root.Set("steps", StepRegistry.DescribeCatalogue());

            var locators = JsonValue.NewArray();
            void AddLocator(string syntax, string meaning) =>
                locators.Add(JsonValue.NewObject().Set("syntax", syntax).Set("meaning", meaning));

            AddLocator("id:play_button", "A TestId component. The most stable selector — prefer it.");
            AddLocator("#PlayButton", "GameObject by exact name.");
            AddLocator("Canvas/Menu/Play", "Hierarchy path; a trailing part of the path is enough.");
            AddLocator("text:Play", "Visible label contains 'Play' (case-insensitive).");
            AddLocator("text:\"Play\"", "Visible label is exactly 'Play'.");
            AddLocator("tag:Enemy", "Unity tag.");
            AddLocator("type:Button", "Has a component of that type (short or full name).");
            AddLocator("pos:0.5,0.5", "Normalised screen point — no object needed.");
            AddLocator("screen:640,360", "Absolute screen pixels.");
            AddLocator("world:1,0,3", "World position projected through the main camera.");
            AddLocator("type:Button[2]", "The third match (indices are zero-based).");
            AddLocator("id:shop >> text:Buy", "Scope a search to descendants of an earlier match.");
            root.Set("locators", locators);

            var expressions = JsonValue.NewObject()
                .Set("operators", "and or not, == != > >= < <=, contains startsWith endsWith matches, + - * /, ()")
                .Set("functions", "exists(sel) count(sel) visible(sel) interactable(sel) blocked(sel) " +
                                  "text(sel) label(sel) sceneLoaded(name) abs min max round len")
                .Set("builtinValues", "scene time realtime timeScale frameCount fps screen.width screen.height")
                .Set("example", "player.gold >= 100 and visible('id:shop_panel')");
            root.Set("expressions", expressions);

            var bindings = JsonValue.NewArray();
            foreach (var binding in GameTestBindings.Describe())
            {
                var item = JsonValue.NewObject()
                    .Set("path", binding.Path)
                    .Set("kind", binding.Kind == BindingKind.Value ? "value" : "action");
                if (!string.IsNullOrEmpty(binding.Description)) item.Set("description", binding.Description);
                bindings.Add(item);
            }
            root.Set("bindings", bindings);
            root.Set("bindingsNote",
                "Bindings are registered by the game at runtime; this list is only complete while the " +
                "game is playing. Use the Inspect command to see them live.");

            var testIds = JsonValue.NewArray();
            foreach (var id in CollectTestIdsInProject()) testIds.Add(JsonValue.New(id));
            root.Set("testIdsInScenes", testIds);

            var scenes = JsonValue.NewArray();
            foreach (var scene in EditorBuildSettings.scenes)
                if (scene.enabled)
                    scenes.Add(JsonValue.New(Path.GetFileNameWithoutExtension(scene.path)));
            root.Set("buildScenes", scenes);

            return root;
        }

        [MenuItem("Tools/GameTestKit/AI/Write Catalogue To Console", priority = 80)]
        public static void Catalogue()
        {
            GameTesterCLI.Emit(BuildCatalogue(), "-gtk-out");
        }

        private static IEnumerable<string> CollectTestIdsInProject()
        {
            var ids = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var component in UnityEngine.Object.FindObjectsByType<TestId>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (component != null) ids.Add(component.Id);

            foreach (var guid in AssetDatabase.FindAssets("t:Prefab"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) continue;

                foreach (var component in prefab.GetComponentsInChildren<TestId>(true))
                    if (component != null) ids.Add(component.Id);
            }

            return ids;
        }

        // ================================================================ validate

        /// <summary>
        /// Parses scripts and reports problems without entering play mode — the fast feedback loop an
        /// agent needs after writing a file.
        /// </summary>
        public static void Validate()
        {
            var paths = new List<string>();
            var args = Environment.GetCommandLineArgs();

            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], "-gtk-test", StringComparison.OrdinalIgnoreCase))
                    paths.Add(args[i + 1]);

            if (paths.Count == 0)
                foreach (var source in EditorTestCatalog.Discover())
                    if (!source.IsSuite) paths.Add(source.Path);

            var root = JsonValue.NewObject();
            var results = JsonValue.NewArray();
            int broken = 0;

            foreach (var path in paths)
            {
                var item = JsonValue.NewObject().Set("path", path);
                var problems = JsonValue.NewArray();

                if (!File.Exists(path))
                {
                    problems.Add(JsonValue.New("File not found."));
                }
                else
                {
                    foreach (var problem in TestScriptParser.Validate(File.ReadAllText(path), path))
                        problems.Add(JsonValue.New(problem));
                }

                item.Set("valid", problems.Count == 0);
                item.Set("problems", problems);
                if (problems.Count > 0) broken++;

                results.Add(item);
            }

            root.Set("checked", paths.Count).Set("invalid", broken).Set("results", results);
            GameTesterCLI.Emit(root, "-gtk-out");

            if (Application.isBatchMode) EditorApplication.Exit(broken == 0 ? 0 : 1);
        }

        [MenuItem("Tools/GameTestKit/AI/Validate All Scripts", priority = 81)]
        public static void ValidateFromMenu()
        {
            var root = JsonValue.NewObject();
            var results = JsonValue.NewArray();
            int broken = 0;

            foreach (var source in EditorTestCatalog.Discover())
            {
                if (source.IsSuite) continue;

                var problems = TestScriptParser.Validate(source.Json, source.Path);
                if (problems.Count == 0) continue;

                broken++;
                var item = JsonValue.NewObject().Set("path", source.Path);
                var array = JsonValue.NewArray();
                foreach (var problem in problems) array.Add(JsonValue.New(problem));
                item.Set("problems", array);
                results.Add(item);
            }

            root.Set("invalid", broken).Set("results", results);

            if (broken == 0) Debug.Log("[GameTestKit] All test scripts parse cleanly.");
            else Debug.LogError("[GameTestKit] Invalid scripts:\n" + root.ToJson());
        }

        // ================================================================ inspect

        /// <summary>
        /// Enters play mode, optionally loads a scene, waits for it to settle and writes a snapshot of
        /// everything on screen. This is how an agent discovers real selectors instead of guessing.
        /// </summary>
        public static void Inspect()
        {
            var args = Environment.GetCommandLineArgs();
            string scene = null, output = null;
            float delay = 1.5f;
            bool includeHidden = false;

            for (int i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : null;
                switch (args[i].ToLowerInvariant())
                {
                    case "-gtk-scene": scene = Next(); break;
                    case "-gtk-delay":
                        float.TryParse(Next(), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out delay);
                        break;
                    case "-gtk-out": output = Next(); break;
                    case "-gtk-include-hidden": includeHidden = true; break;
                }
            }

            RequestInspect(scene, delay, output, includeHidden);
        }

        [MenuItem("Tools/GameTestKit/AI/Inspect Current Scene", priority = 82)]
        public static void InspectFromMenu()
        {
            if (Application.isPlaying)
            {
                var snapshot = GameTester.Inspect();
                var path = GameTester.DefaultInspectPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, snapshot.ToJson().ToJson());
                EditorGUIUtility.systemCopyBuffer = snapshot.ToText();
                Debug.Log($"[GameTestKit] Snapshot copied to the clipboard and written to {path}\n{snapshot.ToText()}");
                return;
            }

            RequestInspect(null, 1.5f, null, false);
        }

        private static void RequestInspect(string scene, float delay, string output, bool includeHidden)
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogWarning("[GameTestKit] Already in play mode — use Inspect Current Scene instead.");
                return;
            }

            output = string.IsNullOrEmpty(output) ? GameTester.DefaultInspectPath : Path.GetFullPath(output);

            var request = JsonValue.NewObject()
                .Set("command", "inspect")
                .Set("delay", delay)
                .Set("includeHidden", includeHidden)
                .Set("output", output.Replace('\\', '/'));

            if (!string.IsNullOrEmpty(scene)) request.Set("scene", scene);

            try
            {
                var path = GameTester.PendingRunPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, request.ToJson());
                if (File.Exists(output)) File.Delete(output);
            }
            catch (Exception e)
            {
                Debug.LogError($"[GameTestKit] Could not queue the inspect request: {e.Message}");
                if (Application.isBatchMode) EditorApplication.Exit(2);
                return;
            }

            SessionState.SetBool(InspectPendingKey, true);
            SessionState.SetString(InspectOutputKey, output);

            EditorApplication.update -= PollInspect;
            EditorApplication.update += PollInspect;
            EditorApplication.EnterPlaymode();
        }

        private static void PollInspect()
        {
            if (!SessionState.GetBool(InspectPendingKey, false))
            {
                EditorApplication.update -= PollInspect;
                return;
            }

            var output = SessionState.GetString(InspectOutputKey, "");
            if (string.IsNullOrEmpty(output) || !File.Exists(output)) return;

            SessionState.EraseBool(InspectPendingKey);
            SessionState.EraseString(InspectOutputKey);
            EditorApplication.update -= PollInspect;

            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;

            Debug.Log($"[GameTestKit] Scene snapshot ready: {output}");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
    }
}
