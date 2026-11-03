using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Tutan.Logging
{
    /// <summary>
    /// One JSON value, as a line of a log's file holds it: null, a bool, a number, a string, an
    /// array or an object. A number keeps its text, so a <see cref="long"/>, a <see cref="ulong"/>
    /// and a <see cref="decimal"/> read back exactly, each as its own reader asks.
    /// </summary>
    /// <remarks>
    /// Read by hand, as <see cref="JsonLine"/> is written, since Unity has no <c>System.Text.Json</c>.
    /// It reads JSON as RFC 8259 has it, and nothing looser: no comments, no trailing commas, no
    /// unescaped control chars in strings. An object that names a member twice keeps the last.
    /// </remarks>
    internal abstract class JsonValue
    {
        /// <summary>The deepest values nest in a line it reads, which no line of the format comes near.</summary>
        private const int MostDepth = 64;

        private JsonValue()
        {
        }

        /// <summary><paramref name="text"/> as one JSON value, with only spaces around it, or null when it is not one.</summary>
        public static JsonValue? Parse(string? text)
        {
            if (text is null)
                return null;

            var reader = new Reader(text);
            JsonValue? value = reader.Value(0);
            return value is not null && reader.AtEnd() ? value : null;
        }

        /// <summary>The member <paramref name="name"/> of an object, if this is one and has it.</summary>
        public bool TryGet(string name, out JsonValue value)
        {
            if (this is Members members && members.ByName.TryGetValue(name, out JsonValue? found))
            {
                value = found;
                return true;
            }

            value = Null.Value;
            return false;
        }

        /// <summary>JSON's null.</summary>
        public sealed class Null : JsonValue
        {
            public static readonly Null Value = new Null();

            private Null()
            {
            }
        }

        /// <summary><c>true</c> or <c>false</c>.</summary>
        public sealed class Bool : JsonValue
        {
            public static readonly Bool True = new Bool(true);
            public static readonly Bool False = new Bool(false);

            private Bool(bool value) => Value = value;

            public bool Value { get; }
        }

        /// <summary>A number, as its text, which each reader reads as the type it asks for.</summary>
        public sealed class Number : JsonValue
        {
            public Number(string text) => Text = text;

            public string Text { get; }

            /// <summary>The number as a <see cref="long"/>, when it is a whole number written without a fraction or an exponent, in range.</summary>
            public bool TryInt64(out long value) =>
                long.TryParse(Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

            /// <summary>The number as a <see cref="ulong"/>, when it is a whole number from 0 written without a fraction or an exponent, in range.</summary>
            public bool TryUInt64(out ulong value) =>
                ulong.TryParse(Text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

            /// <summary>The number as a finite <see cref="double"/>.</summary>
            public bool TryDouble(out double value) =>
                double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

            /// <summary>The number as a finite <see cref="float"/>.</summary>
            public bool TrySingle(out float value) =>
                float.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && float.IsFinite(value);

            /// <summary>The number as a <see cref="decimal"/>, in range.</summary>
            public bool TryDecimal(out decimal value) =>
                decimal.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>A string, its escapes read.</summary>
        public sealed class Text : JsonValue
        {
            public Text(string value) => Value = value;

            public string Value { get; }
        }

        /// <summary>An array: its items, in order.</summary>
        public sealed class Items : JsonValue
        {
            public Items(IReadOnlyList<JsonValue> all) => All = all;

            public IReadOnlyList<JsonValue> All { get; }
        }

        /// <summary>An object: its members, by name.</summary>
        public sealed class Members : JsonValue
        {
            public Members(IReadOnlyDictionary<string, JsonValue> byName) => ByName = byName;

            public IReadOnlyDictionary<string, JsonValue> ByName { get; }
        }

        /// <summary>Reads one value from a string, from where it is on.</summary>
        private sealed class Reader
        {
            private readonly string _text;
            private readonly StringBuilder _chars = new StringBuilder();
            private int _at;

            public Reader(string text) => _text = text;

            /// <summary>Whether only spaces are left.</summary>
            public bool AtEnd()
            {
                SkipSpaces();
                return _at == _text.Length;
            }

            public JsonValue? Value(int depth)
            {
                SkipSpaces();
                if (_at == _text.Length || depth > MostDepth)
                    return null;

                char c = _text[_at];
                switch (c)
                {
                    case '{':
                        return ReadObject(depth);
                    case '[':
                        return ReadArray(depth);
                    case '"':
                        string? text = ReadString();
                        return text is null ? null : new Text(text);
                    case 't':
                        return ReadWord("true") ? Bool.True : null;
                    case 'f':
                        return ReadWord("false") ? Bool.False : null;
                    case 'n':
                        return ReadWord("null") ? Null.Value : null;
                    default:
                        return c == '-' || IsDigit(c) ? ReadNumber() : null;
                }
            }

            private static bool IsDigit(char c) => c >= '0' && c <= '9';

            private JsonValue? ReadObject(int depth)
            {
                _at++;
                var members = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
                SkipSpaces();
                if (Take('}'))
                    return new Members(members);

                while (true)
                {
                    SkipSpaces();
                    if (_at == _text.Length || _text[_at] != '"')
                        return null;

                    string? name = ReadString();
                    SkipSpaces();
                    if (name is null || !Take(':'))
                        return null;

                    JsonValue? value = Value(depth + 1);
                    if (value is null)
                        return null;

                    members[name] = value;
                    SkipSpaces();
                    if (Take('}'))
                        return new Members(members);
                    if (!Take(','))
                        return null;
                }
            }

            private JsonValue? ReadArray(int depth)
            {
                _at++;
                var items = new List<JsonValue>();
                SkipSpaces();
                if (Take(']'))
                    return new Items(items);

                while (true)
                {
                    JsonValue? item = Value(depth + 1);
                    if (item is null)
                        return null;

                    items.Add(item);
                    SkipSpaces();
                    if (Take(']'))
                        return new Items(items);
                    if (!Take(','))
                        return null;
                }
            }

            /// <summary>A string from the quote at the reader to the one that closes it, its escapes read, or null when it is not one.</summary>
            private string? ReadString()
            {
                _at++;
                _chars.Clear();
                while (_at < _text.Length)
                {
                    char c = _text[_at++];
                    if (c == '"')
                        return _chars.ToString();
                    if (c < ' ')
                        return null;
                    if (c != '\\')
                    {
                        _chars.Append(c);
                        continue;
                    }

                    if (_at == _text.Length)
                        return null;

                    char escape = _text[_at++];
                    switch (escape)
                    {
                        case '"': _chars.Append('"'); break;
                        case '\\': _chars.Append('\\'); break;
                        case '/': _chars.Append('/'); break;
                        case 'b': _chars.Append('\b'); break;
                        case 'f': _chars.Append('\f'); break;
                        case 'n': _chars.Append('\n'); break;
                        case 'r': _chars.Append('\r'); break;
                        case 't': _chars.Append('\t'); break;
                        case 'u':
                            if (_at + 4 > _text.Length
                                || !int.TryParse(_text.Substring(_at, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int code))
                                return null;
                            _chars.Append((char)code);
                            _at += 4;
                            break;
                        default:
                            return null;
                    }
                }

                return null;
            }

            /// <summary>A number as JSON writes one: an optional minus, digits with no leading zero, then an optional fraction and exponent.</summary>
            private JsonValue? ReadNumber()
            {
                int start = _at;
                Take('-');
                if (!Take('0') && !Digits())
                    return null;

                if (Take('.') && !Digits())
                    return null;

                if (_at < _text.Length && (_text[_at] == 'e' || _text[_at] == 'E'))
                {
                    _at++;
                    if (!Take('+'))
                        Take('-');
                    if (!Digits())
                        return null;
                }

                return new Number(_text.Substring(start, _at - start));
            }

            /// <summary>Moves past one digit or more, and says whether there was one.</summary>
            private bool Digits()
            {
                int start = _at;
                while (_at < _text.Length && IsDigit(_text[_at]))
                    _at++;
                return _at > start;
            }

            private bool ReadWord(string word)
            {
                if (string.CompareOrdinal(_text, _at, word, 0, word.Length) != 0)
                    return false;

                _at += word.Length;
                return true;
            }

            private bool Take(char c)
            {
                if (_at == _text.Length || _text[_at] != c)
                    return false;

                _at++;
                return true;
            }

            private void SkipSpaces()
            {
                while (_at < _text.Length && _text[_at] is ' ' or '\t' or '\n' or '\r')
                    _at++;
            }
        }
    }
}
