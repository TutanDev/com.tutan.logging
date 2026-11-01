namespace Tutan.Logging
{
    /// <summary>
    /// What a category and an event's name may be spelt as. Compiled into Tutan.Logging and into its
    /// generator, as <see cref="TemplateParser"/> is, so the two refuse the same names.
    /// </summary>
    /// <remarks>This file is compiled for netstandard2.0 too, by the generator, and so is written for C# 9.</remarks>
    internal static class LogNames
    {
        /// <summary>The most fields an event has, which keeps the biggest record well inside the smallest rings a store allows.</summary>
        public const int MaxFields = 32;

        /// <summary>
        /// Whether <paramref name="text"/> is a category: lowercase words of letters and digits, each
        /// starting with a letter, joined by dots, as <c>game.save</c>.
        /// </summary>
        public static bool IsCategory(string text)
        {
            if (text.Length == 0)
                return false;

            bool wordStart = true;
            foreach (char c in text)
            {
                if (c == '.')
                {
                    if (wordStart)
                        return false;
                    wordStart = true;
                    continue;
                }

                bool ok = wordStart ? c is >= 'a' and <= 'z' : c is >= 'a' and <= 'z' or >= '0' and <= '9';
                if (!ok)
                    return false;
                wordStart = false;
            }

            return !wordStart;
        }

        /// <summary>Whether <paramref name="text"/> is an event's name: a letter or '_', then letters, digits and '_', as a C# identifier is.</summary>
        public static bool IsEventName(string text)
        {
            if (text.Length == 0 || !(IsAsciiLetter(text[0]) || text[0] == '_'))
                return false;

            for (int i = 1; i < text.Length; i++)
            {
                char c = text[i];
                if (!(IsAsciiLetter(c) || c is >= '0' and <= '9' || c == '_'))
                    return false;
            }

            return true;
        }

        private static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    }
}
