using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Tutan.Logging
{
    /// <summary>
    /// A message template taken apart: the text between its holes, and the holes. There is always one
    /// more piece of text than there are holes, the first before the first hole and the last after the
    /// last, either of them possibly empty.
    /// </summary>
    /// <remarks>
    /// This file is compiled twice: into Tutan.Logging, which builds a template's pieces when its
    /// event registers, and into Tutan.Logging.Generators, which refuses a template that does not
    /// parse and matches its holes to parameters. So it is written for netstandard2.0 and C# 9 as
    /// well, and names a hole by <see cref="LogNames"/>, which is compiled into both too.
    /// </remarks>
    internal sealed class ParsedTemplate
    {
        public ParsedTemplate(string[] literals, TemplateHole[] holes)
        {
            Literals = literals;
            Holes = holes;
        }

        /// <summary>The text around the holes, one more than <see cref="Holes"/>, with <c>{{</c> and <c>}}</c> already made single.</summary>
        public string[] Literals { get; }

        /// <summary>The holes, in the order they appear.</summary>
        public TemplateHole[] Holes { get; }
    }

    /// <summary>One hole: <c>{Name}</c>, <c>{Name,8}</c>, <c>{Name:F2}</c> or <c>{Name,-8:F2}</c>.</summary>
    internal sealed class TemplateHole
    {
        public TemplateHole(string name, int alignment, string format)
        {
            Name = name;
            Alignment = alignment;
            Format = format;
        }

        /// <summary>The name as the template spells it, which is the field's name.</summary>
        public string Name { get; }

        /// <summary>The width to pad to: on the left when positive, on the right when negative, none when 0.</summary>
        public int Alignment { get; }

        /// <summary>What follows the colon, handed to the value's own formatting, or empty.</summary>
        public string Format { get; }
    }

    /// <summary>Parses a message template, or says why it does not parse.</summary>
    internal static class TemplateParser
    {
        /// <summary>The widest a hole pads to, either way: a column of a message, not a page of spaces.</summary>
        public const int MostAlignment = 1024;

        /// <summary>
        /// The pieces of <paramref name="template"/>, or null with <paramref name="error"/> saying what is
        /// wrong with it, as the end of a sentence: <c>the hole at 12 is not closed</c>.
        /// </summary>
        public static ParsedTemplate? Parse(string template, out string? error)
        {
            var literals = new List<string>();
            var holes = new List<TemplateHole>();
            var text = new StringBuilder();
            error = null;

            int at = 0;
            while (at < template.Length)
            {
                char c = template[at];
                if (c == '{')
                {
                    if (at + 1 < template.Length && template[at + 1] == '{')
                    {
                        text.Append('{');
                        at += 2;
                        continue;
                    }

                    int close = template.IndexOf('}', at + 1);
                    if (close < 0)
                    {
                        error = $"the hole at {at.ToString(CultureInfo.InvariantCulture)} is not closed";
                        return null;
                    }

                    TemplateHole? hole = Hole(template.Substring(at + 1, close - at - 1), at, out error);
                    if (hole is null)
                        return null;

                    literals.Add(text.ToString());
                    text.Clear();
                    holes.Add(hole);
                    at = close + 1;
                    continue;
                }

                if (c == '}')
                {
                    if (at + 1 < template.Length && template[at + 1] == '}')
                    {
                        text.Append('}');
                        at += 2;
                        continue;
                    }

                    error = $"the '}}' at {at.ToString(CultureInfo.InvariantCulture)} closes no hole, and a '}}' of the text is written '}}}}'";
                    return null;
                }

                text.Append(c);
                at++;
            }

            literals.Add(text.ToString());
            return new ParsedTemplate(literals.ToArray(), holes.ToArray());
        }

        /// <summary>What is between a hole's braces: a name, then an alignment after a comma, then a format after a colon.</summary>
        private static TemplateHole? Hole(string inside, int at, out string? error)
        {
            error = null;
            int colon = inside.IndexOf(':');
            string head = colon < 0 ? inside : inside.Substring(0, colon);
            string format = colon < 0 ? "" : inside.Substring(colon + 1);

            int comma = head.IndexOf(',');
            string name = comma < 0 ? head : head.Substring(0, comma);
            int alignment = 0;

            if (name.Length == 0)
            {
                error = $"the hole at {at.ToString(CultureInfo.InvariantCulture)} has no name";
                return null;
            }

            if (!LogNames.IsEventName(name))
            {
                error = char.IsDigit(name[0])
                    ? $"the hole at {at.ToString(CultureInfo.InvariantCulture)} is numbered, and a hole is named after its parameter, as {{Slot}}"
                    : $"the hole at {at.ToString(CultureInfo.InvariantCulture)} is named '{name}', and a name is an ASCII letter or '_' followed by letters, digits and '_'";
                return null;
            }

            if (comma >= 0 && !int.TryParse(head.Substring(comma + 1), NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out alignment))
            {
                error = $"the hole at {at.ToString(CultureInfo.InvariantCulture)} has the alignment '{head.Substring(comma + 1)}', which is not a whole number";
                return null;
            }

            if (alignment < -MostAlignment || alignment > MostAlignment)
            {
                error = $"the hole at {at.ToString(CultureInfo.InvariantCulture)} pads to {alignment.ToString(CultureInfo.InvariantCulture)}, and a hole pads to at most {MostAlignment.ToString(CultureInfo.InvariantCulture)} either way";
                return null;
            }

            if (format.IndexOf('{') >= 0)
            {
                error = $"the hole at {at.ToString(CultureInfo.InvariantCulture)} has a '{{' in its format";
                return null;
            }

            return new TemplateHole(name, alignment, format);
        }
    }
}
