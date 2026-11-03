using System;
using System.Buffers;
using System.IO;

namespace Tutan.Logging
{
    /// <summary>The small steps every formatter here is made of: copy, append, align, redact.</summary>
    internal static class LogText
    {
        /// <summary>What a sink that redacts personal fields writes in place of one.</summary>
        public const string PersonalText = "[personal]";

        /// <summary>The user's home folder as it is written in paths, without a separator at its end, or empty when there is none.</summary>
        private static readonly string s_home = Home();

        /// <summary>Whether paths are matched as Windows reads them: ignoring case, and either slash.</summary>
        private static readonly bool s_windows = Path.DirectorySeparatorChar == '\\';

        public static bool Copy(ReadOnlySpan<char> text, Span<char> destination, out int written)
        {
            if (text.TryCopyTo(destination))
            {
                written = text.Length;
                return true;
            }

            written = 0;
            return false;
        }

        /// <summary>
        /// The name of the type <paramref name="exception"/> was thrown as: its full name, as a file
        /// writes it, or its name, as a message does. An exception read back from a file is named as
        /// the program that threw it named it, not as an <see cref="ArchivedException"/>.
        /// </summary>
        public static string TypeName(Exception exception, bool full)
        {
            if (exception is ArchivedException archived)
                return full ? archived.TypeName : ShortName(archived.TypeName);

            Type type = exception.GetType();
            return full ? type.FullName ?? type.Name : type.Name;
        }

        /// <summary>
        /// A full name's last part, as <see cref="Type.Name"/> has it: what follows the last <c>.</c>
        /// or <c>+</c> before a generic type's arguments.
        /// </summary>
        private static string ShortName(string fullName)
        {
            int arguments = fullName.IndexOf('[');
            int end = arguments < 0 ? fullName.Length : arguments;
            int start = end;
            while (start > 0 && fullName[start - 1] is not ('.' or '+'))
                start--;

            return fullName.Substring(start, end - start);
        }

        /// <summary>Appends <paramref name="text"/> at <paramref name="written"/>, which moves past it, or leaves both alone when it does not fit.</summary>
        public static bool Append(ReadOnlySpan<char> text, Span<char> destination, ref int written)
        {
            if (!text.TryCopyTo(destination.Slice(written)))
                return false;

            written += text.Length;
            return true;
        }

        /// <summary>Formats a field as a hole asks, padded to its alignment, with what <paramref name="redact"/> leaves out left out.</summary>
        public static bool TryFormatAligned(LogValue value, Span<char> destination, out int written, LogHole hole, LogRedaction redact)
        {
            if (!TryFormatRedacted(value, destination, out written, hole.Format, redact))
                return false;

            int width = Math.Abs(hole.Alignment);
            if (written >= width)
                return true;
            if (destination.Length < width)
            {
                written = 0;
                return false;
            }

            int pad = width - written;
            if (hole.Alignment > 0)
            {
                destination.Slice(0, written).CopyTo(destination.Slice(pad));
                destination.Slice(0, pad).Fill(' ');
            }
            else
            {
                destination.Slice(written, pad).Fill(' ');
            }

            written = width;
            return true;
        }

        /// <summary>Formats a field, writing <c>[personal]</c> for a personal one and <c>~</c> for the home folder, as <paramref name="redact"/> asks.</summary>
        public static bool TryFormatRedacted(LogValue value, Span<char> destination, out int written, string? format, LogRedaction redact)
        {
            if (value.Field.Personal && (redact & LogRedaction.Personal) != 0)
                return Copy(PersonalText, destination, out written);
            if (!value.TryFormat(destination, out written, format))
                return false;

            if ((redact & LogRedaction.HomeFolder) != 0 && value.Kind is LogValueKind.String or LogValueKind.Chars
                    or LogValueKind.Exception or LogValueKind.Formattable)
                written = WithoutHome(destination.Slice(0, written));

            return true;
        }

        /// <summary>Writes <c>~</c> in place of the home folder wherever <paramref name="text"/> has it, in place, and says the length that is left.</summary>
        public static int WithoutHome(Span<char> text)
        {
            if (s_home.Length == 0)
                return text.Length;

            int read = 0;
            int write = 0;
            while (read < text.Length)
            {
                if (IsHomeAt(text.Slice(read)))
                {
                    text[write++] = '~';
                    read += s_home.Length;
                    continue;
                }

                text[write++] = text[read++];
            }

            return write;
        }

        /// <summary>The message of <paramref name="record"/> as a string, for what has to have one.</summary>
        public static string ToText(LogRecord record, LogRedaction redact)
        {
            char[] buffer = ArrayPool<char>.Shared.Rent(256);
            try
            {
                while (true)
                {
                    if (record.TryFormat(buffer, out int written, redact))
                        return new string(buffer, 0, written);

                    int length = buffer.Length * 2;
                    ArrayPool<char>.Shared.Return(buffer);
                    buffer = ArrayPool<char>.Shared.Rent(length);
                }
            }
            finally
            {
                ArrayPool<char>.Shared.Return(buffer);
            }
        }

        private static string Home()
        {
            try
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('/', '\\');
            }
            catch (PlatformNotSupportedException)
            {
                return "";
            }
        }

        /// <summary>
        /// Whether the home folder starts <paramref name="text"/> and ends at a separator or the end of
        /// it, so a home of <c>/home/ann</c> is not found in <c>/home/anna</c>. On Windows a path is
        /// matched ignoring case and either slash, as Windows reads one.
        /// </summary>
        private static bool IsHomeAt(ReadOnlySpan<char> text)
        {
            if (text.Length < s_home.Length)
                return false;

            for (int i = 0; i < s_home.Length; i++)
            {
                char a = text[i];
                char b = s_home[i];
                if (a == b)
                    continue;
                if (!s_windows)
                    return false;
                if (a is '/' or '\\' && b is '/' or '\\')
                    continue;
                if (char.ToUpperInvariant(a) != char.ToUpperInvariant(b))
                    return false;
            }

            if (text.Length == s_home.Length)
                return true;

            char next = text[s_home.Length];
            return !(char.IsLetterOrDigit(next) || next is '_' or '-' or '.');
        }
    }
}
