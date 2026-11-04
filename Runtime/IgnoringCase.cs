using System;

namespace Tutan.Logging
{
    /// <summary>
    /// Text compared ignoring case, one UTF-16 unit at a time, as
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> compares it on Mono and IL2CPP, allocating
    /// nothing. On Unity's Mono, <c>MemoryExtensions.IndexOf</c> with
    /// <see cref="StringComparison.OrdinalIgnoreCase"/> allocates for every search, which a view
    /// updating each frame cannot afford.
    /// </summary>
    internal static class IgnoringCase
    {
        /// <summary>Whether <paramref name="text"/> holds <paramref name="value"/> anywhere.</summary>
        public static bool Holds(ReadOnlySpan<char> text, ReadOnlySpan<char> value)
        {
            for (int start = 0; start <= text.Length - value.Length; start++)
            {
                int at = 0;
                while (at < value.Length && Same(text[start + at], value[at]))
                    at++;

                if (at == value.Length)
                    return true;
            }

            return false;
        }

        /// <summary>Whether two chars are the same one, ignoring case.</summary>
        public static bool Same(char a, char b) => a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
    }
}
