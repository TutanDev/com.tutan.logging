using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Tutan.Functional;

namespace Tutan.Logging.Editor
{
    /// <summary>
    /// The rows the developer selected, each by the sequence of its first record, since the rows
    /// change as records arrive, drop and are scanned again: none, or some, with the one whose details
    /// show.
    /// </summary>
    internal abstract record Selection
    {
        private Selection()
        {
        }

        /// <summary>No rows.</summary>
        public static Selection None { get; } = new Nothing();

        /// <summary>The row whose details show, by the sequence of its first record: none when no rows are selected.</summary>
        public abstract Optional<long> Focused { get; }

        /// <summary>
        /// The rows of <paramref name="sequences"/>, in order and each once, with the details of
        /// <paramref name="focus"/>, or of the last when it is not one of them: none for no rows.
        /// </summary>
        public static Selection Of(IEnumerable<long> sequences, long focus) => Rows.From(sequences, focus);

        /// <summary>
        /// The rows at <paramref name="indexes"/> of <paramref name="view"/>, as a list tells what it
        /// selected: the details are of the row it selected last. An index the rows no longer have is
        /// skipped, and none left is no rows.
        /// </summary>
        public static Selection Of(LogView view, IEnumerable<int> indexes)
        {
            if (view is null)
                throw new ArgumentNullException(nameof(view));
            if (indexes is null)
                throw new ArgumentNullException(nameof(indexes));

            int[] held = indexes.Where(index => index >= 0 && index < view.Count).ToArray();
            return held.Length == 0 ? None : Of(held.Select(index => view[index].Sequence), view[held[held.Length - 1]].Sequence);
        }

        /// <summary>No row is selected.</summary>
        public sealed record Nothing : Selection
        {
            /// <inheritdoc />
            public override Optional<long> Focused => F.None;
        }

        /// <summary>Rows are selected, in order and each once, with the one whose details show among them.</summary>
        public sealed record Rows : Selection
        {
            private Rows(ReadOnlyCollection<long> sequences, long focus)
            {
                Sequences = sequences;
                Focus = focus;
            }

            /// <summary>The rows, each by the sequence of its first record, from the oldest.</summary>
            public ReadOnlyCollection<long> Sequences { get; }

            /// <summary>The row whose details show, one of <see cref="Sequences"/>.</summary>
            public long Focus { get; }

            /// <inheritdoc />
            public override Optional<long> Focused => F.Some(Focus);

            /// <summary>Equal when they select the same rows and show the same one: at once for the same selection, as a window asks each tick.</summary>
            public bool Equals(Rows? other) =>
                ReferenceEquals(this, other) || (other is not null && Focus == other.Focus && Sequences.SequenceEqual(other.Sequences));

            /// <inheritdoc />
            public override int GetHashCode()
            {
                var hash = new HashCode();
                hash.Add(Focus);
                foreach (long sequence in Sequences)
                    hash.Add(sequence);

                return hash.ToHashCode();
            }

            /// <summary>What <see cref="Selection.Of(IEnumerable{long}, long)"/> says: made here, so no rows are made any other way.</summary>
            internal static Selection From(IEnumerable<long> sequences, long focus)
            {
                if (sequences is null)
                    throw new ArgumentNullException(nameof(sequences));

                long[] rows = sequences.Distinct().OrderBy(static sequence => sequence).ToArray();
                if (rows.Length == 0)
                    return None;

                return new Rows(Array.AsReadOnly(rows), Array.BinarySearch(rows, focus) >= 0 ? focus : rows[rows.Length - 1]);
            }
        }
    }

    /// <summary>
    /// The rows a <see cref="Selection"/> selects, as a view has them now: their indexes from the
    /// first, and the index of the one whose details show, while the view has it. A row the view no
    /// longer has is not among them.
    /// </summary>
    internal sealed class SelectedRows
    {
        private SelectedRows(ReadOnlyCollection<int> indexes, Optional<int> focus)
        {
            Indexes = indexes;
            Focus = focus;
        }

