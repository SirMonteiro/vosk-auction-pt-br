using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AuctionVoice.ASR
{
    /// <summary>
    /// A small, allocation-conscious JSON reader for the handful of shapes Vosk returns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Unity's <c>JsonUtility</c> cannot parse Vosk's results: the same object is sometimes
    /// <c>{"text": ...}</c> and sometimes <c>{"alternatives": [...]}</c>, and partial results use
    /// <c>{"partial": ...}</c>. Depending on Newtonsoft would add a package dependency to what is
    /// meant to be a drop-in UPM package.
    /// </para>
    /// <para>
    /// This reader supports the subset Vosk actually emits: objects, arrays, strings, numbers,
    /// booleans and null. It is strict about structure but never throws on unexpected keys.
    /// </para>
    /// </remarks>
    public sealed class JsonValue
    {
        private readonly Dictionary<string, JsonValue> _members;
        private readonly List<JsonValue> _items;
        private readonly string _string;
        private readonly double _number;
        private readonly bool _bool;

        /// <summary>Value kind.</summary>
        public JsonKind Kind { get; }

        private JsonValue(JsonKind kind)
        {
            Kind = kind;
        }

        private JsonValue(string value)
        {
            Kind = JsonKind.String;
            _string = value;
        }

        private JsonValue(double value)
        {
            Kind = JsonKind.Number;
            _number = value;
        }

        private JsonValue(bool value)
        {
            Kind = JsonKind.Boolean;
            _bool = value;
        }

        private JsonValue(Dictionary<string, JsonValue> members)
        {
            Kind = JsonKind.Object;
            _members = members;
        }

        private JsonValue(List<JsonValue> items)
        {
            Kind = JsonKind.Array;
            _items = items;
        }

        /// <summary>The parsed JSON <c>null</c>.</summary>
        public static readonly JsonValue Null = new JsonValue(JsonKind.Null);

        /// <summary>Member access; returns <see cref="Null"/> for missing keys.</summary>
        public JsonValue this[string key]
        {
            get
            {
                if (_members != null && key != null && _members.TryGetValue(key, out JsonValue value))
                {
                    return value;
                }

                return Null;
            }
        }

        /// <summary>Element access; returns <see cref="Null"/> when out of range.</summary>
        public JsonValue this[int index]
        {
            get
            {
                if (_items != null && index >= 0 && index < _items.Count)
                {
                    return _items[index];
                }

                return Null;
            }
        }

        /// <summary>Number of elements when this is an array, otherwise 0.</summary>
        public int Count => _items?.Count ?? 0;

        /// <summary>String content, or <c>null</c> when this is not a string.</summary>
        public string AsString => _string;

        /// <summary>Numeric content, or 0 when this is not a number.</summary>
        public double AsNumber => _number;

        /// <summary>Boolean content, or false when this is not a boolean.</summary>
        public bool AsBool => _bool;

        /// <summary>String content, or <paramref name="fallback"/> when this is not a string.</summary>
        public string AsStringOr(string fallback)
        {
            return Kind == JsonKind.String ? _string : fallback;
        }

        /// <summary>Double content, or <paramref name="fallback"/> when this is not a number.</summary>
        public double AsNumberOr(double fallback)
        {
            return Kind == JsonKind.Number ? _number : fallback;
        }

        /// <summary>True when the member exists and is not JSON null.</summary>
        public bool Has(string key)
        {
            return _members != null && key != null && _members.ContainsKey(key);
        }

        /// <summary>Enumerates array elements; empty when this is not an array.</summary>
        public IReadOnlyList<JsonValue> Items =>
            (IReadOnlyList<JsonValue>)_items ?? Array.Empty<JsonValue>();

        /// <summary>Parses a JSON document. Returns <see cref="Null"/> on malformed input.</summary>
        public static JsonValue Parse(string json)
        {
            if (string.IsNullOrEmpty(json))
            {
                return Null;
            }

            int index = 0;
            try
            {
                JsonValue value = ParseValue(json, ref index);
                return value ?? Null;
            }
            catch (FormatException)
            {
                return Null;
            }
            catch (ArgumentOutOfRangeException)
            {
                return Null;
            }
        }

        private static JsonValue ParseValue(string json, ref int index)
        {
            SkipWhitespace(json, ref index);
            if (index >= json.Length)
            {
                return Null;
            }

            char c = json[index];
            switch (c)
            {
                case '{':
                    return ParseObject(json, ref index);
                case '[':
                    return ParseArray(json, ref index);
                case '"':
                    return new JsonValue(ParseString(json, ref index));
                case 't':
                    Expect(json, ref index, "true");
                    return new JsonValue(true);
                case 'f':
                    Expect(json, ref index, "false");
                    return new JsonValue(false);
                case 'n':
                    Expect(json, ref index, "null");
                    return Null;
                default:
                    return new JsonValue(ParseNumber(json, ref index));
            }
        }

        private static JsonValue ParseObject(string json, ref int index)
        {
            var members = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            index++; // '{'
            SkipWhitespace(json, ref index);
            if (index < json.Length && json[index] == '}')
            {
                index++;
                return new JsonValue(members);
            }

            while (index < json.Length)
            {
                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != '"')
                {
                    throw new FormatException("expected a member name");
                }

                string key = ParseString(json, ref index);
                SkipWhitespace(json, ref index);
                if (index >= json.Length || json[index] != ':')
                {
                    throw new FormatException("expected ':'");
                }

                index++;
                members[key] = ParseValue(json, ref index);
                SkipWhitespace(json, ref index);
                if (index >= json.Length)
                {
                    throw new FormatException("unterminated object");
                }

                if (json[index] == ',')
                {
                    index++;
                    continue;
                }

                if (json[index] == '}')
                {
                    index++;
                    return new JsonValue(members);
                }

                throw new FormatException("expected ',' or '}'");
            }

            throw new FormatException("unterminated object");
        }

        private static JsonValue ParseArray(string json, ref int index)
        {
            var items = new List<JsonValue>(4);
            index++; // '['
            SkipWhitespace(json, ref index);
            if (index < json.Length && json[index] == ']')
            {
                index++;
                return new JsonValue(items);
            }

            while (index < json.Length)
            {
                items.Add(ParseValue(json, ref index));
                SkipWhitespace(json, ref index);
                if (index >= json.Length)
                {
                    throw new FormatException("unterminated array");
                }

                if (json[index] == ',')
                {
                    index++;
                    continue;
                }

                if (json[index] == ']')
                {
                    index++;
                    return new JsonValue(items);
                }

                throw new FormatException("expected ',' or ']'");
            }

            throw new FormatException("unterminated array");
        }

        private static string ParseString(string json, ref int index)
        {
            index++; // opening quote
            var builder = new StringBuilder(16);
            while (index < json.Length)
            {
                char c = json[index++];
                if (c == '"')
                {
                    return builder.ToString();
                }

                if (c != '\\')
                {
                    builder.Append(c);
                    continue;
                }

                if (index >= json.Length)
                {
                    break;
                }

                char escape = json[index++];
                switch (escape)
                {
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case '/': builder.Append('/'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'u':
                        if (index + 4 > json.Length)
                        {
                            throw new FormatException("truncated \\u escape");
                        }

                        builder.Append((char)int.Parse(
                            json.Substring(index, 4),
                            NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture));
                        index += 4;
                        break;
                    default:
                        throw new FormatException("invalid escape");
                }
            }

            throw new FormatException("unterminated string");
        }

        private static double ParseNumber(string json, ref int index)
        {
            int start = index;
            while (index < json.Length)
            {
                char c = json[index];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E')
                {
                    index++;
                    continue;
                }

                break;
            }

            if (start == index)
            {
                throw new FormatException("expected a number");
            }

            return double.Parse(
                json.Substring(start, index - start),
                NumberStyles.Float,
                CultureInfo.InvariantCulture);
        }

        private static void Expect(string json, ref int index, string literal)
        {
            if (index + literal.Length > json.Length
                || string.CompareOrdinal(json, index, literal, 0, literal.Length) != 0)
            {
                throw new FormatException("expected " + literal);
            }

            index += literal.Length;
        }

        private static void SkipWhitespace(string json, ref int index)
        {
            while (index < json.Length && char.IsWhiteSpace(json[index]))
            {
                index++;
            }
        }
    }

    /// <summary>JSON value kinds.</summary>
    public enum JsonKind
    {
        /// <summary>JSON <c>null</c>.</summary>
        Null = 0,

        /// <summary>Object.</summary>
        Object = 1,

        /// <summary>Array.</summary>
        Array = 2,

        /// <summary>String.</summary>
        String = 3,

        /// <summary>Number.</summary>
        Number = 4,

        /// <summary>Boolean.</summary>
        Boolean = 5
    }
}
