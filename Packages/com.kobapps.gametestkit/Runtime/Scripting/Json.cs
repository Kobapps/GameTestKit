using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Kobapps.GameTestKit.Scripting
{
    public enum JsonType { Null, Bool, Number, String, Array, Object }

    /// <summary>
    /// A tiny, allocation-sane JSON DOM. Unity's <c>JsonUtility</c> only maps onto fixed serializable
    /// classes, which cannot express a test script's polymorphic step objects — so scripts are parsed
    /// with this instead. It is also what writes the JSON report.
    /// </summary>
    public sealed class JsonValue : IEnumerable<JsonValue>
    {
        public JsonType Type { get; private set; }

        private bool _bool;
        private double _number;
        private string _string;
        private List<JsonValue> _array;
        private Dictionary<string, JsonValue> _object;
        private List<string> _keyOrder;

        public static readonly JsonValue Null = new JsonValue { Type = JsonType.Null };

        // ---------------------------------------------------------------- construction

        public static JsonValue NewObject() => new JsonValue
        {
            Type = JsonType.Object,
            _object = new Dictionary<string, JsonValue>(StringComparer.Ordinal),
            _keyOrder = new List<string>(),
        };

        public static JsonValue NewArray() => new JsonValue
        {
            Type = JsonType.Array,
            _array = new List<JsonValue>(),
        };

        public static JsonValue New(string value) =>
            value == null ? Null : new JsonValue { Type = JsonType.String, _string = value };

        public static JsonValue New(double value) => new JsonValue { Type = JsonType.Number, _number = value };

        public static JsonValue New(bool value) => new JsonValue { Type = JsonType.Bool, _bool = value };

        // ---------------------------------------------------------------- access

        public bool IsNull => Type == JsonType.Null;
        public bool IsObject => Type == JsonType.Object;
        public bool IsArray => Type == JsonType.Array;
        public bool IsString => Type == JsonType.String;
        public bool IsNumber => Type == JsonType.Number;
        public bool IsBool => Type == JsonType.Bool;

        public int Count => Type == JsonType.Array ? _array.Count : Type == JsonType.Object ? _keyOrder.Count : 0;

        public IReadOnlyList<string> Keys => Type == JsonType.Object ? (IReadOnlyList<string>)_keyOrder : Array.Empty<string>();

        /// <summary>Indexer that never throws: a missing key or index yields <see cref="Null"/>.</summary>
        public JsonValue this[string key]
        {
            get
            {
                if (Type == JsonType.Object && _object.TryGetValue(key, out var value)) return value;
                return Null;
            }
            set
            {
                if (Type != JsonType.Object) throw new InvalidOperationException("Not a JSON object.");
                if (!_object.ContainsKey(key)) _keyOrder.Add(key);
                _object[key] = value ?? Null;
            }
        }

        public JsonValue this[int index]
        {
            get
            {
                if (Type == JsonType.Array && index >= 0 && index < _array.Count) return _array[index];
                return Null;
            }
        }

        public bool Has(string key) => Type == JsonType.Object && _object.ContainsKey(key);

        public JsonValue Add(JsonValue value)
        {
            if (Type != JsonType.Array) throw new InvalidOperationException("Not a JSON array.");
            _array.Add(value ?? Null);
            return this;
        }

        public JsonValue Set(string key, JsonValue value) { this[key] = value; return this; }
        public JsonValue Set(string key, string value) { this[key] = New(value); return this; }
        public JsonValue Set(string key, double value) { this[key] = New(value); return this; }
        public JsonValue Set(string key, bool value) { this[key] = New(value); return this; }

        public IEnumerator<JsonValue> GetEnumerator() =>
            (Type == JsonType.Array ? _array : new List<JsonValue>()).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        // ---------------------------------------------------------------- conversion

        public string AsString(string fallback = null)
        {
            switch (Type)
            {
                case JsonType.String: return _string;
                case JsonType.Number: return _number.ToString(CultureInfo.InvariantCulture);
                case JsonType.Bool: return _bool ? "true" : "false";
                default: return fallback;
            }
        }

        public double AsNumber(double fallback = 0)
        {
            if (Type == JsonType.Number) return _number;
            if (Type == JsonType.Bool) return _bool ? 1 : 0;
            if (Type == JsonType.String &&
                double.TryParse(_string, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
            return fallback;
        }

        public float AsFloat(float fallback = 0f) => (float)AsNumber(fallback);

        public int AsInt(int fallback = 0) => (int)Math.Round(AsNumber(fallback));

        public bool AsBool(bool fallback = false)
        {
            if (Type == JsonType.Bool) return _bool;
            if (Type == JsonType.Number) return Math.Abs(_number) > double.Epsilon;
            if (Type == JsonType.String) return string.Equals(_string, "true", StringComparison.OrdinalIgnoreCase);
            return fallback;
        }

        /// <summary>The value as a plain CLR object (string/double/bool/null) for expression evaluation.</summary>
        public object AsObject()
        {
            switch (Type)
            {
                case JsonType.String: return _string;
                case JsonType.Number: return _number;
                case JsonType.Bool: return _bool;
                default: return null;
            }
        }

        public List<string> AsStringList()
        {
            var list = new List<string>();
            if (Type == JsonType.Array)
                foreach (var item in _array) list.Add(item.AsString());
            else if (Type == JsonType.String) list.Add(_string);
            return list;
        }

        // ---------------------------------------------------------------- writing

        public override string ToString() => ToJson(false);

        public string ToJson(bool pretty = true)
        {
            var builder = new StringBuilder(256);
            Write(builder, pretty, 0);
            return builder.ToString();
        }

        private void Write(StringBuilder builder, bool pretty, int depth)
        {
            switch (Type)
            {
                case JsonType.Null: builder.Append("null"); break;
                case JsonType.Bool: builder.Append(_bool ? "true" : "false"); break;
                case JsonType.Number: builder.Append(FormatNumber(_number)); break;
                case JsonType.String: WriteString(builder, _string); break;

                case JsonType.Array:
                    if (_array.Count == 0) { builder.Append("[]"); break; }
                    builder.Append('[');
                    for (int i = 0; i < _array.Count; i++)
                    {
                        if (i > 0) builder.Append(',');
                        NewLine(builder, pretty, depth + 1);
                        _array[i].Write(builder, pretty, depth + 1);
                    }
                    NewLine(builder, pretty, depth);
                    builder.Append(']');
                    break;

                case JsonType.Object:
                    if (_keyOrder.Count == 0) { builder.Append("{}"); break; }
                    builder.Append('{');
                    for (int i = 0; i < _keyOrder.Count; i++)
                    {
                        if (i > 0) builder.Append(',');
                        NewLine(builder, pretty, depth + 1);
                        WriteString(builder, _keyOrder[i]);
                        builder.Append(pretty ? ": " : ":");
                        _object[_keyOrder[i]].Write(builder, pretty, depth + 1);
                    }
                    NewLine(builder, pretty, depth);
                    builder.Append('}');
                    break;
            }
        }

        private static void NewLine(StringBuilder builder, bool pretty, int depth)
        {
            if (!pretty) return;
            builder.Append('\n');
            builder.Append(' ', depth * 2);
        }

        private static string FormatNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "null";
            if (Math.Abs(value - Math.Round(value)) < 1e-9 && Math.Abs(value) < 1e15)
                return ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static void WriteString(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (var c in value ?? string.Empty)
            {
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c == 0x7f) builder.Append("\\u").Append(((int)c).ToString("x4"));
                        else builder.Append(c);
                        break;
                }
            }
            builder.Append('"');
        }

        // ---------------------------------------------------------------- parsing

        /// <summary>Parses JSON text. Comments (<c>//</c> and <c>/* */</c>) and trailing commas are tolerated.</summary>
        public static JsonValue Parse(string text)
        {
            if (text == null) throw new JsonParseException("Input is null.", 0, null);
            int index = 0;
            var value = ParseValue(text, ref index);
            SkipWhitespace(text, ref index);
            if (index < text.Length)
                throw new JsonParseException($"Unexpected trailing content '{text[index]}'.", index, text);
            return value;
        }

        public static bool TryParse(string text, out JsonValue value, out string error)
        {
            try { value = Parse(text); error = null; return true; }
            catch (JsonParseException e) { value = Null; error = e.Message; return false; }
        }

        private static JsonValue ParseValue(string text, ref int index)
        {
            SkipWhitespace(text, ref index);
            if (index >= text.Length) throw new JsonParseException("Unexpected end of input.", index, text);

            char c = text[index];
            switch (c)
            {
                case '{': return ParseObject(text, ref index);
                case '[': return ParseArray(text, ref index);
                case '"': return New(ParseString(text, ref index));
                case 't':
                    Expect(text, ref index, "true");
                    return New(true);
                case 'f':
                    Expect(text, ref index, "false");
                    return New(false);
                case 'n':
                    Expect(text, ref index, "null");
                    return Null;
                default:
                    return New(ParseNumber(text, ref index));
            }
        }

        private static JsonValue ParseObject(string text, ref int index)
        {
            var result = NewObject();
            index++; // {
            while (true)
            {
                SkipWhitespace(text, ref index);
                if (index >= text.Length) throw new JsonParseException("Unterminated object.", index, text);
                if (text[index] == '}') { index++; return result; }

                if (text[index] != '"')
                    throw new JsonParseException($"Expected a quoted key but found '{text[index]}'.", index, text);

                var key = ParseString(text, ref index);
                SkipWhitespace(text, ref index);
                if (index >= text.Length || text[index] != ':')
                    throw new JsonParseException($"Expected ':' after key '{key}'.", index, text);
                index++;

                result[key] = ParseValue(text, ref index);

                SkipWhitespace(text, ref index);
                if (index < text.Length && text[index] == ',') { index++; continue; }
            }
        }

        private static JsonValue ParseArray(string text, ref int index)
        {
            var result = NewArray();
            index++; // [
            while (true)
            {
                SkipWhitespace(text, ref index);
                if (index >= text.Length) throw new JsonParseException("Unterminated array.", index, text);
                if (text[index] == ']') { index++; return result; }

                result.Add(ParseValue(text, ref index));

                SkipWhitespace(text, ref index);
                if (index < text.Length && text[index] == ',') { index++; continue; }
            }
        }

        private static string ParseString(string text, ref int index)
        {
            var builder = new StringBuilder();
            index++; // opening quote
            while (index < text.Length)
            {
                char c = text[index++];
                if (c == '"') return builder.ToString();
                if (c != '\\') { builder.Append(c); continue; }

                if (index >= text.Length) break;
                char escape = text[index++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'u':
                        if (index + 4 > text.Length)
                            throw new JsonParseException("Truncated \\u escape.", index, text);
                        builder.Append((char)Convert.ToInt32(text.Substring(index, 4), 16));
                        index += 4;
                        break;
                    default:
                        throw new JsonParseException($"Unknown escape '\\{escape}'.", index, text);
                }
            }
            throw new JsonParseException("Unterminated string.", index, text);
        }

        private static double ParseNumber(string text, ref int index)
        {
            int start = index;
            if (index < text.Length && (text[index] == '-' || text[index] == '+')) index++;
            while (index < text.Length &&
                   (char.IsDigit(text[index]) || text[index] == '.' ||
                    text[index] == 'e' || text[index] == 'E' ||
                    ((text[index] == '-' || text[index] == '+') && (text[index - 1] == 'e' || text[index - 1] == 'E'))))
                index++;

            var slice = text.Substring(start, index - start);
            if (double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                return value;
            throw new JsonParseException($"'{slice}' is not a valid number.", start, text);
        }

        private static void Expect(string text, ref int index, string literal)
        {
            if (index + literal.Length > text.Length ||
                string.CompareOrdinal(text, index, literal, 0, literal.Length) != 0)
                throw new JsonParseException($"Expected '{literal}'.", index, text);
            index += literal.Length;
        }

        private static void SkipWhitespace(string text, ref int index)
        {
            while (index < text.Length)
            {
                char c = text[index];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') { index++; continue; }

                if (c == '/' && index + 1 < text.Length)
                {
                    if (text[index + 1] == '/')
                    {
                        while (index < text.Length && text[index] != '\n') index++;
                        continue;
                    }
                    if (text[index + 1] == '*')
                    {
                        index += 2;
                        while (index + 1 < text.Length && !(text[index] == '*' && text[index + 1] == '/')) index++;
                        index = Math.Min(text.Length, index + 2);
                        continue;
                    }
                }
                return;
            }
        }
    }

    /// <summary>A JSON syntax error with a line/column, so a failed script points at the right place.</summary>
    public sealed class JsonParseException : Exception
    {
        public int Index { get; }
        public int Line { get; }
        public int Column { get; }

        public JsonParseException(string message, int index, string source)
            : base(Describe(message, index, source))
        {
            Index = index;
            if (source != null)
            {
                Line = 1;
                Column = 1;
                for (int i = 0; i < index && i < source.Length; i++)
                {
                    if (source[i] == '\n') { Line++; Column = 1; }
                    else Column++;
                }
            }
        }

        private static string Describe(string message, int index, string source)
        {
            if (source == null) return message;
            int line = 1, column = 1;
            for (int i = 0; i < index && i < source.Length; i++)
            {
                if (source[i] == '\n') { line++; column = 1; }
                else column++;
            }
            return $"{message} (line {line}, column {column})";
        }
    }
}
