using System;
using System.Globalization;
using Tutan.Functional;
using UnityEngine;

namespace Tutan.Logging.Editor
{
    /// <summary>Why a change of the console's settings, or an entry of a settings file, was refused: the code of its <see cref="Error"/>.</summary>
    internal enum SettingsError
    {
        /// <summary>A saved filter's name is empty, or only white space.</summary>
        NameEmpty = 1,

        /// <summary>A saved filter's name is longer than <see cref="FilterName.MostLength"/>.</summary>
        NameTooLong,

        /// <summary>A saved filter's name holds a tab, a line break or another control character.</summary>
        NameUnreadable,

        /// <summary>Another saved filter has the name, ignoring case.</summary>
        NameTaken,

        /// <summary>No saved filter has the name to change.</summary>
        NoSuchFilter,

        /// <summary>A query does not read: the filter's error is the inner error.</summary>
        QueryUnreadable,

        /// <summary>What is always left out names a level, a frame or a word kept, where only words after - can be.</summary>
        LeftOutKeeps,

        /// <summary>A colour is not written as #RRGGBB.</summary>
        ColourUnreadable,

        /// <summary>A category is not lowercase words of letters and digits joined by dots.</summary>
        CategoryUnreadable,

        /// <summary>The settings file could not be read, so it is not written over.</summary>
        Unwritable,

        /// <summary>The settings file could not be written.</summary>
        NotWritten,
    }

    /// <summary>Makes the errors of the console's settings, with a <see cref="SettingsError"/> as the code.</summary>
    internal static class SettingsErrors
    {
        /// <summary>A refusal for <paramref name="why"/>, saying <paramref name="message"/>.</summary>
        public static Error Of(SettingsError why, string message) => F.Error(message, (int)why);

        /// <summary>A refusal for <paramref name="why"/>, saying <paramref name="message"/>, because of <paramref name="inner"/>.</summary>
        public static Error Of(SettingsError why, string message, Error inner) => F.Error(message, (int)why, inner);
    }

    /// <summary>
    /// The name a saved filter goes by: its text trimmed, of 1 to <see cref="MostLength"/> characters,
    /// none a control character. Two names that differ only in case are one name to a team, as
    /// <see cref="SameAs"/> says, so no two saved filters have them.
    /// </summary>
    internal sealed record FilterName
    {
        /// <summary>The most characters a name has, which keeps its button short.</summary>
        public const int MostLength = 32;

        private FilterName(string value) => Value = value;

        /// <summary>The name, as spelt.</summary>
        public string Value { get; }

        /// <summary>
        /// <paramref name="text"/> trimmed as a name, or why it is not one: empty, longer than
        /// <see cref="MostLength"/>, or with a control character in it.
        /// </summary>
        public static Result<FilterName> Of(string text)
        {
            if (text is null)
                throw new ArgumentNullException(nameof(text));

            string name = text.Trim();
            if (name.Length == 0)
                return SettingsErrors.Of(SettingsError.NameEmpty, "A saved filter needs a name.");
            if (name.Length > MostLength)
                return SettingsErrors.Of(SettingsError.NameTooLong,
                    $"A saved filter's name is at most {MostLength.ToString(CultureInfo.InvariantCulture)} characters, and \"{name}\" is {name.Length.ToString(CultureInfo.InvariantCulture)}.");

            foreach (char c in name)
            {
                if (char.IsControl(c))
                    return SettingsErrors.Of(SettingsError.NameUnreadable, "A saved filter's name is one line of text, without tabs or other control characters.");
            }

            return new FilterName(name);
        }

        /// <summary>Whether it is <paramref name="other"/> to a team: the same name, ignoring case.</summary>
        public bool SameAs(FilterName other)
        {
            if (other is null)
                throw new ArgumentNullException(nameof(other));

            return string.Equals(Value, other.Value, StringComparison.OrdinalIgnoreCase);
        }

        /// <inheritdoc />
        public override string ToString() => Value;
    }

