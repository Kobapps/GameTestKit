using System;
using System.Collections.Generic;
using System.IO;
using Kobapps.GameTestKit.Scripting;
using UnityEngine;

namespace Kobapps.GameTestKit
{
    /// <summary>One discovered script file, before it is parsed.</summary>
    public sealed class GameTestSource
    {
        public string Path;
        public string Json;
        public bool IsSuite;
        public string Origin;   // Assets | Resources | StreamingAssets | File

        public override string ToString() => Path;
    }

    /// <summary>
    /// Finds test scripts. The same catalogue serves the Editor, a built player and CI, so a suite that
    /// passes on a developer machine is literally the same suite that runs on device.
    /// </summary>
    public static class GameTestCatalog
    {
        /// <summary>
        /// Set by the Editor assembly to add AssetDatabase-backed discovery (which can see files that
        /// are neither in Resources nor StreamingAssets). Null in players.
        /// </summary>
        public static Func<IEnumerable<GameTestSource>> EditorDiscovery;

        /// <summary>Extra scripts registered from code, e.g. by a test bootstrap.</summary>
        public static readonly List<GameTestSource> Registered = new List<GameTestSource>();

        public static List<GameTestSource> DiscoverSources(RunOptions options = null)
        {
            var settings = GameTesterSettings.Instance;
            var sources = new List<GameTestSource>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(GameTestSource source)
            {
                if (source == null || string.IsNullOrEmpty(source.Json)) return;
                if (!seen.Add(NormalizeKey(source.Path))) return;
                sources.Add(source);
            }

            // 1. Explicit paths always win and skip discovery entirely.
            if (options != null && options.Paths.Count > 0)
            {
                foreach (var path in options.Paths)
                {
                    var source = LoadFile(path);
                    if (source != null) Add(source);
                    else Debug.LogWarning($"[GameTestKit] Test script not found: {path}");
                }
                return sources;
            }

            foreach (var source in Registered) Add(source);

            // 2. Editor: the authored files under the configured folders.
            if (EditorDiscovery != null)
            {
                foreach (var source in EditorDiscovery()) Add(source);
            }

            // 3. Resources: works in built players. Skipped in the Editor, where the AssetDatabase pass
            // above has already seen these files at their real paths — and a real path is what a
            // category is derived from, which Resources.LoadAll cannot give us.
            if (EditorDiscovery == null && !string.IsNullOrEmpty(settings.RuntimeResourcesFolder))
            {
                foreach (var asset in Resources.LoadAll<TextAsset>(settings.RuntimeResourcesFolder))
                {
                    if (asset == null) continue;
                    var name = asset.name;
                    bool isSuite = name.EndsWith(".gamesuite", StringComparison.OrdinalIgnoreCase);
                    bool isTest = name.EndsWith(".gametest", StringComparison.OrdinalIgnoreCase);
                    if (!isSuite && !isTest) continue;

                    Add(new GameTestSource
                    {
                        Path = $"Resources/{settings.RuntimeResourcesFolder}/{name}.json",
                        Json = asset.text,
                        IsSuite = isSuite,
                        Origin = "Resources",
                    });
                }
            }

            // 4. StreamingAssets: lets QA drop scripts next to a build without rebuilding.
            if (!string.IsNullOrEmpty(settings.StreamingAssetsFolder))
            {
                var folder = Path.Combine(Application.streamingAssetsPath, settings.StreamingAssetsFolder);
                foreach (var source in LoadFolder(folder, "StreamingAssets")) Add(source);
            }

            return sources;
        }

