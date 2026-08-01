using System;
using System.IO;
using System.Text;
using Kobapps.GameTestKit.Scripting;
using UnityEditor;
using UnityEngine;

namespace Kobapps.GameTestKit.Editor
{
    /// <summary>
    /// Generates and installs an AI authoring skill (<c>SKILL.md</c>) describing how to write, validate,
    /// run and debug GameTestKit tests <em>in this project</em>.
    /// </summary>
    /// <remarks>
    /// The prose comes from <c>SkillBody.txt</c>; the reference section is generated live from the step
    /// registry, the build scene list and the test ids present in the project — so custom steps a game
    /// registers, and ids a designer added this morning, are documented automatically instead of going
    /// stale in a hand-written file.
    /// </remarks>
    public static class GameTesterSkillGenerator
    {
        public const string SkillName = "gametestkit-author";

        private const string BodyAssetPath =
            "Packages/com.kobapps.gametestkit/Editor/AISkills/SkillBody.txt";

        private const string Description =
            "Author, validate, run and debug end-to-end GameTestKit tests for this Unity game — " +
            "scripts that drive real gameplay with simulated clicks, taps, drags, keys and gamepad " +
            "input. Use when writing or fixing automated game tests, reproducing a bug as a test, or " +
            "adding a flow to the smoke suite.";

        public static string ProjectRoot => Directory.GetParent(Application.dataPath).FullName;

        public static string ClaudeSkillPath =>
            Path.Combine(ProjectRoot, ".claude", "skills", SkillName, "SKILL.md");

        public static bool IsInstalled => File.Exists(ClaudeSkillPath);

        [MenuItem("Tools/GameTestKit/AI/Install Authoring Skill", priority = 83)]
        public static void InstallFromMenu()
        {
            var path = Install();
            Debug.Log($"[GameTestKit] AI authoring skill written to {path}");
            EditorUtility.RevealInFinder(path);
        }

        /// <summary>Writes the skill file. Defaults to <c>&lt;project&gt;/.claude/skills/…/SKILL.md</c>.</summary>
        public static string Install(string path = null)
        {
            path ??= ClaudeSkillPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, Build(), new UTF8Encoding(false));
            return path;
        }

        public static string Build()
        {
            var builder = new StringBuilder();
            builder.Append("---\n");
            builder.Append("name: ").Append(SkillName).Append('\n');
            builder.Append("description: ").Append(Description).Append('\n');
            builder.Append("---\n\n");

            var body = AssetDatabase.LoadAssetAtPath<TextAsset>(BodyAssetPath);
            builder.Append(body != null
                ? body.text
                : "# GameTestKit authoring\n(The skill body asset is missing from the package.)\n");

            builder.Append("\n\n");
            builder.Append(BuildReference());
            return builder.ToString();
        }

        /// <summary>The live half of the skill: steps, selectors, expressions, scenes and ids.</summary>
        public static string BuildReference()
        {
            var builder = new StringBuilder();
            var catalogue = AICommands.BuildCatalogue();

            builder.AppendLine("# Reference (generated from this project)");
            builder.AppendLine();
            builder.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm} · Unity {Application.unityVersion}");
            builder.AppendLine();

            // ---- steps, grouped by category -----------------------------
            builder.AppendLine("## Step verbs");
            builder.AppendLine();