    /// <summary>A colour a developer chose, opaque: one colour on both skins, written <c>#RRGGBB</c>.</summary>
    /// <param name="Red">Its red, from 0 to 255.</param>
    /// <param name="Green">Its green, from 0 to 255.</param>
    /// <param name="Blue">Its blue, from 0 to 255.</param>
    internal readonly record struct ChosenColour(byte Red, byte Green, byte Blue)
    {
        /// <summary>It written as a file holds it: <c>#3FA9F5</c>.</summary>
        public string Hex => "#" + Red.ToString("X2", CultureInfo.InvariantCulture) + Green.ToString("X2", CultureInfo.InvariantCulture)
                             + Blue.ToString("X2", CultureInfo.InvariantCulture);

        /// <summary>It as Unity draws it.</summary>
        public Color Colour => new Color32(Red, Green, Blue, 255);

        /// <summary><paramref name="text"/> as a colour: six hex digits, after a <c>#</c> or not, in either case, as <c>#3FA9F5</c> or <c>3fa9f5</c>.</summary>
        public static Result<ChosenColour> Parse(string text)
        {
            if (text is null)
                throw new ArgumentNullException(nameof(text));

            string hex = text.Trim();
            if (hex.StartsWith("#", StringComparison.Ordinal))
                hex = hex.Substring(1);

            if (hex.Length == 6 && Byte(hex, 0, out byte red) && Byte(hex, 2, out byte green) && Byte(hex, 4, out byte blue))
                return new ChosenColour(red, green, blue);

            return SettingsErrors.Of(SettingsError.ColourUnreadable, $"\"{text}\" is not a colour: a colour is written as #RRGGBB, as #3FA9F5.");
        }

        /// <summary><paramref name="colour"/>'s red, green and blue, each rounded to a byte; its alpha is dropped, as a colour chosen is opaque.</summary>
        public static ChosenColour Of(Color colour) => new ChosenColour(ToByte(colour.r), ToByte(colour.g), ToByte(colour.b));

        /// <inheritdoc />
        public override string ToString() => Hex;

        private static bool Byte(string hex, int at, out byte value) =>
            byte.TryParse(hex.Substring(at, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);

        private static byte ToByte(float channel) => (byte)Mathf.RoundToInt(Mathf.Clamp01(channel) * 255f);
    }

    /// <summary>
    /// A category as declared: lowercase words of letters and digits, each starting with a letter,
    /// joined by dots, as <c>game.save</c>.
    /// </summary>
    internal sealed record CategoryName
    {
        private CategoryName(string value) => Value = value;

        /// <summary>The category.</summary>
        public string Value { get; }

        /// <summary><paramref name="text"/> trimmed as a category, or why it is not one.</summary>
        public static Result<CategoryName> Of(string text)
        {
            if (text is null)
                throw new ArgumentNullException(nameof(text));

            string category = text.Trim();
            return IsCategory(category)
                ? new CategoryName(category)
                : SettingsErrors.Of(SettingsError.CategoryUnreadable,
                    $"\"{category}\" is not a category: a category is lowercase words of letters and digits, each starting with a letter, joined by dots, as game.save.");
        }

        /// <summary>Whether <paramref name="category"/> is it, or a category under it, as <c>game.save.slots</c> is under <c>game</c>.</summary>
        public bool Holds(string category)
        {
            if (category is null)
                throw new ArgumentNullException(nameof(category));

            return category.Length == Value.Length
                ? string.Equals(category, Value, StringComparison.Ordinal)
                : category.Length > Value.Length && category[Value.Length] == '.' && string.CompareOrdinal(category, 0, Value, 0, Value.Length) == 0;
        }

        /// <inheritdoc />
        public override string ToString() => Value;

        /// <summary>
        /// Whether <paramref name="text"/> is a category: a copy of the runtime's <c>LogNames.IsCategory</c>,
        /// which this assembly does not see; a test holds the two alike.
        /// </summary>
        internal static bool IsCategory(string text)
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
    }
}
