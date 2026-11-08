using System;
using System.Globalization;
using Tutan.Functional;

namespace Tutan.Logging.Editor
{
    /// <summary>
    /// A row as the list draws it, each column's text read once: when, the frame, the level, the
    /// category, the message, and what the row adds of its own - how many it repeats, how many a
    /// limit dropped, how long a scope took, whether it is folded, and the records lost before it.
    /// </summary>
    internal sealed record RowCells
    {
        private static readonly char[] s_lineEnds = { '\r', '\n' };

        private RowCells(LogRow row, string time, string frame, string category, string message, bool context, bool held, Optional<RowScope> scope)
        {
            Row = row;
            Time = time;
            Frame = frame;
            Category = category;
            Message = message;
            Context = context;
            Held = held;
            bool ofScope = scope.HasValue(out RowScope shows);
            ScopeTimeRead = ofScope ? F.Some(shows.Time) : F.None;
            Duration = ofScope ? DurationOf(shows.Time) : "";
            Folded = ofScope && shows.Folded;
            Repeats = row.Records > 1 ? "×" + row.Records.ToString("N0", CultureInfo.InvariantCulture) : "";
            Suppressed = row.Suppressed > 0 ? "+" + row.Suppressed.ToString("N0", CultureInfo.InvariantCulture) : "";
            SuppressedTip = row.Suppressed > 0 ? Limited(row.Suppressed) : "";
            LostBefore = row.LostBefore > 0 ? Lost(row.LostBefore) : "";
            LostMark = row.LostBefore > 0 ? "⚠" : "";
            Fold = !ofScope ? "" : Folded ? "▸" : "▾";
            FoldTip = !ofScope ? "" : Folded ? "Shows what is under this scope." : "Hides what is under this scope.";
        }

        /// <summary>The row the cells are of.</summary>
        public LogRow Row { get; }

        /// <summary>When its record was written, on the wall clock, as <c>14:03:12.481</c>.</summary>
        public string Time { get; }

        /// <summary>The frame it was written in, or empty when the store had none.</summary>
        public string Frame { get; }

        /// <summary>Its event's category, as <c>game.save</c>.</summary>
        public string Category { get; }

        /// <summary>Its message, or what is known of it when its records are gone.</summary>
        public string Message { get; }

        /// <summary>Whether it was written as an error's context.</summary>
        public bool Context { get; }

        /// <summary>Whether the session still holds its record: a row whose records were overwritten since shows what the row knows.</summary>
        public bool Held { get; }

        /// <summary>How long a scope's row took when the cells were read; none for a row that is not a scope's.</summary>
        public Optional<ScopeTime> ScopeTimeRead { get; }

        /// <summary>How long a scope's row took, as <c>12.4 ms</c>, <c>open</c> while it has not closed, or empty.</summary>
        public string Duration { get; }

        /// <summary>Whether a scope's row hides what is under it; never for a row that is not a scope's.</summary>
        public bool Folded { get; }

        /// <summary>How many records the row repeats, as <c>×12</c>, or empty for one.</summary>
        public string Repeats { get; }

        /// <summary>How many records of its event a limit dropped, as <c>+3</c>, or empty for none.</summary>
        public string Suppressed { get; }

        /// <summary>What the cell of <see cref="Suppressed"/> says when pointed at, or empty for none.</summary>
        public string SuppressedTip { get; }

        /// <summary>The records lost before the row, as the mark's tip says it, or empty for none.</summary>
        public string LostBefore { get; }

        /// <summary>The mark drawn on a row records were lost before, ⚠, or empty for none.</summary>
        public string LostMark { get; }

        /// <summary>The fold of a scope's row, ▸ folded and ▾ not, or empty for a row that is not a scope's.</summary>
        public string Fold { get; }

        /// <summary>What the fold says when pointed at, or empty for a row that has none.</summary>
        public string FoldTip { get; }

