using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Tutan.Functional;

namespace Tutan.Logging.Editor
{
    /// <summary>What a line said under the query line is about.</summary>
    internal enum SaidKind
    {
        /// <summary>Opening a file, exporting, or changing the settings went wrong.</summary>
        Trouble,

        /// <summary>An export was written, but the file it wrote over could not be deleted, and is still beside it.</summary>
        NotDeleted,

        /// <summary>The query line does not read.</summary>
        Refused,

        /// <summary>Words of the query name what no event of the session is or has.</summary>
        MatchingNothing,

        /// <summary>A regular expression took too long and was given up on, which a retry tries again.</summary>
        GaveUp,

        /// <summary>Lines of the session's file could not be read.</summary>
        Skipped,

        /// <summary>Records were lost before the console read them.</summary>
        Lost,

        /// <summary>The error the toolbar's badge showed is not among the rows.</summary>
        Hidden,

        /// <summary>The error the toolbar's badge showed was let go before it could be shown.</summary>
        Gone,

        /// <summary>What the project always leaves out, which an Edit button opens the settings of.</summary>
        LeftOut,
    }

    /// <summary>A line said under the query line: what it is about, and its text.</summary>
    internal sealed record SaidLine(SaidKind Kind, string Text);

    /// <summary>What the last try at opening a file, exporting, changing the settings, or showing an error told, said under the query line until the next try.</summary>
    internal abstract record Told
    {
        private Told()
        {
        }

        /// <summary>The line it is said as.</summary>
        internal abstract SaidLine Line { get; }

        /// <summary>The try went wrong, as <see cref="Message"/> says.</summary>
        internal sealed record Trouble(string Message) : Told
        {
            internal override SaidLine Line => new(SaidKind.Trouble, SaidLines.Closed(Message));
        }

        /// <summary>
        /// An export to <see cref="Export"/> was written, but could not delete the file it wrote over, as
        /// <see cref="Left"/> says where and why: a warning, not trouble, so the developer can delete that
        /// file.
        /// </summary>
        internal sealed record NotDeleted(string Export, LogNotDeleted Left) : Told
        {
            internal override SaidLine Line => new(SaidKind.NotDeleted,
                $"The rows were exported to {Export}. The file that was there is now {Left.Path}, and could not be deleted: {Left.Reason} It may hold what this export left out.");
        }

        /// <summary>The error the toolbar's badge showed is not among the rows, once they were read: a warning, as something the developer asked for hides it.</summary>
        internal sealed record Hidden : Told
        {
            private Hidden()
            {
            }

            /// <summary>The one line of an error hidden.</summary>
            public static Hidden Instance { get; } = new Hidden();

            internal override SaidLine Line => new(SaidKind.Hidden,
                "The error the toolbar showed is not among the rows: the query, a fold, or what the project always leaves out hides it.");
        }

        /// <summary>The error the toolbar's badge showed was let go before its row was found, past its session's cap or by a clear, or with its session: a warning, as nothing the developer did hides it.</summary>
        internal sealed record Gone : Told
        {
            private Gone()
            {
            }

            /// <summary>The one line of an error gone.</summary>
            public static Gone Instance { get; } = new Gone();

            internal override SaidLine Line => new(SaidKind.Gone,
                "The error the toolbar showed was let go before it could be shown: past its session's cap, by a clear, or with its session.");
        }
    }

    /// <summary>
    /// What of a view the lines said under the query line are of: the filter its rows were made with,
    /// the scan the window started last, how many events the session declared, how many terms gave
    /// up, and how many records were lost. The terms that gave up only grow until a scan starts.
    /// </summary>
    /// <param name="Shown">The filter the rows were made with.</param>
    /// <param name="Scans">How many scans the window started on the view.</param>
    /// <param name="Events">How many events the session declared.</param>
    /// <param name="GaveUp">How many terms of the filter gave up on a record.</param>
    /// <param name="Lost">How many records were lost before the view read them.</param>
    internal readonly record struct RowsSaid(LogFilter Shown, int Scans, int Events, int GaveUp, long Lost);

