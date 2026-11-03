using System;
using System.Collections.Generic;

namespace Tutan.Logging
{
    /// <summary>
    /// Records a reader reads by sequence: a live <see cref="LogStore"/>'s, or a session's, held as a
    /// <see cref="LogArchive"/>, and nothing else. A <see cref="LogView"/> reads either, so a console
    /// asks a session read back from its file what it asks the live one.
    /// </summary>
    /// <remarks>
    /// It is the reading half of a store, and only that: what reads an archive cannot write to it or
    /// change what it captures. The two are all there are, so a view and its scopes are written
    /// against what those two do, and not against what some third kind of records might.
    /// </remarks>
    public abstract class LogRecords
    {
        private protected LogRecords()
        {
        }

        /// <summary>The program that wrote the records.</summary>
        public abstract string Program { get; }

        /// <summary>When the records' store started, which turns a record's timestamp into a time.</summary>
        public abstract LogClock Clock { get; }

        /// <summary>Every event a record here can be of, which is what a filter's patterns are checked against.</summary>
        public abstract IReadOnlyList<LogEventSpec> Events { get; }

        /// <summary>How many records it holds at most.</summary>
        public abstract int Capacity { get; }

        /// <summary>The oldest sequence it holds.</summary>
        public abstract long First { get; }

        /// <summary>One past the newest sequence it holds.</summary>
        public abstract long Next { get; }

        /// <summary>
        /// What the records lack, which every copy of them lacks too, so a sink that writes them says
        /// so: nothing for a store or a session kept whole, and what its header says for a file.
        /// </summary>
        public abstract LogRedaction Redacted { get; }

        /// <summary>
        /// The mark of every record placed late so far, which a reader that starts now holds: a record
        /// placed late is one given after one with a later sequence, and placed among the records by its
        /// own, as a record written as an error's context reaches a sink after the records it skipped
        /// over. A store numbers its records as it writes them, so it places none.
        /// </summary>
        public abstract LogLateMark LateMark { get; }

        /// <summary>
        /// Where a reader that holds <paramref name="seen"/> reads again from, to see the records placed
        /// late since: the least sequence among them, or <see cref="First"/> when it no longer knows
        /// them all, or <see cref="Next"/> when there are none. With it is the mark the reader holds
        /// after, taken with the answer, so a record placed while it asks is seen now or the next time.
        /// A mark of other records has seen none of these.
        /// </summary>
        public abstract LogLate LateFrom(LogLateMark seen);

        /// <summary>
        /// Copies the records from <paramref name="from"/> on into <paramref name="into"/>, as many as
        /// fit, which it empties first, and says what it copied, what to read next, and how many were
        /// lost before the first.
        /// </summary>
        public abstract LogRead Read(long from, LogBatch into);

        /// <summary>
        /// Copies the records of <paramref name="sequences"/> it holds into <paramref name="into"/>, in
        /// the order given, as many as fit, and says how many.
        /// </summary>
        public abstract int Read(ReadOnlySpan<long> sequences, LogBatch into);
    }

    /// <summary>
    /// How many records a reader has seen placed late, of the records it reads: only they make one,
    /// in <see cref="LogRecords.LateMark"/> and <see cref="LogRecords.LateFrom"/>, so it is never taken
    /// for a sequence, nor for a mark of other records. Its <c>default</c> has seen none, of any.
    /// </summary>
    public readonly record struct LogLateMark
    {
        internal LogLateMark(LogRecords of, long placed)
        {
            Of = of;
            Placed = placed;
        }

        /// <summary>The records whose late records it counts: none for <c>default</c>.</summary>
        internal LogRecords? Of { get; }

        /// <summary>How many were placed late.</summary>
        internal long Placed { get; }
    }

    /// <summary>
    /// What <see cref="LogRecords.LateFrom"/> says: where a reader reads again from, to see the records
    /// placed late since its mark, and the mark to hand it next time.
    /// </summary>
    /// <param name="From">
    /// The least sequence placed late since the mark, <see cref="LogRecords.First"/> when the records
    /// no longer know them all, or <see cref="LogRecords.Next"/> when none was.
    /// </param>
    /// <param name="Mark">The mark the reader holds after, taken with <paramref name="From"/>.</param>
    public readonly record struct LogLate(long From, LogLateMark Mark);
}
