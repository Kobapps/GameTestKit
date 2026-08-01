using System.Collections;
using System.Text.RegularExpressions;
using UnityEngine;

namespace Kobapps.GameTestKit
{
    /// <summary>
    /// Asserts an expression over game state and the screen, e.g.
    /// <c>player.gold == 40 and visible('#ShopClosed')</c>.
    /// </summary>
    /// <remarks>
    /// On failure the message includes the sub-values the expression read, so a report says
    /// <em>what</em> was wrong rather than just that something was.
    /// </remarks>
    public sealed class AssertStep : TestStep
    {
        public string Expression;
        public string Message;

        /// <summary>Give the game a moment to settle before failing — 0 asserts immediately.</summary>
        public float RetryFor;

        public override string Describe() => $"assert {Expression}";

        public override IEnumerator Execute(TestContext ctx)
        {
            float deadline = Time.realtimeSinceStartup + Mathf.Max(0f, RetryFor);

            while (true)
            {
                bool ok = Scripting.Expression.TryEvaluateBool(Expression, out var result, out var error);

                if (ok && result) yield break;

                if (Time.realtimeSinceStartup >= deadline)
                {
                    var detail = ok ? Explain(Expression) : error;
                    var prefix = string.IsNullOrEmpty(Message) ? "Assertion failed" : Message;
                    throw new TestFailureException($"{prefix}: {Expression} — {detail}");
                }

                yield return null;
            }
        }

        /// <summary>Best-effort "actual value" text for a failed comparison.</summary>
        private static string Explain(string expression)
        {
            foreach (var op in new[] { ">=", "<=", "==", "!=", ">", "<", " contains " })
            {
                int index = expression.IndexOf(op, System.StringComparison.OrdinalIgnoreCase);
                if (index <= 0) continue;

                var left = expression.Substring(0, index).Trim();
                var right = expression.Substring(index + op.Length).Trim();
                try
                {
                    var actual = Scripting.Expression.Evaluate(left);
                    var expected = Scripting.Expression.Evaluate(right);
                    return $"{left} was {Scripting.Expression.Describe(actual)}, " +
                           $"expected {op.Trim()} {Scripting.Expression.Describe(expected)}";
                }
                catch
                {
                    return "evaluated to false";
                }
            }
            return "evaluated to false";
        }
    }

    public enum ElementCondition
    {
        Exists,
        Missing,
        Visible,
        Hidden,
        Interactable,
        Disabled,
    }

    /// <summary>Asserts the state of an element without writing an expression by hand.</summary>
    public sealed class AssertElementStep : TestStep
    {
        public string Selector;
        public ElementCondition Condition = ElementCondition.Visible;
        public float RetryFor = 1f;

        public override string Describe() => $"assert {Selector} is {Condition.ToString().ToLowerInvariant()}";

        public override IEnumerator Execute(TestContext ctx)
        {
            float deadline = Time.realtimeSinceStartup + Mathf.Max(0f, RetryFor);

            while (true)
            {
                var matches = Locator.FindAll(Selector);
                bool satisfied;
                string actual;

                switch (Condition)
                {
                    case ElementCondition.Exists:
                        satisfied = matches.Count > 0;
                        actual = $"{matches.Count} match(es)";
                        break;

                    case ElementCondition.Missing:
                        satisfied = matches.Count == 0;
                        actual = $"{matches.Count} match(es) still present";
                        break;

                    case ElementCondition.Visible:
                        satisfied = AnyVisible(matches);
                        actual = matches.Count == 0 ? "no match at all" : "found but not visible";
                        break;

                    case ElementCondition.Hidden:
                        satisfied = !AnyVisible(matches);
                        actual = "it is visible";
                        break;

                    case ElementCondition.Interactable:
                        satisfied = AnyInteractable(matches);
                        actual = matches.Count == 0 ? "no match at all" : "found but not interactable";
                        break;

                    case ElementCondition.Disabled:
                        satisfied = !AnyInteractable(matches);
                        actual = "it is interactable";
                        break;

                    default:
                        satisfied = false;
                        actual = "unknown condition";
                        break;
                }

                if (satisfied) yield break;

                if (Time.realtimeSinceStartup >= deadline)
                    throw new TestFailureException(
                        $"Expected '{Selector}' to be {Condition.ToString().ToLowerInvariant()} but {actual}.");

                yield return null;
            }
        }

