using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Kobapps.GameTestKit.Scripting
{
    /// <summary>
    /// The small expression language behind <c>assert</c> and <c>waitFor</c>:
    /// <c>visible('#ShopPanel') and player.gold &gt;= 100</c>.
    /// </summary>
    /// <remarks>
    /// It deliberately does one thing — read the world and compare — so a script can state an outcome
    /// without a compile step or an escape hatch into arbitrary code.
    /// <para><b>Operators</b>: <c>and or not</c> (or <c>&amp;&amp; || !</c>), <c>== != &gt; &gt;= &lt; &lt;=</c>,
    /// <c>contains</c>, <c>+ - * /</c>, parentheses.</para>
    /// <para><b>Functions</b>: <c>exists(sel) visible(sel) interactable(sel) count(sel) text(sel)
    /// label(sel) blocked(sel) sceneLoaded(name) abs(x) min(a,b) max(a,b)</c>.</para>
    /// <para><b>Values</b>: <c>scene time fps screen.width screen.height timeScale</c>, plus every path
    /// registered through <see cref="GameTestBindings"/>.</para>
    /// </remarks>
    public static class Expression
    {
        public static object Evaluate(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
                throw new TestFailureException("Empty expression.");

            var tokens = Tokenize(source);
            var parser = new Parser(tokens, source);
            var value = parser.ParseExpression();
            parser.ExpectEnd();
            return value;
        }

        /// <summary>Evaluates and coerces to bool. Numbers are true when non-zero, strings when non-empty.</summary>
        public static bool EvaluateBool(string source)
        {
            var value = Evaluate(source);
            return Truthy(value);
        }

        /// <summary>Evaluates without throwing — used by waits that poll until an expression holds.</summary>
        public static bool TryEvaluateBool(string source, out bool result, out string error)
        {
            try { result = EvaluateBool(source); error = null; return true; }
            catch (Exception e) { result = false; error = e.Message; return false; }
        }

        public static bool Truthy(object value)
        {
            switch (value)
            {
                case null: return false;
                case bool b: return b;
                case double d: return Math.Abs(d) > double.Epsilon;
                case string s: return !string.IsNullOrEmpty(s);
                default: return true;
            }
        }

        /// <summary>Renders a value the way an assertion message should show it.</summary>
        public static string Describe(object value)
        {
            switch (value)
            {
                case null: return "null";
                case bool b: return b ? "true" : "false";
                case double d: return d.ToString("0.####", CultureInfo.InvariantCulture);
                case string s: return $"\"{s}\"";
                default: return value.ToString();
            }
        }

        // ================================================================ lexer

        private enum TokenType { Number, String, Identifier, Operator, LeftParen, RightParen, Comma, End }

        private struct Token
        {
            public TokenType Type;
            public string Text;
            public double Number;
            public int Position;
        }

        private static List<Token> Tokenize(string source)
        {
            var tokens = new List<Token>();
            int i = 0;

            while (i < source.Length)
            {
                char c = source[i];

                if (char.IsWhiteSpace(c)) { i++; continue; }

                if (c == '(') { tokens.Add(new Token { Type = TokenType.LeftParen, Text = "(", Position = i++ }); continue; }
                if (c == ')') { tokens.Add(new Token { Type = TokenType.RightParen, Text = ")", Position = i++ }); continue; }
                if (c == ',') { tokens.Add(new Token { Type = TokenType.Comma, Text = ",", Position = i++ }); continue; }

                if (c == '\'' || c == '"')
                {
                    int start = i;
                    char quote = c;
                    i++;
                    var builder = new StringBuilder();
                    while (i < source.Length && source[i] != quote)
                    {
                        if (source[i] == '\\' && i + 1 < source.Length) i++;
                        builder.Append(source[i++]);
                    }
                    if (i >= source.Length)
                        throw new TestFailureException($"Unterminated string in expression at {start}.");
                    i++; // closing quote
                    tokens.Add(new Token { Type = TokenType.String, Text = builder.ToString(), Position = start });
                    continue;
                }

                if (char.IsDigit(c) || (c == '.' && i + 1 < source.Length && char.IsDigit(source[i + 1])))
                {
                    int start = i;
                    while (i < source.Length && (char.IsDigit(source[i]) || source[i] == '.')) i++;
                    var slice = source.Substring(start, i - start);
                    if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                        throw new TestFailureException($"'{slice}' is not a number.");
                    tokens.Add(new Token { Type = TokenType.Number, Number = number, Text = slice, Position = start });
                    continue;
                }

                // Identifiers stay narrow (letters, digits, '_' and '.') so that `gold-10` still parses
                // as arithmetic. Selectors go in quotes: visible('#PlayButton').
                if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    while (i < source.Length &&
                           (char.IsLetterOrDigit(source[i]) || source[i] == '_' || source[i] == '.'))
                        i++;
                    tokens.Add(new Token
                    {
                        Type = TokenType.Identifier,
                        Text = source.Substring(start, i - start),
                        Position = start,
                    });
                    continue;
                }

                // Operators, longest first.
                foreach (var op in new[] { "==", "!=", ">=", "<=", "&&", "||", ">", "<", "!", "+", "-", "*", "/" })
                {
                    if (string.CompareOrdinal(source, i, op, 0, op.Length) != 0) continue;
                    tokens.Add(new Token { Type = TokenType.Operator, Text = op, Position = i });
                    i += op.Length;
                    goto next;
                }

                throw new TestFailureException($"Unexpected character '{c}' at position {i} in expression.");
            next: ;
            }

            tokens.Add(new Token { Type = TokenType.End, Text = "<end>", Position = source.Length });
            return tokens;
        }

        // ================================================================ parser / evaluator

        private sealed class Parser
        {
            private readonly List<Token> _tokens;
            private readonly string _source;
            private int _index;

            public Parser(List<Token> tokens, string source) { _tokens = tokens; _source = source; }

            private Token Current => _tokens[_index];

            private bool TakeOperator(params string[] candidates)
            {
                var token = Current;
                bool isWord = token.Type == TokenType.Identifier;
                if (token.Type != TokenType.Operator && !isWord) return false;

                foreach (var candidate in candidates)
                {
                    if (!string.Equals(token.Text, candidate,
                            isWord ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        continue;
                    _index++;
                    return true;
                }
                return false;
            }

            private string PeekOperator(params string[] candidates)
            {
                var token = Current;
                bool isWord = token.Type == TokenType.Identifier;
                if (token.Type != TokenType.Operator && !isWord) return null;
                foreach (var candidate in candidates)
                    if (string.Equals(token.Text, candidate,
                            isWord ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        return candidate;
                return null;
            }

            public void ExpectEnd()
            {
                if (Current.Type != TokenType.End)
                    throw new TestFailureException(
                        $"Unexpected '{Current.Text}' at position {Current.Position} in expression '{_source}'.");
            }

            public object ParseExpression() => ParseOr();

            private object ParseOr()
            {
                var left = ParseAnd();
                while (TakeOperator("or", "||"))
                {
                    var right = ParseAnd();
                    left = Truthy(left) || Truthy(right);
                }
                return left;
            }

            private object ParseAnd()
            {
                var left = ParseNot();
                while (TakeOperator("and", "&&"))
                {
                    var right = ParseNot();
                    left = Truthy(left) && Truthy(right);
                }
                return left;
            }

            private object ParseNot()
            {
                if (TakeOperator("not", "!"))
                    return !Truthy(ParseNot());
                return ParseComparison();
            }

            private object ParseComparison()
            {
                var left = ParseAdditive();

                var op = PeekOperator("==", "!=", ">=", "<=", ">", "<", "contains", "startswith", "endswith", "matches");
                if (op == null) return left;
                _index++;

                var right = ParseAdditive();
                return Compare(left, right, op);
            }

            private object ParseAdditive()
            {
                var left = ParseMultiplicative();
                while (true)
                {
                    if (TakeOperator("+"))
                    {
                        var right = ParseMultiplicative();
                        left = left is string || right is string
                            ? (object)(Stringify(left) + Stringify(right))
                            : ToNumber(left) + ToNumber(right);
                    }
                    else if (TakeOperator("-")) left = ToNumber(left) - ToNumber(ParseMultiplicative());
                    else return left;
                }
            }

            private object ParseMultiplicative()
            {
                var left = ParseUnary();
                while (true)
                {
                    if (TakeOperator("*")) left = ToNumber(left) * ToNumber(ParseUnary());
                    else if (TakeOperator("/"))
                    {
                        double divisor = ToNumber(ParseUnary());
                        if (Math.Abs(divisor) < double.Epsilon)
                            throw new TestFailureException("Division by zero in expression.");
                        left = ToNumber(left) / divisor;
                    }
                    else return left;
                }
            }

            private object ParseUnary()
            {
                if (TakeOperator("-")) return -ToNumber(ParseUnary());
                if (TakeOperator("+")) return ToNumber(ParseUnary());
                return ParsePrimary();
            }

            private object ParsePrimary()
            {
                var token = Current;

                switch (token.Type)
                {
                    case TokenType.Number:
                        _index++;
                        return token.Number;

                    case TokenType.String:
                        _index++;
                        return token.Text;

                    case TokenType.LeftParen:
                    {
                        _index++;
                        var value = ParseExpression();
                        if (Current.Type != TokenType.RightParen)
                            throw new TestFailureException($"Missing ')' in expression '{_source}'.");
                        _index++;
                        return value;
                    }

                    case TokenType.Identifier:
                    {
                        _index++;
                        var name = token.Text;

                        if (Current.Type == TokenType.LeftParen)
                        {
                            _index++;
                            var arguments = new List<object>();
                            if (Current.Type != TokenType.RightParen)
                            {
                                arguments.Add(ParseExpression());
                                while (Current.Type == TokenType.Comma)
                                {
                                    _index++;
                                    arguments.Add(ParseExpression());
                                }
                            }
                            if (Current.Type != TokenType.RightParen)
                                throw new TestFailureException($"Missing ')' after {name}(… in expression '{_source}'.");
                            _index++;
                            return Functions.Call(name, arguments);
                        }

                        return Values.Read(name);
                    }

                    default:
                        throw new TestFailureException(
                            $"Unexpected '{token.Text}' at position {token.Position} in expression '{_source}'.");
                }
            }
        }

        // ================================================================ semantics

        private static object Compare(object left, object right, string op)
        {
            switch (op.ToLowerInvariant())
            {
                case "contains":
                    return Stringify(left).IndexOf(Stringify(right), StringComparison.OrdinalIgnoreCase) >= 0;
                case "startswith":
                    return Stringify(left).StartsWith(Stringify(right), StringComparison.OrdinalIgnoreCase);
                case "endswith":
                    return Stringify(left).EndsWith(Stringify(right), StringComparison.OrdinalIgnoreCase);
                case "matches":
                    return System.Text.RegularExpressions.Regex.IsMatch(Stringify(left), Stringify(right));
            }

            if (op == "==" || op == "!=")
            {
                bool equal = AreEqual(left, right);
                return op == "==" ? equal : !equal;
            }

            double a = ToNumber(left), b = ToNumber(right);
            switch (op)
            {
                case ">": return a > b;
                case ">=": return a >= b;
                case "<": return a < b;
                case "<=": return a <= b;
                default: throw new TestFailureException($"Unknown operator '{op}'.");
            }
        }

        private static bool AreEqual(object left, object right)
        {
            if (left == null || right == null) return left == null && right == null;
            if (left is bool || right is bool) return Truthy(left) == Truthy(right);
            if (IsNumeric(left) && IsNumeric(right)) return Math.Abs(ToNumber(left) - ToNumber(right)) < 1e-6;
            return string.Equals(Stringify(left), Stringify(right), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNumeric(object value) =>
            value is double || value is float || value is int || value is long ||
            (value is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _));

        internal static double ToNumber(object value)
        {
            switch (value)
            {
                case null: return 0;
                case bool b: return b ? 1 : 0;
                case double d: return d;
                case float f: return f;
                case int i: return i;
                case long l: return l;
                case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                    return parsed;
                default:
                    throw new TestFailureException($"Cannot use {Describe(value)} as a number.");
            }
        }

        internal static string Stringify(object value)
        {
            switch (value)
            {
                case null: return "";
                case bool b: return b ? "true" : "false";
                case double d: return d.ToString("0.####", CultureInfo.InvariantCulture);
                default: return value.ToString();
            }
        }

        /// <summary>Built-in readable values, then anything registered with <see cref="GameTestBindings"/>.</summary>
        private static class Values
        {
            public static object Read(string path)
            {
                switch (path.ToLowerInvariant())
                {
                    case "true": return true;
                    case "false": return false;
                    case "null": return null;
                    case "scene": return SceneManager.GetActiveScene().name;
                    case "time": return (double)Time.time;
                    case "realtime": return (double)Time.realtimeSinceStartup;
                    case "timescale": return (double)Time.timeScale;
                    case "framecount": return (double)Time.frameCount;
                    case "fps": return Time.unscaledDeltaTime > 0f ? 1.0 / Time.unscaledDeltaTime : 0.0;
                    case "screen.width": return (double)Screen.width;
                    case "screen.height": return (double)Screen.height;
                }

                if (GameTestBindings.TryGetValue(path, out var value))
                    return Normalize(value);

                var bindings = GameTestBindings.Describe();
                var known = new List<string>();
                foreach (var binding in bindings)
                    if (binding.Kind == BindingKind.Value) known.Add(binding.Path);

                throw new TestFailureException(
                    $"Unknown value '{path}'. Bind it with GameTestBindings.BindValue(\"{path}\", …). " +
                    (known.Count > 0 ? $"Currently bound: {string.Join(", ", known)}." : "No values are bound yet."));
            }

            private static object Normalize(object value)
            {
                switch (value)
                {
                    case null: return null;
                    case bool b: return b;
                    case string s: return s;
                    case float f: return (double)f;
                    case int i: return (double)i;
                    case long l: return (double)l;
                    case double d: return d;
                    case decimal m: return (double)m;
                    case Enum e: return e.ToString();
                    default: return value.ToString();
                }
            }
        }

        /// <summary>Functions that let an expression ask about the screen.</summary>
        private static class Functions
        {
            public static object Call(string name, List<object> args)
            {
                string Selector(int index)
                {
                    if (index >= args.Count)
                        throw new TestFailureException($"{name}() needs a selector argument.");
                    return Stringify(args[index]);
                }

                switch (name.ToLowerInvariant())
                {
                    case "exists":
                        return Locator.FindAll(Selector(0)).Count > 0;

                    case "count":
                        return (double)Locator.FindAll(Selector(0)).Count;

                    case "visible":
                    {
                        var matches = Locator.FindAll(Selector(0));
                        foreach (var go in matches)
                            if (UiProbe.IsVisible(go)) return true;
                        return false;
                    }

                    case "interactable":
                    {
                        var matches = Locator.FindAll(Selector(0));
                        foreach (var go in matches)
                            if (UiProbe.IsInteractable(go)) return true;
                        return false;
                    }

                    case "blocked":
                    {
                        var go = Locator.Find(Selector(0));
                        if (go == null) return false;
                        return !UiProbe.IsHitTestable(go, UiProbe.ScreenPointOf(go), out _);
                    }

                    case "text":
                    case "label":
                    {
                        var go = Locator.Find(Selector(0));
                        if (go == null) return "";
                        var text = name.ToLowerInvariant() == "text" ? UiProbe.TextOf(go) : UiProbe.LabelOf(go);
                        return text ?? "";
                    }

                    case "sceneloaded":
                    {
                        var wanted = Selector(0);
                        for (int i = 0; i < SceneManager.sceneCount; i++)
                        {
                            var scene = SceneManager.GetSceneAt(i);
                            if (scene.isLoaded && string.Equals(scene.name, wanted, StringComparison.OrdinalIgnoreCase))
                                return true;
                        }
                        return false;
                    }

                    case "abs": return Math.Abs(ToNumber(args.Count > 0 ? args[0] : null));
                    case "min": return Math.Min(ToNumber(args[0]), ToNumber(args[1]));
                    case "max": return Math.Max(ToNumber(args[0]), ToNumber(args[1]));
                    case "round": return Math.Round(ToNumber(args[0]));
                    case "len": return (double)Stringify(args.Count > 0 ? args[0] : null).Length;

                    default:
                        throw new TestFailureException(
                            $"Unknown function '{name}()'. Available: exists, count, visible, interactable, blocked, " +
                            "text, label, sceneLoaded, abs, min, max, round, len.");
                }
            }
        }
    }
}