    /// <summary>
    /// What the lines said under the query line are drawn of: what the last try told, the query line,
    /// the session shown, what of its rows they say, and what is always left out. They are made again
    /// only when it changed.
    /// </summary>
    /// <param name="Told">What the last try at opening a file, exporting, or showing an error told.</param>
    /// <param name="Query">The query line.</param>
    /// <param name="Session">The session shown, while the process has it.</param>
    /// <param name="Rows">What of the rows they say, while a session is shown.</param>
    /// <param name="LeftOut">What the project always leaves out.</param>
    internal readonly record struct SaidMark(Optional<Told> Told, QueryLine Query, Optional<LogConsoleSession> Session, Optional<RowsSaid> Rows, LeftOut LeftOut);

    /// <summary>What the console says under its query line.</summary>
    internal static class SaidLines
    {
        /// <summary>
        /// What is said under the query line, in order: what the last try said, why the query does not read,
        /// what the rows of <paramref name="view"/> lack, as the query and the session say, and what the
        /// project always leaves out of them.
        /// </summary>
        /// <param name="told">What the last try at opening a file, exporting, or showing an error told, until the next try.</param>
        /// <param name="query">The query line.</param>
        /// <param name="view">The rows, while a session is shown.</param>
        /// <param name="session">The session shown, while the process has it.</param>
        /// <param name="leftOut">What the project always leaves out, which the line does not show.</param>
        public static ReadOnlyCollection<SaidLine> Of(Optional<Told> told, QueryLine query, Optional<LogView> view, Optional<LogConsoleSession> session, LeftOut leftOut)
        {
            if (query is null)
                throw new ArgumentNullException(nameof(query));
            if (leftOut is null)
                throw new ArgumentNullException(nameof(leftOut));

            return Array.AsReadOnly(Said(told, query, view, session, leftOut).ToArray());
        }

        /// <summary>
        /// <paramref name="clause"/> closed, as every line here is: without the white space at its end, and with
        /// a full stop unless it then ends in one or in a question or exclamation mark, so it is closed once
        /// however often it is closed; a clause that is empty or only white space is closed as nothing. A copy
        /// of the runtime's <c>LogText.Closed</c>, which this assembly does not see; a test holds the two alike.
        /// </summary>
        internal static string Closed(string clause)
        {
            string trimmed = clause.TrimEnd();
            return trimmed.Length == 0 || trimmed[trimmed.Length - 1] is '.' or '?' or '!' ? trimmed : trimmed + ".";
        }

        private static IEnumerable<SaidLine> Said(Optional<Told> told, QueryLine query, Optional<LogView> view, Optional<LogConsoleSession> session, LeftOut leftOut)
        {
            if (told.HasValue(out Told last))
                yield return last.Line;
            if (query.Refused.HasValue(out Error refused))
                yield return new SaidLine(SaidKind.Refused, $"{Closed(refused.Message)} The rows are of the last query that read.");
            if (!view.HasValue(out LogView rows))
                yield break;

            IReadOnlyList<LogFilterTerm> nothing = query.Filter.MatchingNothing(rows.Source.Events);
            if (nothing.Count > 0)
                yield return new SaidLine(SaidKind.MatchingNothing, $"No event of this session is or has {Words(nothing)}. Text meant to be found is in quotes: \"text\".");

            if (rows.GaveUp.Count > 0)
                yield return new SaidLine(SaidKind.GaveUp, $"A regular expression took too long, and matches nothing from the record it gave up on: {Words(rows.GaveUp)}.");

            Optional<LogConsoleSession.File> read = session.Bind(static shown =>
                shown.Match<Optional<LogConsoleSession.File>>(static _ => F.None, static _ => F.None, static fromFile => fromFile));
            if (read.HasValue(out LogConsoleSession.File file) && file.Skipped.Count > 0)
            {
                string first = file.Skipped[0].ToString();
                yield return new SaidLine(SaidKind.Skipped, file.Skipped.Count == 1
                    ? $"A line of the file could not be read: {first}."
                    : $"{file.Skipped.Count.ToString("N0", CultureInfo.InvariantCulture)} lines of the file could not be read. The first: {first}.");
            }

            if (rows.Lost > 0)
                yield return new SaidLine(SaidKind.Lost, $"{RowCells.Lost(rows.Lost)}, before the console read them.");

            if (leftOut.Terms.Count > 0)
                yield return new SaidLine(SaidKind.LeftOut, $"The project always leaves out {leftOut.Query}.");
        }

        private static string Words(IReadOnlyList<LogFilterTerm> terms) => string.Join(", ", terms.Select(static term => term.ToString()));
    }
}