        /// <summary>No rows.</summary>
        public static SelectedRows Nothing { get; } = new SelectedRows(Array.AsReadOnly(Array.Empty<int>()), F.None);

        /// <summary>The rows' indexes, from the first.</summary>
        public ReadOnlyCollection<int> Indexes { get; }

        /// <summary>The index of the row whose details show, while the view has it.</summary>
        public Optional<int> Focus { get; }

        /// <summary>The indexes in the order a list is told them: the focus last, as a list tells the row it selected last.</summary>
        public IReadOnlyList<int> FocusLast()
        {
            if (!Focus.HasValue(out int focus))
                return Indexes;

            // The focus is one of the indexes, as the rows found say, so the others fill all but the last place.
            var ordered = new int[Indexes.Count];
            int at = 0;
            for (int i = 0; i < Indexes.Count; i++)
            {
                if (Indexes[i] != focus)
                    ordered[at++] = Indexes[i];
            }

            ordered[at] = focus;
            return ordered;
        }

        /// <summary>The rows of <paramref name="view"/> that <paramref name="selection"/> selects.</summary>
        public static SelectedRows Of(Selection selection, LogView view)
        {
            if (selection is null)
                throw new ArgumentNullException(nameof(selection));
            if (view is null)
                throw new ArgumentNullException(nameof(view));

            return selection switch
            {
                Selection.Nothing => Nothing,
                Selection.Rows rows => Found(rows, view),
                _ => throw new ArgumentOutOfRangeException(nameof(selection), $"{selection.GetType().Name} is not a selection the rows know."),
            };
        }

        /// <summary>
        /// The rows found of <paramref name="rows"/>, in one pass: the sequences are in order and the rows
        /// of a view are too, so the rows found are in order as well, and a repeat whose run spans two of
        /// the sequences is found twice in a row, which is kept once.
        /// </summary>
        private static SelectedRows Found(Selection.Rows rows, LogView view)
        {
            ReadOnlyCollection<long> sequences = rows.Sequences;
            var found = new int[sequences.Count];
            int count = 0;
            for (int i = 0; i < sequences.Count; i++)
            {
                if (view.Find(sequences[i]).HasValue(out int index) && (count == 0 || found[count - 1] != index))
                    found[count++] = index;
            }

            if (count == 0)
                return Nothing;

            if (count < found.Length)
                Array.Resize(ref found, count);

            // The focus is one of the sequences, so it is found whenever any row is.
            return new SelectedRows(Array.AsReadOnly(found), view.Find(rows.Focus));
        }
    }

    /// <summary>
    /// What the indexes of a view's rows are of, as far as they can move: the session, the scan the
    /// window started last, whether one is under way, the late records placed among the rows, and the
    /// first row, which moves every index as rows drop from the front. A row added after the last
    /// moves none, so a selection's indexes stay while only that happens.
    /// </summary>
    /// <param name="Source">The session's records the view reads.</param>
    /// <param name="Scans">How many scans the window started on the view, as for a filter or a fold.</param>
    /// <param name="Scanning">Whether a scan is under way, whose rows replace these when it catches up.</param>
    /// <param name="Late">The late records the session placed among what the view read.</param>
    /// <param name="First">The sequence of the first row: none while there are no rows.</param>
    internal readonly record struct IndexMark(LogRecords Source, int Scans, bool Scanning, LogLateMark Late, Optional<long> First);

    /// <summary>
    /// What a list's selection is drawn of: the rows selected, and where the view's rows are. The
    /// selection is drawn again only when either changed, not as each record arrives.
    /// </summary>
    /// <param name="Selection">The rows selected.</param>
    /// <param name="Rows">Where the rows of the view are: none while no session is shown or no row is selected, since no index then moves anything the list is told.</param>
    internal readonly record struct SelectionMark(Selection Selection, Optional<IndexMark> Rows);
}
