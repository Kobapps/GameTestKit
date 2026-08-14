using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Kobapps.GameTestKit.Scripting;

namespace Kobapps.GameTestKit
{
    /// <summary>The verdict on one property of one event.</summary>
    public sealed class EventPropertyMatch
    {
        public string Key;
        public string Expected;
        public string Observed;
        public bool Passed;

        /// <summary>Why it failed, phrased to be read in a report without the matcher beside it.</summary>
        public string Reason;

        public static EventPropertyMatch Pass(string key, string expected, string observed) =>
            new EventPropertyMatch { Key = key, Expected = expected, Observed = observed, Passed = true };

        public static EventPropertyMatch Fail(string key, string expected, string observed, string reason) =>
            new EventPropertyMatch
            {
                Key = key, Expected = expected, Observed = observed, Passed = false, Reason = reason,
            };

        public override string ToString() =>
            Passed ? $"{Key} = {Observed}" : $"{Key}: expected {Expected}, got {Observed} — {Reason}";
    }

    /// <summary>
    /// Decides whether one property on an event says what a test expected it to say.
    /// </summary>
    /// <remarks>
    /// The grammar is deliberately small, and every form is here because a real defect looks like it:
    /// <list type="table">
    /// <item><term><c>1</c>, <c>"Win"</c>, <c>true</c></term><description>equality.</description></item>
    /// <item><term><c>"*"</c></term><description>present and non-null — for a value that is real but not
    /// predictable, like a duration.</description></item>
    /// <item><term><c>"!"</c></term><description>absent or null — for a property that must <em>not</em> be
    /// stamped on this event.</description></item>
    /// <item><term><c>"&gt;0"</c>, <c>"&gt;=1"</c>, <c>"&lt;10"</c>, <c>"&lt;=3"</c>, <c>"!=0"</c></term>
    /// <description>a numeric bound. A duration of zero and a duration of forty seconds are both "a
    /// number"; only one of them is right.</description></item>
    /// <item><term><c>"~^Level_"</c></term><description>regex.</description></item>
    /// <item><term><c>["Win","Fail","Quit"]</c></term><description>one of.</description></item>
    /// <item><term><c>"type:number|string|bool|list"</c></term><description>type only.</description></item>
    /// <item><term><c>"@const:My.Telemetry.RESULT_*"</c></term><description>one of the constants a class
    /// declares. This is the form that catches a <c>"win"</c> written where <c>"Win"</c> was meant — a
    /// break no equality check against a hand-copied literal will ever find.</description></item>
    /// </list>
    /// Comparison follows the kit's expression engine on purpose — numbers within an epsilon, strings
    /// case-insensitively — so <c>{"assert": …}</c> and <c>{"expectEvent": …}</c> in one file never
    /// disagree about whether two values are equal.
    /// </remarks>
    public static class EventPropertyMatcher
    {
        private const string Present = "*";
        private const string Absent = "!";
        private const string RegexPrefix = "~";
        private const string ConstPrefix = "@const:";
        private const string TypePrefix = "type:";
        private const double Epsilon = 1e-6;

        /// <summary>Evaluates one key against one matcher, given the payload that was on the wire.</summary>
        public static EventPropertyMatch Match(string key, JsonValue matcher,
            IReadOnlyDictionary<string, object> properties)
        {
            bool present = properties != null && properties.TryGetValue(key, out var found) && found != null;
            object observed = present ? properties[key] : null;

            var observedText = Describe(observed);
            var matcherText = matcher == null ? "null" : matcher.ToJson(false);

            if (matcher != null && matcher.IsArray)
                return MatchOneOf(key, matcher, matcherText, observed, observedText, present);

            if (matcher != null && matcher.IsString)
                return MatchString(key, matcher.AsString(""), matcherText, observed, observedText, present);

            if (!present)
                return EventPropertyMatch.Fail(key, matcherText, null, "the property was absent or null");

            if (matcher != null && matcher.IsNumber)
                return NumbersEqual(observed, matcher.AsNumber())
                    ? EventPropertyMatch.Pass(key, matcherText, observedText)
                    : EventPropertyMatch.Fail(key, matcherText, observedText, "the numbers differ");

            if (matcher != null && matcher.IsBool)
                return observed is bool actual && actual == matcher.AsBool()
                    ? EventPropertyMatch.Pass(key, matcherText, observedText)
                    : EventPropertyMatch.Fail(key, matcherText, observedText, "the booleans differ");

            return EventPropertyMatch.Fail(key, matcherText, observedText, "unsupported matcher");
        }

        /// <summary>Every property of an event checked against a matcher object, in the matcher's order.</summary>
        public static List<EventPropertyMatch> MatchAll(JsonValue matchers,
            IReadOnlyDictionary<string, object> properties)
        {
            var results = new List<EventPropertyMatch>();
            if (matchers == null || !matchers.IsObject) return results;

            foreach (var key in matchers.Keys)
                results.Add(Match(key, matchers[key], properties));

            return results;
        }