        /// <summary>The cells of <paramref name="row"/>, whose record is <paramref name="record"/>.</summary>
        /// <param name="record">The row's record, as <see cref="LogView.Read"/> read it.</param>
        /// <param name="row">The row.</param>
        /// <param name="clock">The clock of the session's records.</param>
        /// <param name="scope">What a scope's row shows of its scope: none for a row that is not a scope's.</param>
        /// <exception cref="ArgumentException">
        /// <paramref name="record"/> is not the record of <paramref name="row"/>, or <paramref name="scope"/>
        /// is some for a row that is not a scope's, or none for one that is.
        /// </exception>
        public static RowCells Of(LogRecord record, LogRow row, LogClock clock, Optional<RowScope> scope)
        {
            CheckRecord(record, row);
            CheckScope(row, scope);

            string frame = record.Frame != 0 ? record.Frame.ToString(CultureInfo.InvariantCulture) : "";
            return new RowCells(row, Clock(clock.At(record.Timestamp)), frame, record.Event.Category, FirstLine(record.ToString()), record.IsBacklog,
                held: true, scope);
        }

        /// <summary>The cells of <paramref name="row"/>, whose records the session has overwritten since it was made.</summary>
        /// <param name="row">The row.</param>
        /// <param name="scope">What a scope's row shows of its scope: none for a row that is not a scope's.</param>
        /// <exception cref="ArgumentException"><paramref name="scope"/> is some for a row that is not a scope's, or none for one that is.</exception>
        public static RowCells Gone(LogRow row, Optional<RowScope> scope)
        {
            CheckScope(row, scope);

            return new RowCells(row, "", "", "", "Overwritten since: the session no longer holds this record.", context: false, held: false, scope);
        }

        /// <summary>
        /// Refuses <paramref name="record"/> unless it is the record of <paramref name="row"/>, as
        /// <see cref="LogView.Read"/> reads one: its first, or its last once the first is gone.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="record"/> is not the row's.</exception>
        public static void CheckRecord(LogRecord record, LogRow row)
        {
            if (record.Sequence != row.Sequence && record.Sequence != row.Last)
                throw new ArgumentException(
                    $"Record {record.Sequence.ToString(CultureInfo.InvariantCulture)} is not the record of the row of records "
                    + $"{row.Sequence.ToString(CultureInfo.InvariantCulture)} to {row.Last.ToString(CultureInfo.InvariantCulture)}.", nameof(record));
        }

        /// <summary>Refuses <paramref name="scope"/> unless it is some exactly for a scope's row: an event's row has no time and no fold.</summary>
        /// <exception cref="ArgumentException"><paramref name="scope"/> is some for a row that is not a scope's, or none for one that is.</exception>
        private static void CheckScope(LogRow row, Optional<RowScope> scope)
        {
            bool ofScope = row.Kind == LogRowKind.Scope;
            if (scope.IsSome != ofScope)
                throw new ArgumentException(ofScope
                    ? $"The row of record {row.Sequence.ToString(CultureInfo.InvariantCulture)} is a scope's, and shows how long it took and whether it is folded: its scope was not given."
                    : $"The row of record {row.Sequence.ToString(CultureInfo.InvariantCulture)} is an event's, which has no time and no fold: a scope was given for it.", nameof(scope));
        }

        /// <summary>A count of records lost, as a person reads it.</summary>
        public static string Lost(long count) =>
            $"{count.ToString("N0", CultureInfo.InvariantCulture)} {(count == 1 ? "record was" : "records were")} lost here";

        /// <summary>How many records of a row's event a limit dropped before it, as the row's tip and its details say.</summary>
        public static string Limited(long count) =>
            $"A limit dropped {count.ToString("N0", CultureInfo.InvariantCulture)} {(count == 1 ? "record" : "records")} of this event before it.";

        /// <summary>A time of day as the rows show it, to the millisecond.</summary>
        public static string Clock(DateTimeOffset at) => at.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

        /// <summary>How long a scope took, as the rows and the details show it.</summary>
        public static string DurationOf(ScopeTime time) => time switch
        {
            ScopeTime.Took took => Elapsed(took.Elapsed),
            ScopeTime.StillOpen => "open",
            ScopeTime.NotKnown => "",
            _ => throw new ArgumentOutOfRangeException(nameof(time), $"{time.GetType().Name} is not a scope's time the rows show."),
        };