        /// <summary>Discovers and parses every test, collecting broken scripts into <paramref name="errors"/>.</summary>
        public static List<GameTest> DiscoverTests(RunOptions options, List<string> errors = null)
        {
            var tests = new List<GameTest>();

            // Category + name, not the file name: two folders may each hold a "smoke" test, and both
            // are real. What must not appear twice is one logical test found through two origins — the
            // Resources copy of a script that StreamingAssets also carries, say — so the later origin
            // replaces the earlier one, which is what makes a StreamingAssets drop an override.
            var byIdentity = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var source in DiscoverSources(options))
            {
                if (source.IsSuite) continue;

                GameTest test;
                try { test = TestScriptParser.ParseTest(source.Json, source.Path); }
                catch (Exception e)
                {
                    var message = $"{source.Path}: {e.Message}";
                    errors?.Add(message);
                    Debug.LogError($"[GameTestKit] {message}");
                    continue;
                }

                var identity = $"{TestCategory.Normalize(test.Category)}/{test.Name}";

                if (byIdentity.TryGetValue(identity, out var existing))
                {
                    // Say so. Collapsing two origins of one test is the intent, but when the paths
                    // differ it is just as likely to be a stale copy somebody left behind — and
                    // silently running the wrong one of those costs an afternoon.
                    var previous = tests[existing];
                    if (!string.Equals(previous.SourcePath, test.SourcePath, StringComparison.OrdinalIgnoreCase))
                        Debug.LogWarning(
                            $"[GameTestKit] Two tests are both '{test.Name}' in category " +
                            $"'{TestCategory.Display(test.Category)}'. Running '{test.SourcePath}' and " +
                            $"ignoring '{previous.SourcePath}'. Delete one, rename one, or move one to " +
                            "another category.");

                    tests[existing] = test;
                }
                else
                {
                    byIdentity[identity] = tests.Count;
                    tests.Add(test);
                }
            }

            // Category first so the list reads as a folder tree even before the window groups it.
            tests.Sort((a, b) =>
            {
                int byCategory = TestCategory.Compare(a.Category, b.Category);
                return byCategory != 0 ? byCategory : string.CompareOrdinal(a.Name, b.Name);
            });

            return tests;
        }

        /// <summary>Every category that has at least one test in it, parents included, in tree order.</summary>
        public static List<string> DiscoverCategories(IEnumerable<GameTest> tests)
        {
            var all = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var test in tests)
                foreach (var category in TestCategory.SelfAndAncestors(test.Category))
                    all.Add(category);

            var ordered = new List<string>(all);
            ordered.Sort(TestCategory.Compare);
            return ordered;
        }

        /// <summary>Discovers suites (options + include lists).</summary>
        public static List<TestSuiteScript> DiscoverSuites(List<string> errors = null)
        {
            var suites = new List<TestSuiteScript>();

            foreach (var source in DiscoverSources())
            {
                if (!source.IsSuite) continue;
                try { suites.Add(TestScriptParser.ParseSuite(source.Json, source.Path)); }
                catch (Exception e)
                {
                    var message = $"{source.Path}: {e.Message}";
                    errors?.Add(message);
                    Debug.LogError($"[GameTestKit] {message}");
                }
            }

            return suites;
        }

        /// <summary>Reads one script file from disk.</summary>
        public static GameTestSource LoadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return new GameTestSource
                {
                    Path = path.Replace('\\', '/'),
                    Json = File.ReadAllText(path),
                    IsSuite = path.EndsWith(TestScriptParser.SuiteExtension, StringComparison.OrdinalIgnoreCase),
                    Origin = "File",
                };
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[GameTestKit] Could not read '{path}': {e.Message}");
                return null;
            }
        }

        public static IEnumerable<GameTestSource> LoadFolder(string folder, string origin)
        {
            List<GameTestSource> results = new List<GameTestSource>();
            try
            {
                if (!Directory.Exists(folder)) return results;

                foreach (var file in Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories))
                {
                    bool isSuite = file.EndsWith(TestScriptParser.SuiteExtension, StringComparison.OrdinalIgnoreCase);
                    bool isTest = file.EndsWith(TestScriptParser.TestExtension, StringComparison.OrdinalIgnoreCase);
                    if (!isSuite && !isTest) continue;

                    results.Add(new GameTestSource
                    {
                        Path = file.Replace('\\', '/'),
                        Json = File.ReadAllText(file),
                        IsSuite = isSuite,
                        Origin = origin,
                    });
                }
            }
            catch (Exception e)
            {
                // Compressed/streamed platforms (Android, WebGL) cannot enumerate StreamingAssets.
                Debug.Log($"[GameTestKit] Skipping '{folder}': {e.Message}");
            }
            return results;
        }

        /// <summary>
        /// Identifies a file so the same one is not added twice. The whole path, not just the file
        /// name: with categories, <c>Shop/smoke.gametest.json</c> and <c>Onboarding/smoke.gametest.json</c>
        /// are two different tests. Duplicates that survive this are collapsed by category and name in
        /// <see cref="DiscoverTests"/>, which can only be done once the scripts are parsed.
        /// </summary>
        private static string NormalizeKey(string path)
        {
            if (string.IsNullOrEmpty(path)) return Guid.NewGuid().ToString();
            return path.Replace('\\', '/').ToLowerInvariant();
        }
    }
}
