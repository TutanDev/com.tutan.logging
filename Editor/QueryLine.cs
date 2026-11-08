using System;
using Tutan.Functional;

namespace Tutan.Logging.Editor
{
    /// <summary>
    /// The query line: the text in it, the filter the text last read as, which the rows are of, and
    /// why the text does not read, when it does not. Text that does not read keeps the filter it had,
    /// so the rows stay as they were while the developer types.
    /// </summary>
    internal sealed record QueryLine
    {
        private QueryLine(string text, LogFilter filter, Optional<Error> refused)
        {
            Text = text;
            Filter = filter;
            Refused = refused;
        }

        /// <summary>An empty line, which keeps every record.</summary>
        public static QueryLine Empty { get; } = new QueryLine("", LogFilter.All, F.None);

        /// <summary>What is in the line, as typed or as written from a filter.</summary>
        public string Text { get; }

        /// <summary>The filter the line last read as.</summary>
        public LogFilter Filter { get; }

        /// <summary>Why <see cref="Text"/> does not read, with a <see cref="LogFilterError"/> as its code; none when it reads as <see cref="Filter"/>.</summary>
        public Optional<Error> Refused { get; }

        /// <summary><paramref name="text"/> as typed: its filter when it reads, or <paramref name="previous"/> and why it does not.</summary>
        public static QueryLine Of(string text, LogFilter previous)
        {
            if (text is null)
                throw new ArgumentNullException(nameof(text));
            if (previous is null)
                throw new ArgumentNullException(nameof(previous));

            return LogFilter.Parse(text).Match(
                refused => new QueryLine(text, previous, refused),
                filter => new QueryLine(text, filter, F.None));
        }

        /// <summary>The line a filter is written as, in the fewest words: what a badge, a category or a scope's press makes.</summary>
        public static QueryLine Written(LogFilter filter)
        {
            if (filter is null)
                throw new ArgumentNullException(nameof(filter));

            return new QueryLine(filter.ToString(), filter, F.None);
        }
    }
}
