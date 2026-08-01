using System;
using System.Collections.Generic;
using Kobapps.CodeEditor;
using Kobapps.GameTestKit.Scripting;

namespace Kobapps.GameTestKit.Editor
{
    /// <summary>
    /// Teaches the generic code editor what a <c>.gametest.json</c> may contain.
    /// </summary>
    /// <remarks>
    /// Everything offered comes from the live Editor rather than a hard-coded list: the verbs from
    /// <see cref="StepRegistry"/>, the state paths from <see cref="GameTestBindings"/>. That is the point
    /// of generating it — a game that registers its own steps or bindings gets them in the dropdown the
    /// same day, with the same parameter help, and nobody has to remember to update a schema.
    /// </remarks>
    public sealed class GameTestCompletionSource : ICompletionSource
    {
        private static readonly string[] TopLevelKeys =
        {
            "name", "description", "tags", "scene", "timeout", "retries", "repeat",
            "skip", "skipReason", "setup", "steps", "teardown",
        };

        private static readonly string[] CommonStepKeys =
        {
            "timeout", "label", "continueOnFailure", "message",
        };

        public IEnumerable<char> TriggerCharacters => new[] { '"', ':' };

        public IEnumerable<CompletionItem> GetCompletions(CompletionContext context)
        {
            var items = new List<CompletionItem>();

            // Inside an expression — assert / waitFor / until — the useful vocabulary is state, not verbs.
            if (LooksLikeExpression(context.LinePrefix))
            {
                AddBindings(items);
                AddExpressionFunctions(items);
                return items;
            }

            AddSteps(items);
            AddKeys(items);
            AddBindings(items);
            return items;
        }

        /// <summary>True when the caret sits in the value of a step that takes an expression.</summary>
        private static bool LooksLikeExpression(string linePrefix)
        {
            foreach (var key in new[] { "\"assert\"", "\"waitFor\"", "\"until\"", "\"failIf\"" })
                if (linePrefix.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            return false;
        }

        private static void AddSteps(List<CompletionItem> items)
        {
            foreach (var definition in StepRegistry.All)
            {
                var parameters = DescribeParameters(definition);
                items.Add(new CompletionItem
                {
                    Label = definition.Key,
                    InsertText = string.IsNullOrEmpty(definition.Example)
                        ? $"{{ \"{definition.Key}\": \"\" }}"
                        : definition.Example.Trim(),
                    Detail = definition.Category,
                    Documentation = string.IsNullOrEmpty(parameters)
                        ? definition.Summary
                        : definition.Summary + "\n\n" + parameters,
                    Kind = CompletionKind.Keyword,
                    // Game verbs above framework ones: on any given project they are the rarer half and
                    // the half nobody has memorised.
                    Priority = definition.Category == "General" ? 10 : 20,
                });
            }
        }

        private static string DescribeParameters(StepDefinition definition)
        {
            if (definition.Parameters == null || definition.Parameters.Length == 0) return "";

            var lines = new List<string>();
            foreach (var parameter in definition.Parameters)
            {
                var required = parameter.Required ? " (required)" : "";
                var fallback = string.IsNullOrEmpty(parameter.Default) ? "" : $" [default {parameter.Default}]";
                lines.Add($"• {parameter.Name}: {parameter.Type}{required}{fallback} — {parameter.Description}");
            }
            return string.Join("\n", lines);
        }

        private static void AddKeys(List<CompletionItem> items)
        {
            foreach (var key in TopLevelKeys)
                items.Add(new CompletionItem(key, "test", CompletionKind.Member) { Priority = 5 });

            foreach (var key in CommonStepKeys)
                items.Add(new CompletionItem(key, "any step", CompletionKind.Member) { Priority = 4 });
        }

        private static void AddBindings(List<CompletionItem> items)
        {
            foreach (var binding in GameTestBindings.Describe())
            {
                items.Add(new CompletionItem
                {
                    Label = binding.Path,
                    Detail = binding.Kind == BindingKind.Value
                        ? binding.ValueTypeName ?? "value"
                        : "action",
                    Documentation = binding.Description,
                    Kind = binding.Kind == BindingKind.Value ? CompletionKind.Value : CompletionKind.Member,
                    Priority = 15,
                });
            }
        }

        private static void AddExpressionFunctions(List<CompletionItem> items)
        {
            (string name, string detail)[] functions =
            {
                ("exists", "exists(selector)"),
                ("visible", "visible(selector)"),
                ("interactable", "interactable(selector)"),
                ("blocked", "blocked(selector)"),
                ("count", "count(selector)"),
                ("text", "text(selector)"),
                ("label", "label(selector)"),
                ("sceneLoaded", "sceneLoaded(name)"),
                ("abs", "abs(x)"), ("min", "min(a,b)"), ("max", "max(a,b)"),
                ("round", "round(x)"), ("len", "len(x)"),
                ("scene", "active scene name"),
                ("time", "seconds since load"),
                ("fps", "current frame rate"),
            };

            foreach (var (name, detail) in functions)
                items.Add(new CompletionItem(name, detail, CompletionKind.Snippet) { Priority = 12 });

            foreach (var op in new[] { "and", "or", "not", "contains", "startsWith", "endsWith", "matches" })
                items.Add(new CompletionItem(op, "operator", CompletionKind.Keyword) { Priority = 8 });
        }
    }

    /// <summary>Parses the document with the same parser the runner uses, so nothing disagrees.</summary>
    public sealed class GameTestValidator : ICodeValidator
    {
        public string SourcePath;

        public IEnumerable<CodeDiagnostic> Validate(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                yield return CodeDiagnostic.Warning("Empty script.");
                yield break;
            }

            GameTest test = null;
            string failure = null;
            try { test = TestScriptParser.ParseTest(text, SourcePath); }
            catch (Exception e) { failure = e.Message; }

            if (failure != null)
            {
                yield return CodeDiagnostic.Error(failure, LineFromMessage(failure));
                yield break;
            }

            int steps = test.Setup.Count + test.Steps.Count + test.Teardown.Count;
            var tags = test.Tags.Count > 0 ? $", tags: {string.Join(", ", test.Tags)}" : "";
            yield return CodeDiagnostic.Info($"\"{test.Name}\" — {steps} step(s){tags}");
        }

        /// <summary>Pulls a line number out of the parser's message so the status line can point at it.</summary>
        private static int LineFromMessage(string message)
        {
            var match = System.Text.RegularExpressions.Regex.Match(message, @"line\s+(\d+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups[1].Value, out var line) ? line : 0;
        }
    }
}
