using System.Collections.Generic;
using Tutan.Functional;

namespace Tutan.Logging.Editor
{
    internal sealed partial class LogConsoleWindow
    {
        /// <summary>What an update of the rows changed of what the window draws.</summary>
        internal enum RowsChange
        {
            /// <summary>Nothing the rows show changed, so nothing is drawn again.</summary>
            Nothing,

            /// <summary>The rows, or what is counted of them, changed.</summary>
            Rows,

            /// <summary>The rows changed, and the session declared events, so the tree of its categories was made again.</summary>
            RowsAndCategories,
        }

        /// <summary>
        /// The view of the session shown, with the tree of its categories and what was read of its
        /// rows: made when a session is shown, and let go of with it, so none of it outlives the others.
        /// It changes only through its own methods, and what the window asks of it each tick allocates
        /// nothing unless what it shows changed, as the allocation tests hold.
        /// </summary>
        internal sealed class Bound
        {
            // The cells read, by the sequence of their row, so a row added or dropped moves none: each
            // is read again only when it may show something else, as HeldCells.Still says.
            private readonly Dictionary<long, HeldCells> _cells = new Dictionary<long, HeldCells>();
            private readonly LogBatch _record = new LogBatch(1);
            private readonly List<long> _gone = new List<long>();
            private long _prunedBefore;

            // What the rows were as the last update found them: none before the first.
            private Optional<ViewMark> _last;

            // Where the records were before the last update read them, which every mark is of: the default before the first.
            private SourcePosition _read;

            // How many scans the window started, for a filter, a fold or a retry: a scan can catch up
            // within one tick with rows that look as many as before, so it is told by this.
            private int _scans;

            private Bound(LogView view)
            {
                View = view;
                Tree = CategoryTree.Of(view.Source.Events);
            }

            /// <summary>The rows of the session shown.</summary>
            public LogView View { get; }

            /// <summary>The tree of the session's categories, made again when the session declares more events.</summary>
            public CategoryTree Tree { get; private set; }

            /// <summary>Where the rows are, as far as a selection's indexes can move.</summary>
            public IndexMark Indexes =>
                new IndexMark(View.Source, _scans, View.IsScanning, _read.Late, View.Count > 0 ? F.Some(View[0].Sequence) : F.None);

            /// <summary>What of the rows the lines under the query line are of.</summary>
            public RowsSaid Said => new RowsSaid(View.Shown, _scans, View.Source.Events.Count, View.GaveUp.Count, View.Lost);

            /// <summary>A view of <paramref name="records"/> as <paramref name="state"/> asks: its filter, and the scopes it folds.</summary>
            public static Bound Of(LogRecords records, ConsoleState state)
            {
                var bound = new Bound(new LogView(records, state.Filter));
                bound.Refold(ScopeSet.Empty, state.Folded);
                return bound;
            }

            /// <summary>Makes the view show what <paramref name="now"/> asks: its filter, and the scopes it folds where they changed since <paramref name="was"/>.</summary>
            public void Show(ConsoleState was, ConsoleState now)
            {
                if (!now.Filter.Equals(View.Filter))
                {
                    View.Filter = now.Filter;
                    _scans++;
                }

                Refold(was.Folded, now.Folded);
            }

            /// <summary>Scans the rows again, so the regular expressions that took too long are tried afresh.</summary>
            public void Retry()
            {
                View.Retry();
                _scans++;
            }

            /// <summary>
            /// Reads what arrived since the last update, or goes on with a scan, and says what that changed
            /// of what the window draws: letting go of the cells no row shows, and making the tree of the
            /// categories again when the session declared events.
            /// </summary>
            public RowsChange Update()
            {
                // Where the records were before the view read them, so the mark is of no more than it read: a
                // record kept while it reads, as a drain pumped on another thread keeps one, draws once more
                // on the next update, and never not at all.
                LogRecords source = View.Source;
                var position = new SourcePosition(source.First, source.Next, source.LateMark);
                View.Update();
                _read = position;
                var mark = ViewMark.Of(View, _scans, position);
                Optional<ViewMark> was = _last;
                if (was.HasValue(out ViewMark last) && last == mark)
                    return RowsChange.Nothing;

                _last = mark;
                Prune(was, mark);
                if (Tree.Events == View.Source.Events.Count)
                    return RowsChange.Rows;

                Tree = CategoryTree.Of(View.Source.Events);
                return RowsChange.RowsAndCategories;
            }

            /// <summary>The cells of the row at <paramref name="index"/>, read again only when what was read of it may be out of date.</summary>
            public RowCells Cells(int index)
            {
                LogRow row = View[index];
                bool scope = row.Kind == LogRowKind.Scope;
                bool folded = scope && View.IsFolded(row.Sequence);
                long first = View.Source.First;
                if (_cells.TryGetValue(row.Sequence, out HeldCells? held) && held.Still(row, folded, first, View.Scopes))
                    return held.Cells;

                Optional<RowScope> shows = scope ? F.Some(new RowScope(ScopeTime.Of(View.Scopes, row.Sequence), folded)) : F.None;
                RowCells cells = View.Read(index, 1, _record) == 1
                    ? RowCells.Of(_record[0], row, View.Source.Clock, shows)
                    : RowCells.Gone(row, shows);
                _cells[row.Sequence] = HeldCells.Of(cells, first);
                return cells;
            }

