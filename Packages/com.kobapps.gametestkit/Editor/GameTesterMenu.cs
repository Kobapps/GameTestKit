using System;
using System.IO;
using Kobapps.GameTestKit.Scripting;
using UnityEditor;
using UnityEngine;

namespace Kobapps.GameTestKit.Editor
{
    /// <summary>Menu commands: create scripts and the settings asset, install the AI skill.</summary>
    public static class GameTesterMenu
    {
        private const string SettingsAssetPath = "Assets/Resources/GameTesterSettings.asset";

        private const string TestTemplate = @"{
  ""name"": ""NAME"",
  ""description"": ""What this flow proves."",
  ""tags"": [""smoke""],

  // Scene to load first. Remove this line to test whatever is already open.
  ""scene"": ""SCENE"",

  ""steps"": [
    { ""waitForVisible"": ""text:Play"" },
    { ""click"": ""text:Play"" },
    { ""waitForScene"": ""Gameplay"" },
    { ""screenshot"": ""gameplay-started"" }
  ]
}
";

        private const string SuiteTemplate = @"{
  ""name"": ""Smoke"",
  ""description"": ""The set that must pass before anything ships."",
  ""tags"": [""smoke""],
  ""options"": {
    ""stopOnFirstFailure"": false,
    ""retries"": 1,
    ""pointer"": ""mouse"",
    ""screenshotOnFailure"": true,
    ""reportFormats"": [""junit"", ""json"", ""html""]
  }
}
";

        [MenuItem("Tools/GameTestKit/New Test Script", priority = 20)]
        public static void CreateTestScript() => CreateTestScript(null);

        /// <summary>
        /// Creates a template script inside <paramref name="category"/> — the folder <em>is</em> the
        /// category, so nothing has to be written into the file for it to land in the right group.
        /// </summary>
        /// <returns>The asset path of the new script.</returns>
        public static string CreateTestScript(string category)
        {
            var folder = CategoryFolder(category);
            var path = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(folder, "new-flow.gametest.json"));

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            var content = TestTemplate
                .Replace("NAME", Path.GetFileName(path).Replace(".gametest.json", "").Replace('-', ' '))
                .Replace("SCENE", string.IsNullOrEmpty(scene) ? "MainMenu" : scene);

            Write(path, content);
            return path.Replace('\\', '/');
        }

        [MenuItem("Tools/GameTestKit/New Suite", priority = 21)]
        public static void CreateSuite()
        {
            var path = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(DefaultFolder(), "smoke.gamesuite.json"));
            Write(path, SuiteTemplate);
        }

        private static void Write(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, content);
            AssetDatabase.ImportAsset(path);

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
            Debug.Log($"[GameTestKit] Created {path}");
        }

        private static string DefaultFolder()
        {
            var settings = GameTesterSettings.Instance;
            var folder = settings.TestFolders.Count > 0 ? settings.TestFolders[0] : "Assets/GameTests";

            if (!AssetDatabase.IsValidFolder(folder))
            {
                Directory.CreateDirectory(folder);
                AssetDatabase.Refresh();
            }
            return folder;
        }

        // ---------------------------------------------------------------- categories

        /// <summary>
        /// The folder backing a category, created if it does not exist. An empty category is the test
        /// root itself.
        /// </summary>
        public static string CategoryFolder(string category)
        {
            var root = DefaultFolder();
            var normalized = TestCategory.Normalize(category);
            if (normalized.Length == 0) return root;

            var folder = root;

            // One segment at a time, so Unity registers each new folder rather than finding a directory
            // on disk it has never imported.
            foreach (var segment in TestCategory.Segments(normalized))
            {
                var child = $"{folder}/{Sanitize(segment)}";

                if (!AssetDatabase.IsValidFolder(child))
                    AssetDatabase.CreateFolder(folder, Sanitize(segment));

                folder = child;
            }

            return folder;
        }

        /// <summary>Creates the folder for a category so it shows up in the window while still empty.</summary>
        public static string CreateCategory(string category)
        {
            var folder = CategoryFolder(category);
            AssetDatabase.Refresh();
            Debug.Log($"[GameTestKit] Category '{TestCategory.Display(category)}' is {folder}");
            return folder;
        }

        /// <summary>
        /// Moves a script into the folder for <paramref name="category"/>, which is what re-categorises
        /// it. Returns the new path, or null when Unity refused the move.
        /// </summary>
        /// <remarks>
        /// <see cref="AssetDatabase.MoveAsset"/> rather than a file move: it carries the <c>.meta</c>
        /// file across, so the asset keeps its GUID and anything referencing it keeps working.
        /// </remarks>
        public static string MoveTestToCategory(string assetPath, string category)
        {
            if (string.IsNullOrEmpty(assetPath)) return null;

            var folder = CategoryFolder(category);
            var target = $"{folder}/{Path.GetFileName(assetPath)}";

            if (string.Equals(target, assetPath.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                return assetPath;

            target = AssetDatabase.GenerateUniqueAssetPath(target);

            var error = AssetDatabase.MoveAsset(assetPath, target);
            if (!string.IsNullOrEmpty(error))
            {
                Debug.LogError($"[GameTestKit] Could not move '{assetPath}' to '{folder}': {error}");
                return null;
            }

            AssetDatabase.Refresh();
            Debug.Log($"[GameTestKit] Moved {Path.GetFileName(assetPath)} to {TestCategory.Display(category)}");
            return target;
        }

        /// <summary>Strips what a folder name cannot contain, so a typed category never fails the move.</summary>
        private static string Sanitize(string segment)
        {
            foreach (var invalid in Path.GetInvalidFileNameChars())
                segment = segment.Replace(invalid, '-');
            return segment.Trim();
        }

        [MenuItem("Tools/GameTestKit/Settings", priority = 40)]
        public static void OpenSettings()
        {
            var settings = AssetDatabase.LoadAssetAtPath<GameTesterSettings>(SettingsAssetPath);
            if (settings == null)
            {
                Directory.CreateDirectory("Assets/Resources");
                settings = ScriptableObject.CreateInstance<GameTesterSettings>();
                AssetDatabase.CreateAsset(settings, SettingsAssetPath);
                AssetDatabase.SaveAssets();
                Debug.Log($"[GameTestKit] Created {SettingsAssetPath}");
            }

            GameTesterSettings.Instance = settings;
            Selection.activeObject = settings;
            EditorGUIUtility.PingObject(settings);
        }

        /// <summary>
        /// Mirrors the authored scripts into Resources so they ship inside player builds.
        /// </summary>
        /// <remarks>
        /// <c>Resources.LoadAll</c> reports an asset's name and nothing about the folder it came from, so
        /// a copied script would lose the category its folder gave it. Each copy therefore gets its
        /// category (and name) written into the JSON, and a file name qualified by the category so two
        /// <c>smoke.gametest.json</c> files in different folders cannot overwrite each other in the flat
        /// mirror.
        /// </remarks>
        [MenuItem("Tools/GameTestKit/Copy Tests To Resources", priority = 41)]
        public static void CopyTestsToResources()
        {
            var settings = GameTesterSettings.Instance;
            var target = Path.Combine("Assets/Resources", settings.RuntimeResourcesFolder).Replace('\\', '/');
            Directory.CreateDirectory(target);

            int copied = 0;
            foreach (var source in EditorTestCatalog.Discover())
            {
                if (source.Path.Replace('\\', '/').StartsWith(target, StringComparison.OrdinalIgnoreCase))
                    continue;

                var category = TestCategory.FromSourcePath(source.Path);
                var fileName = Path.GetFileName(source.Path);
                var json = source.Json;

                if (!source.IsSuite)
                {
                    try
                    {
                        var root = JsonValue.Parse(source.Json);
                        if (root.IsObject)
                        {
                            if (!root.Has("name")) root.Set("name", TestScriptParser.DefaultName(source.Path));
                            if (!root.Has("category") && category.Length > 0) root.Set("category", category);
                            json = root.ToJson();
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning($"[GameTestKit] Copying '{source.Path}' verbatim — it could not be " +
                                         $"parsed to stamp its category: {e.Message}");
                    }
                }

                if (category.Length > 0)
                    fileName = category.Replace(TestCategory.Separator, '.').ToLowerInvariant() + "." + fileName;

                File.WriteAllText(Path.Combine(target, fileName), json);
                copied++;
            }

            AssetDatabase.Refresh();
            Debug.Log($"[GameTestKit] Copied {copied} script(s) to {target} — they will now ship inside player " +
                      "builds, each carrying its category. Copies of tests you have since renamed or deleted " +
                      "are not cleaned up; clear the folder first for an exact mirror.");
        }

        private const string OverlayMenu = "Tools/GameTestKit/Input Overlay";

        /// <summary>
        /// Shows/hides the on-screen input overlay. The same switch as F9 during a run, for when the
        /// run isolates real devices and the hotkey therefore cannot reach the game.
        /// </summary>
        [MenuItem(OverlayMenu, priority = 42)]
        public static void ToggleInputOverlay()
        {
            InputOverlay.Toggle();
            Menu.SetChecked(OverlayMenu, InputOverlay.Enabled);
            Debug.Log($"[GameTestKit] Input overlay {(InputOverlay.Enabled ? "on" : "off")}.");
        }

        [MenuItem(OverlayMenu, true)]
        private static bool ToggleInputOverlayValidate()
        {
            Menu.SetChecked(OverlayMenu, InputOverlay.Enabled);
            return true;
        }

        [MenuItem("Tools/GameTestKit/Print Step Catalogue", priority = 60)]
        public static void PrintCatalogue()
        {
            Debug.Log("[GameTestKit] Step catalogue:\n" + StepRegistry.DescribeCatalogue().ToJson());
        }
    }
}
