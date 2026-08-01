using System.IO;
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
        public static void CreateTestScript()
        {
            var folder = DefaultFolder();
            var path = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(folder, "new-flow.gametest.json"));

            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            var content = TestTemplate
                .Replace("NAME", Path.GetFileName(path).Replace(".gametest.json", "").Replace('-', ' '))
                .Replace("SCENE", string.IsNullOrEmpty(scene) ? "MainMenu" : scene);

            Write(path, content);
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

        [MenuItem("Tools/GameTestKit/Copy Tests To Resources", priority = 41)]
        public static void CopyTestsToResources()
        {
            var settings = GameTesterSettings.Instance;
            var target = Path.Combine("Assets/Resources", settings.RuntimeResourcesFolder);
            Directory.CreateDirectory(target);

            int copied = 0;
            foreach (var source in EditorTestCatalog.Discover())
            {
                if (source.Path.StartsWith(target)) continue;
                File.WriteAllText(Path.Combine(target, Path.GetFileName(source.Path)), source.Json);
                copied++;
            }

            AssetDatabase.Refresh();
            Debug.Log($"[GameTestKit] Copied {copied} script(s) to {target} — they will now ship inside player builds.");
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