        // ---------------------------------------------------------------- forms

        private static EventPropertyMatch MatchString(string key, string matcher, string matcherText,
            object observed, string observedText, bool present)
        {
            if (matcher == Present)
                return present
                    ? EventPropertyMatch.Pass(key, matcherText, observedText)
                    : EventPropertyMatch.Fail(key, matcherText, null, "the property was absent or null");

            if (matcher == Absent)
                return present
                    ? EventPropertyMatch.Fail(key, matcherText, observedText,
                        "the property must not be on this event, but it was")
                    : EventPropertyMatch.Pass(key, matcherText, "(absent)");

            if (!present)
                return EventPropertyMatch.Fail(key, matcherText, null, "the property was absent or null");

            if (matcher.StartsWith(RegexPrefix, StringComparison.Ordinal))
                return MatchRegex(key, matcher.Substring(1), matcherText, observedText);

            if (matcher.StartsWith(TypePrefix, StringComparison.OrdinalIgnoreCase))
                return MatchType(key, matcher.Substring(TypePrefix.Length), matcherText, observed, observedText);

            if (matcher.StartsWith(ConstPrefix, StringComparison.OrdinalIgnoreCase))
                return MatchConstant(key, matcher.Substring(ConstPrefix.Length), matcherText, observedText);

            var comparison = ParseComparison(matcher);
            if (comparison != null)
                return MatchComparison(key, comparison, matcherText, observed, observedText);

            return StringsEqual(observedText, matcher)
                ? EventPropertyMatch.Pass(key, matcherText, observedText)
                : EventPropertyMatch.Fail(key, matcherText, observedText, "the values differ");
        }

        private static EventPropertyMatch MatchOneOf(string key, JsonValue options, string matcherText,
            object observed, string observedText, bool present)
        {
            if (!present)
                return EventPropertyMatch.Fail(key, matcherText, null, "the property was absent or null");

            foreach (var option in options)
            {
                if (option.IsNumber && NumbersEqual(observed, option.AsNumber()))
                    return EventPropertyMatch.Pass(key, matcherText, observedText);

                if (option.IsBool && observed is bool actual && actual == option.AsBool())
                    return EventPropertyMatch.Pass(key, matcherText, observedText);

                if (option.IsString && StringsEqual(observedText, option.AsString("")))
                    return EventPropertyMatch.Pass(key, matcherText, observedText);
            }

            return EventPropertyMatch.Fail(key, matcherText, observedText, "the value is not one of the allowed ones");
        }

        private static EventPropertyMatch MatchRegex(string key, string pattern, string matcherText, string observedText)
        {
            try
            {
                return Regex.IsMatch(observedText, pattern)
                    ? EventPropertyMatch.Pass(key, matcherText, observedText)
                    : EventPropertyMatch.Fail(key, matcherText, observedText, $"it does not match /{pattern}/");
            }
            catch (ArgumentException e)
            {
                return EventPropertyMatch.Fail(key, matcherText, observedText, $"the pattern is not valid: {e.Message}");
            }
        }

        private static EventPropertyMatch MatchType(string key, string wanted, string matcherText,
            object observed, string observedText)
        {
            bool ok;
            switch ((wanted ?? "").Trim().ToLowerInvariant())
            {
                case "number": ok = IsNumber(observed); break;
                case "string": ok = observed is string; break;
                case "bool": ok = observed is bool; break;
                case "list": ok = observed is IEnumerable && !(observed is string); break;
                default:
                    return EventPropertyMatch.Fail(key, matcherText, observedText,
                        $"unknown type '{wanted}'. Use number, string, bool or list.");
            }

            return ok
                ? EventPropertyMatch.Pass(key, matcherText, observedText)
                : EventPropertyMatch.Fail(key, matcherText, observedText, $"it is not a {wanted}");
        }

        /// <summary>
        /// Matches against the constants a class declares, e.g. <c>@const:My.Telemetry.RESULT_*</c>.
        /// </summary>
        /// <remarks>
        /// The point is that the test never repeats the literal. A controlled vocabulary that the game
        /// renames — or that a caller spells with the wrong case — breaks the dashboard silently, and an
        /// assertion holding its own copy of the old string passes right through it.
        /// </remarks>
        private static EventPropertyMatch MatchConstant(string key, string reference, string matcherText,
            string observedText)
        {
            int split = reference.LastIndexOf('.');
            if (split <= 0)
                return EventPropertyMatch.Fail(key, matcherText, observedText,
                    "expected @const:Namespace.Type.MEMBER or @const:Namespace.Type.PREFIX_*");

            var typeName = reference.Substring(0, split);
            var member = reference.Substring(split + 1);

            var type = FindType(typeName);
            if (type == null)
                return EventPropertyMatch.Fail(key, matcherText, observedText, $"no type named '{typeName}'");

            var allowed = ConstantsOf(type, member);
            if (allowed.Count == 0)
                return EventPropertyMatch.Fail(key, matcherText, observedText,
                    $"'{typeName}' declares no constant matching '{member}'");

            foreach (var value in allowed)
                if (StringsEqual(observedText, value))
                    return EventPropertyMatch.Pass(key, matcherText, observedText);

            return EventPropertyMatch.Fail(key, matcherText, observedText,
                $"it is none of {typeName}.{member}: {string.Join(", ", allowed)}");
        }

