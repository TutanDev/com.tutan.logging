using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Tutan.Logging
{
    /// <summary>
    /// Writes one JSON object at a time into a buffer of chars, then as a line of UTF-8 into a stream.
    /// Its buffers grow to the longest line and stay, so writing a line allocates nothing after that.
    /// </summary>
    /// <remarks>
    /// Written by hand because Unity has no <c>System.Text.Json</c>. It writes only what a sink needs:
    /// objects, arrays, strings, numbers, bools and null, with every char JSON requires escaped. A
    /// number that is not finite is written as a string, since JSON has no such number.
    /// </remarks>
    internal sealed class JsonLine
    {
        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        private char[] _chars = new char[1024];
        private byte[] _bytes = new byte[4096];
        private int _length;
        private bool _comma;

        /// <summary>Starts a line: an empty object.</summary>
        public void Begin()
        {
            _length = 0;
            _comma = false;
            Raw('{');
        }

        /// <summary>Ends the line's object and writes the line, with its newline, to <paramref name="stream"/>.</summary>
        public void End(Stream stream)
        {
            Raw('}');
            Raw('\n');
            int most = s_utf8.GetMaxByteCount(_length);
            if (_bytes.Length < most)
                _bytes = new byte[Math.Max(most, _bytes.Length * 2)];

            int count = s_utf8.GetBytes(_chars, 0, _length, _bytes, 0);
            stream.Write(_bytes, 0, count);
        }

        /// <summary>What the line holds so far, for a test.</summary>
        public override string ToString() => new string(_chars, 0, _length);

        public void Name(string name)
        {
            Separate();
            Quoted(name.AsSpan());
            Raw(':');
            _comma = false;
        }

        public void StartObject()
        {
            Separate();
            Raw('{');
            _comma = false;
        }

        public void EndObject()
        {
            Raw('}');
            _comma = true;
        }

        public void StartArray()
        {
            Separate();
            Raw('[');
            _comma = false;
        }

        public void EndArray()
        {
            Raw(']');
            _comma = true;
        }

        public void String(ReadOnlySpan<char> value)
        {
            Separate();
            Quoted(value);
            _comma = true;
        }

        public void Null()
        {
            Separate();
            Raw("null");
            _comma = true;
        }

        public void Bool(bool value)
        {
            Separate();
            Raw(value ? "true" : "false");
            _comma = true;
        }

        public void Number(long value)
        {
            Separate();
            Ensure(20);
            value.TryFormat(_chars.AsSpan(_length), out int written, default, CultureInfo.InvariantCulture);
            _length += written;
            _comma = true;
        }

        public void Number(ulong value)
        {
            Separate();
            Ensure(20);
            value.TryFormat(_chars.AsSpan(_length), out int written, default, CultureInfo.InvariantCulture);
            _length += written;
            _comma = true;
        }

        /// <summary>A double as the shortest text that reads back as it, or as a string when it is not finite.</summary>
        public void Number(double value)
        {
            if (!double.IsFinite(value))
            {
                String(NonFinite(value));
                return;
            }

            Separate();
            Ensure(32);
            value.TryFormat(_chars.AsSpan(_length), out int written, "R", CultureInfo.InvariantCulture);
            _length += written;
            _comma = true;
        }

        /// <summary>A float as the shortest text that reads back as it, so 0.1f is written 0.1, or as a string when it is not finite.</summary>
        public void Number(float value)
        {
            if (!float.IsFinite(value))
            {
                String(NonFinite(value));
                return;
            }

            Separate();
            Ensure(32);
            value.TryFormat(_chars.AsSpan(_length), out int written, "R", CultureInfo.InvariantCulture);
            _length += written;
            _comma = true;
        }

        public void Number(decimal value)
        {
            Separate();
            Ensure(40);
            value.TryFormat(_chars.AsSpan(_length), out int written, default, CultureInfo.InvariantCulture);
            _length += written;
            _comma = true;
        }

        /// <summary>A time as ISO 8601 with its offset, as a string.</summary>
        public void Time(DateTimeOffset value)
        {
            Separate();
            Ensure(40);
            Raw('"');
            value.TryFormat(_chars.AsSpan(_length), out int written, "O", CultureInfo.InvariantCulture);
            _length += written;
            Raw('"');
            _comma = true;
        }

        private static string NonFinite(double value) =>
            double.IsNaN(value) ? "NaN" : value > 0 ? "Infinity" : "-Infinity";

        private void Separate()
        {
            if (_comma)
                Raw(',');
        }

        /// <summary>Writes <paramref name="text"/> in quotes, escaping what JSON requires: the quote, the backslash and every control char.</summary>
        private void Quoted(ReadOnlySpan<char> text)
        {
            Ensure(text.Length + 2);
            _chars[_length++] = '"';
            int run = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= ' ' && c != '"' && c != '\\')
                    continue;

                Raw(text.Slice(run, i - run));
                switch (c)
                {
                    case '"': Raw("\\\""); break;
                    case '\\': Raw("\\\\"); break;
                    case '\n': Raw("\\n"); break;
                    case '\r': Raw("\\r"); break;
                    case '\t': Raw("\\t"); break;
                    case '\b': Raw("\\b"); break;
                    case '\f': Raw("\\f"); break;
                    default:
                        Raw("\\u00");
                        Raw(Hex(c >> 4));
                        Raw(Hex(c & 0xF));
                        break;
                }

                run = i + 1;
            }

            Raw(text.Slice(run));
            Raw('"');
        }

        private static char Hex(int digit) => (char)(digit < 10 ? '0' + digit : 'a' + digit - 10);

        private void Raw(char c)
        {
            Ensure(1);
            _chars[_length++] = c;
        }

        private void Raw(ReadOnlySpan<char> text)
        {
            Ensure(text.Length);
            text.CopyTo(_chars.AsSpan(_length));
            _length += text.Length;
        }

        private void Ensure(int more)
        {
            if (_length + more <= _chars.Length)
                return;

            var grown = new char[Math.Max(_length + more, _chars.Length * 2)];
            Array.Copy(_chars, grown, _length);
            _chars = grown;
        }
    }
}