        /// <summary>A time a scope took: in milliseconds to a tenth under a second, and in seconds to a hundredth after.</summary>
        public static string Elapsed(TimeSpan elapsed) => elapsed.TotalSeconds < 1
            ? elapsed.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture) + " ms"
            : elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s";

        /// <summary>The first line of a message, which a row shows; the details show it whole.</summary>
        private static string FirstLine(string message)
        {
            int end = message.IndexOfAny(s_lineEnds);
            return end < 0 ? message : message.Substring(0, end) + " …";
        }
    }

    /// <summary>What a scope's row shows of its scope: how long it took, as it was read, and whether it is folded.</summary>
    internal sealed record RowScope
    {
        /// <summary>A scope's <paramref name="time"/>, and whether it is <paramref name="folded"/>.</summary>
        public RowScope(ScopeTime time, bool folded)
        {
            Time = time ?? throw new ArgumentNullException(nameof(time));
            Folded = folded;
        }

        /// <summary>How long the scope took, as it was read.</summary>
        public ScopeTime Time { get; }

        /// <summary>Whether the scope's row hides what is under it.</summary>
        public bool Folded { get; }
    }

    /// <summary>
    /// The cells read of a row, with the first record the session held when they were read, and
    /// whether the time of the row's scope is final: closed, or not a scope's row.
    /// </summary>
    internal sealed record HeldCells
    {
        private HeldCells(RowCells cells, long first, bool final)
        {
            Cells = cells;
            First = first;
            Final = final;
        }

        /// <summary>The cells read.</summary>
        public RowCells Cells { get; }

        /// <summary>The first record the session held when they were read.</summary>
        public long First { get; }

        /// <summary>Whether the time of the row's scope can change no more: it closed, or the row is not a scope's.</summary>
        public bool Final { get; }

        /// <summary>
        /// <paramref name="cells"/>, read while the session's first record was <paramref name="first"/>:
        /// final when the time they were read with is the one their scope took, and always for a row that
        /// is not a scope's.
        /// </summary>
        public static HeldCells Of(RowCells cells, long first)
        {
            if (cells is null)
                throw new ArgumentNullException(nameof(cells));

            bool final = !cells.ScopeTimeRead.HasValue(out ScopeTime time) || time switch
            {
                ScopeTime.Took => true,
                ScopeTime.StillOpen or ScopeTime.NotKnown => false,
                _ => throw new ArgumentOutOfRangeException(nameof(cells), $"{time.GetType().Name} is not a scope's time the rows show."),
            };
            return new HeldCells(cells, first, final);
        }

        /// <summary>
        /// Whether the cells still show what <paramref name="row"/> would, so it need not be read again:
        /// the row is as it was, with no repeat added, it is folded as it was, its record is the one
        /// read, and its scope's time is final or still what it was read as, as while it stays open.
        /// Its record is the one read while the session still holds the row's first record, as when
        /// nothing was overwritten since, or when the cells held no record to begin with.
        /// </summary>
        /// <param name="row">The row as the view has it now.</param>
        /// <param name="folded">Whether its scope is folded now; of no row but a scope's, as the cells keep no fold for one.</param>
        /// <param name="first">The first record the session holds now.</param>
        /// <param name="scopes">The scopes the view has read, asked how long the row's scope took only when what was read of it is not final.</param>
        public bool Still(LogRow row, bool folded, long first, LogScopes scopes)
        {
            if (scopes is null)
                throw new ArgumentNullException(nameof(scopes));

            return Cells.Row == row
                && Cells.Folded == (row.Kind == LogRowKind.Scope && folded)
                && (!Cells.Held || First == first || row.Sequence >= first)
                && (Final || (Cells.ScopeTimeRead.HasValue(out ScopeTime read) && read.Equals(ScopeTime.Of(scopes, row.Sequence))));
        }
    }
}