        private static bool AnyVisible(System.Collections.Generic.List<GameObject> matches)
        {
            for (int i = 0; i < matches.Count; i++)
                if (UiProbe.IsVisible(matches[i])) return true;
            return false;
        }

        private static bool AnyInteractable(System.Collections.Generic.List<GameObject> matches)
        {
            for (int i = 0; i < matches.Count; i++)
                if (UiProbe.IsInteractable(matches[i])) return true;
            return false;
        }
    }

    /// <summary>Asserts what an element says — the check most end-to-end flows actually need.</summary>
    public sealed class AssertTextStep : TestStep
    {
        public string Selector;

        /// <summary>Exact match (trimmed). Set exactly one of these three.</summary>
        public string ExpectedEquals;

        public string ExpectedContains;

        /// <summary>Regular expression the text must match.</summary>
        public string ExpectedPattern;

        public float RetryFor = 1f;

        public override string Describe()
        {
            if (ExpectedEquals != null) return $"assert {Selector} text == \"{ExpectedEquals}\"";
            if (ExpectedContains != null) return $"assert {Selector} text contains \"{ExpectedContains}\"";
            return $"assert {Selector} text matches /{ExpectedPattern}/";
        }

        public override IEnumerator Execute(TestContext ctx)
        {
            float deadline = Time.realtimeSinceStartup + Mathf.Max(0f, RetryFor);

            while (true)
            {
                var target = Locator.Find(Selector);
                var text = target != null ? UiProbe.LabelOf(target) : null;

                bool satisfied = false;
                if (text != null)
                {
                    if (ExpectedEquals != null)
                        satisfied = string.Equals(text.Trim(), ExpectedEquals.Trim(), System.StringComparison.Ordinal);
                    else if (ExpectedContains != null)
                        satisfied = text.IndexOf(ExpectedContains, System.StringComparison.OrdinalIgnoreCase) >= 0;
                    else if (ExpectedPattern != null)
                        satisfied = Regex.IsMatch(text, ExpectedPattern);
                    else
                        throw new TestFailureException(
                            "assertText needs one of \"equals\", \"contains\" or \"matches\".");
                }

                if (satisfied) yield break;

                if (Time.realtimeSinceStartup >= deadline)
                {
                    if (target == null)
                        throw new TestFailureException($"Cannot read text: {Locator.DescribeMiss(Selector)}");
                    throw new TestFailureException(
                        $"Text of '{Selector}' was \"{text}\", expected {DescribeExpectation()}.");
                }

                yield return null;
            }
        }

        private string DescribeExpectation()
        {
            if (ExpectedEquals != null) return $"\"{ExpectedEquals}\"";
            if (ExpectedContains != null) return $"to contain \"{ExpectedContains}\"";
            return $"to match /{ExpectedPattern}/";
        }
    }

    /// <summary>
    /// Declares that the game is expected to log an error matching a pattern, so the run's
    /// fail-on-error policy does not trip. Fails if the error never appears.
    /// </summary>
    public sealed class ExpectLogStep : TestStep
    {
        public string Pattern;
        public float Within = 5f;

        public override string Describe() => $"expect log /{Pattern}/";

        public override IEnumerator Execute(TestContext ctx)
        {
            if (ctx.Logs == null)
                throw new TestFailureException("Log capture is not active for this run.");

            var regex = new Regex(Pattern, RegexOptions.CultureInvariant);
            float deadline = Time.realtimeSinceStartup + Within;

            while (true)
            {
                foreach (var entry in ctx.Logs.Entries)
                {
                    if (!regex.IsMatch(entry.Message ?? string.Empty)) continue;
                    ctx.Logs.ForgiveError(regex);
                    yield break;
                }

                if (Time.realtimeSinceStartup >= deadline)
                    throw new TestFailureException(
                        $"Expected a log line matching /{Pattern}/ within {Within:0.#}s but none appeared.");

                yield return null;
            }
        }
    }
}