        private static List<string> ConstantsOf(Type type, string member)
        {
            var values = new List<string>();
            bool wildcard = member.EndsWith("*", StringComparison.Ordinal);
            var prefix = wildcard ? member.Substring(0, member.Length - 1) : member;

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            {
                if (!field.IsLiteral && !field.IsInitOnly) continue;

                bool matches = wildcard
                    ? field.Name.StartsWith(prefix, StringComparison.Ordinal)
                    : string.Equals(field.Name, prefix, StringComparison.Ordinal);

                if (!matches) continue;

                var value = field.GetValue(null);
                if (value != null) values.Add(Describe(value));
            }

            return values;
        }

        private static Type FindType(string name)
        {
            var direct = Type.GetType(name, false, true);
            if (direct != null) return direct;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var found = assembly.GetType(name, false, true);
                if (found != null) return found;
            }

            return null;
        }

        // ---------------------------------------------------------------- comparisons

        private sealed class Comparison
        {
            public string Operator;
            public double Value;
        }

        /// <summary>Reads <c>"&gt;=1"</c> and friends. Null when the text is an ordinary value.</summary>
        private static Comparison ParseComparison(string matcher)
        {
            var text = matcher.Trim();

            foreach (var op in new[] { ">=", "<=", "!=", ">", "<" })
            {
                if (!text.StartsWith(op, StringComparison.Ordinal)) continue;

                var rest = text.Substring(op.Length).Trim();
                if (!double.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    return null;

                return new Comparison { Operator = op, Value = value };
            }

            return null;
        }

        private static EventPropertyMatch MatchComparison(string key, Comparison comparison, string matcherText,
            object observed, string observedText)
        {
            if (!TryNumber(observed, out var actual))
                return EventPropertyMatch.Fail(key, matcherText, observedText,
                    "it is not a number, so it cannot be compared");

            bool ok;
            switch (comparison.Operator)
            {
                case ">": ok = actual > comparison.Value; break;
                case ">=": ok = actual >= comparison.Value - Epsilon; break;
                case "<": ok = actual < comparison.Value; break;
                case "<=": ok = actual <= comparison.Value + Epsilon; break;
                case "!=": ok = Math.Abs(actual - comparison.Value) > Epsilon; break;
                default: ok = false; break;
            }

            return ok
                ? EventPropertyMatch.Pass(key, matcherText, observedText)
                : EventPropertyMatch.Fail(key, matcherText, observedText,
                    $"{actual.ToString("0.####", CultureInfo.InvariantCulture)} is not {comparison.Operator} " +
                    comparison.Value.ToString("0.####", CultureInfo.InvariantCulture));
        }

        private static bool NumbersEqual(object observed, double expected) =>
            TryNumber(observed, out var actual) && Math.Abs(actual - expected) <= Epsilon;

        private static bool StringsEqual(string observed, string expected) =>
            string.Equals(observed, expected, StringComparison.OrdinalIgnoreCase);

        private static bool IsNumber(object value) => TryNumber(value, out _) && !(value is string) && !(value is bool);

        private static bool TryNumber(object value, out double number)
        {
            number = 0;

            switch (value)
            {
                case null: return false;
                case bool _: return false;
                case double d: number = d; return true;
                case float f: number = f; return true;
                case int i: number = i; return true;
                case long l: number = l; return true;
                case short s: number = s; return true;
                case byte b: number = b; return true;
                case decimal m: number = (double)m; return true;
                case string text:
                    return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
                default:
                    return double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                        NumberStyles.Float, CultureInfo.InvariantCulture, out number);
            }
        }

        /// <summary>A property value as a report would show it. Lists become <c>[a, b]</c>.</summary>
        public static string Describe(object value)
        {
            switch (value)
            {
                case null: return "(absent)";
                case string text: return text;
                case bool flag: return flag ? "true" : "false";
                case float f: return f.ToString("0.####", CultureInfo.InvariantCulture);
                case double d: return d.ToString("0.####", CultureInfo.InvariantCulture);
                case IEnumerable list when !(value is string):
                    var text2 = new StringBuilder("[");
                    bool first = true;
                    foreach (var item in list)
                    {
                        if (!first) text2.Append(", ");
                        text2.Append(Describe(item));
                        first = false;
                    }
                    return text2.Append(']').ToString();
                default:
                    return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            }
        }
    }
}