            /// <summary>The index of the row whose details <paramref name="selection"/> shows, while the rows have it.</summary>
            public Optional<int> Focus(Selection selection) => selection.Focused.HasValue(out long focused) ? View.Find(focused) : F.None;

            /// <summary>
            /// What the parts are drawn of, as far as the rows say, for <paramref name="state"/>: the one value
            /// the window draws each part of, and the tests compare, so a tick asks of the rows what the tests do.
            /// </summary>
            public RowsMarks Marks(ConsoleState state)
            {
                Selection selection = state.Selection;
                Optional<FocusMark> focus = Focus(selection).HasValue(out int index) ? F.Some(new FocusMark(index, Key(index))) : F.None;

                // With nothing selected, where the rows are moves nothing the list is told.
                Optional<IndexMark> rows = selection.Focused.IsSome ? F.Some(Indexes) : F.None;
                return new RowsMarks(new SelectionMark(selection, rows), focus, Said,
                    new BadgesMark(View.Counts, state.Filter.Levels), new StatusMark(View.Count, View.IsScanning));
            }

            /// <summary>What the details of the row at <paramref name="index"/> are of, as far as they can change, read without allocating.</summary>
            public DetailsKey Key(int index)
            {
                LogRow row = View[index];
                LogRecords source = View.Source;
                if (View.Read(index, 1, _record) != 1)
                    return new DetailsKey(source, row, _read.Late, F.None);

                LogRecord record = _record[0];
                var held = new HeldMark(record.Sequence, ChainMark.Of(View.Scopes, record.Scope, source.First));
                return new DetailsKey(source, row, _read.Late, F.Some(held));
            }

            /// <summary>Folds and unfolds the view where <paramref name="now"/> differs from <paramref name="was"/>.</summary>
            private void Refold(ScopeSet was, ScopeSet now)
            {
                if (now.Equals(was))
                    return;

                foreach (long scope in was.Scopes)
                {
                    if (!now.Contains(scope))
                        View.Unfold(scope);
                }

                foreach (long scope in now.Scopes)
                {
                    if (!was.Contains(scope))
                        View.Fold(scope);
                }

                _scans++;
            }

            /// <summary>
            /// Lets go of the cells no row shows: all when a scan's rows replaced the rows since
            /// <paramref name="was"/>, or nothing was read before, and else those of rows the view dropped from its start.
            /// </summary>
            private void Prune(Optional<ViewMark> was, ViewMark now)
            {
                // A scan's rows replace the rows once it catches up: within the last tick, or since one started.
                if (!was.HasValue(out ViewMark before) || (!now.Scanning && (before.Scanning || before.Scans != now.Scans)))
                {
                    _cells.Clear();
                    _prunedBefore = 0;
                    return;
                }

                long first = View.Count > 0 ? View[0].Sequence : long.MaxValue;
                if (first == _prunedBefore)
                    return;

                // Through a list, as a dictionary is not changed while it is gone through; held for every tick.
                _prunedBefore = first;
                _gone.Clear();
                foreach (KeyValuePair<long, HeldCells> pair in _cells)
                {
                    if (pair.Key < first)
                        _gone.Add(pair.Key);
                }

                for (int i = 0; i < _gone.Count; i++)
                    _cells.Remove(_gone[i]);
            }
        }

        /// <summary>What the parts of the window are drawn of, as far as the rows of the session shown say.</summary>
        /// <param name="Selection">What the list is told of the selection.</param>
        /// <param name="Focus">The row whose details show, while the rows have it.</param>
        /// <param name="Said">What of the rows the lines under the query line are of.</param>
        /// <param name="Badges">What the badges are drawn of.</param>
        /// <param name="Status">What the count of rows is drawn of.</param>
        internal readonly record struct RowsMarks(SelectionMark Selection, Optional<FocusMark> Focus, RowsSaid Said, BadgesMark Badges, StatusMark Status)
        {
            /// <summary>What the details are drawn of: the focus's key, without its index, so rows dropped before it draw nothing again.</summary>
            public Optional<DetailsKey> Details => Focus.HasValue(out FocusMark focus) ? F.Some(focus.Details) : F.None;
        }

        /// <summary>The row whose details show: its index in the rows, and what its details are of.</summary>
        internal readonly record struct FocusMark(int Index, DetailsKey Details);

        /// <summary>What the badges are drawn of: the count of each level, and the levels the filter keeps.</summary>
        internal readonly record struct BadgesMark(LogLevelCounts Counts, LogLevelRange Levels);

        /// <summary>What the count of rows under the list is drawn of: how many, and whether a scan is on.</summary>
        internal readonly record struct StatusMark(int Rows, bool Scanning);

        /// <summary>Where a view's records were before it read them: the first held, the next to come, and the late mark.</summary>
        private readonly record struct SourcePosition(long First, long Next, LogLateMark Late);

        /// <summary>What the rows were as an update found them, read from where the records were before it: when any of it changes, they are drawn again.</summary>
        private readonly record struct ViewMark(LogRecords Source, int Scans, SourcePosition Position, int Count, bool Scanning,
            LogFilter Shown, long Lost, int Events, int GaveUp)
        {
            public static ViewMark Of(LogView view, int scans, SourcePosition position) => new ViewMark(view.Source, scans, position,
                view.Count, view.IsScanning, view.Shown, view.Lost, view.Source.Events.Count, view.GaveUp.Count);
        }
    }
}
