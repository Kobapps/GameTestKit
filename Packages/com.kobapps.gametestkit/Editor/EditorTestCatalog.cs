using System.Collections.Generic;
using System.IO;
using Kobapps.GameTestKit.Scripting;
using UnityEditor;
using UnityEngine;

namespace Kobapps.GameTestKit.Editor
{
    /// <summary>
    /// Teaches the runtime catalogue to find scripts through the AssetDatabase, so tests can live
    /// anywhere in the project during development instead of being confined to Resources.
    /// </summary>
    [InitializeOnLoad]
    public static class EditorTestCatalog
    {
        static EditorTestCatalog()
        {
            GameTestCatalog.EditorDiscovery = Discover;
        }

        public static IEnumerable<GameTestSource> Discover()
        {
            var settings = GameTesterSettings.Instance;
            var results = new List<GameTestSource>();
            var folders = new List<string>();

            foreach (var folder in settings.TestFolders)
                if (!string.IsNullOrWhiteSpace(folder) && AssetDatabase.IsValidFolder(folder.TrimEnd('/')))
                    folders.Add(folder.TrimEnd('/'));

            // Nothing configured (or the folder doesn't exist yet): scan the whole project rather than
            // silently finding nothing — a first-time user should not have to configure anything.
            var guids = folders.Count > 0
                ? AssetDatabase.FindAssets("t:TextAsset", folders.ToArray())
                : AssetDatabase.FindAssets("t:TextAsset");

            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                bool isSuite = path.EndsWith(TestScriptParser.SuiteExtension, System.StringComparison.OrdinalIgnoreCase);
                bool isTest = path.EndsWith(TestScriptParser.TestExtension, System.StringComparison.OrdinalIgnoreCase);
                if (!isSuite && !isTest) continue;

                string json;
                try { json = File.ReadAllText(path); }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[GameTestKit] Could not read '{path}': {e.Message}");
                    continue;
                }

                results.Add(new GameTestSource
                {
                    Path = path,
                    Json = json,
                    IsSuite = isSuite,
                    Origin = "Assets",
                });
            }

            return results;
        }

        /// <summary>All parsed tests plus the scripts that failed to parse, for the window and the CLI.</summary>
        public static List<GameTest> LoadAll(out List<string> errors)
        {
            errors = new List<string>();
            return GameTestCatalog.DiscoverTests(new RunOptions(), errors);
        }
    }
}