            string currentCategory = null;
            foreach (var step in Sorted(catalogue["steps"]))
            {
                var category = step["category"].AsString("General");
                if (category != currentCategory)
                {
                    currentCategory = category;
                    builder.AppendLine($"### {category}");
                    builder.AppendLine();
                }

                builder.Append("**`").Append(step["key"].AsString()).Append("`**");

                var aliases = step["aliases"];
                if (aliases.IsArray && aliases.Count > 0)
                {
                    builder.Append(" (also `");
                    for (int i = 0; i < aliases.Count; i++)
                    {
                        if (i > 0) builder.Append("`, `");
                        builder.Append(aliases[i].AsString());
                    }
                    builder.Append("`)");
                }

                builder.Append(" — ").AppendLine(step["summary"].AsString());
                builder.AppendLine();

                var parameters = step["parameters"];
                if (parameters.IsArray && parameters.Count > 0)
                {
                    builder.AppendLine("| parameter | type | required | default | meaning |");
                    builder.AppendLine("|---|---|---|---|---|");
                    foreach (var parameter in parameters)
                    {
                        builder.Append("| `").Append(parameter["name"].AsString()).Append("` | ")
                            .Append(parameter["type"].AsString()).Append(" | ")
                            .Append(parameter["required"].AsBool() ? "yes" : "—").Append(" | ")
                            .Append(parameter.Has("default") ? "`" + parameter["default"].AsString() + "`" : "—")
                            .Append(" | ").Append(parameter["description"].AsString()).AppendLine(" |");
                    }
                    builder.AppendLine();
                }

                var example = step["example"].AsString();
                if (!string.IsNullOrEmpty(example))
                    builder.AppendLine("```json").AppendLine(example).AppendLine("```").AppendLine();
            }

            // ---- selectors ----------------------------------------------
            builder.AppendLine("## Selectors");
            builder.AppendLine();
            builder.AppendLine("| syntax | meaning |");
            builder.AppendLine("|---|---|");
            foreach (var locator in catalogue["locators"])
                builder.Append("| `").Append(locator["syntax"].AsString()).Append("` | ")
                    .Append(locator["meaning"].AsString()).AppendLine(" |");
            builder.AppendLine();

            // ---- expressions --------------------------------------------
            var expressions = catalogue["expressions"];
            builder.AppendLine("## Expressions (used by `assert` and `waitFor`)");
            builder.AppendLine();
            builder.AppendLine($"- Operators: {expressions["operators"].AsString()}");
            builder.AppendLine($"- Functions: {expressions["functions"].AsString()}");
            builder.AppendLine($"- Built-in values: {expressions["builtinValues"].AsString()}");
            builder.AppendLine($"- Example: `{expressions["example"].AsString()}`");
            builder.AppendLine();

            // ---- project specifics --------------------------------------
            AppendList(builder, "## Scenes in the build", catalogue["buildScenes"],
                "None are enabled in Build Profiles — `loadScene` and `scene` will fail until some are added.");

            AppendList(builder, "## Test ids found in scenes and prefabs", catalogue["testIdsInScenes"],
                "None yet. Add `TestId` components to the elements your tests touch — " +
                "it is the difference between a suite that survives a refactor and one that does not.");

            var bindings = catalogue["bindings"];
            builder.AppendLine("## Game state bindings");
            builder.AppendLine();
            if (bindings.Count == 0)
            {
                builder.AppendLine("None are registered at edit time. Bindings are registered by the game while it " +
                                   "runs — use the Inspect command to list the live ones. If the game has none, see " +
                                   "§5 above for how to add them.");
            }
            else
            {
                builder.AppendLine("| path | kind | description |");
                builder.AppendLine("|---|---|---|");
                foreach (var binding in bindings)
                    builder.Append("| `").Append(binding["path"].AsString()).Append("` | ")
                        .Append(binding["kind"].AsString()).Append(" | ")
                        .Append(binding["description"].AsString("")).AppendLine(" |");
            }
            builder.AppendLine();

            return builder.ToString();
        }

        private static void AppendList(StringBuilder builder, string heading, JsonValue array, string emptyNote)
        {
            builder.AppendLine(heading);
            builder.AppendLine();

            if (array == null || array.Count == 0)
            {
                builder.AppendLine(emptyNote);
            }
            else
            {
                foreach (var item in array) builder.AppendLine($"- `{item.AsString()}`");
            }

            builder.AppendLine();
        }

        /// <summary>Groups the catalogue by category while keeping registration order inside a group.</summary>
        private static System.Collections.Generic.List<JsonValue> Sorted(JsonValue steps)
        {
            var list = new System.Collections.Generic.List<JsonValue>();

            foreach (var category in new[] { "Input", "Flow", "Scene", "Assertions" })
                foreach (var step in steps)
                    if (step["category"].AsString("General") == category)
                        list.Add(step);

            // Anything a game registered under its own category goes last, in registration order.
            foreach (var step in steps)
                if (!list.Contains(step))
                    list.Add(step);

            return list;
        }
    }
}
