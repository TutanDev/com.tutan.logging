using System;
using NUnit.Framework;
using Tutan.Functional;
using Tutan.Logging.Editor;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace Tutan.Logging.Tests
{
    /// <summary>
    /// What the profile promises of the console window's tick: one with nothing new allocates nothing,
    /// and one with records allocates only for what they changed. Each test binds a console session's
    /// records as the window does, and asks of them all a tick of the window asks: the update, what
    /// each row and each category is bound of, and <see cref="LogConsoleWindow.Bound.Marks"/>, the one
    /// value every other part is drawn of. What UI Toolkit allocates to draw is Unity's, and is not
    /// measured, nor is what the session allocates to keep records, which the allocation tests measure.
    /// </summary>
    public class ConsoleTickAllocationTests
    {
        private const string File = "slot1.sav";

        [Test]
        public void A_tick_with_nothing_new_allocates_nothing()
        {
            using var session = new Session();
            LogScope open = Write(session.Store);
            session.Pump();
            var window = new Window(session.Records);
            LogView view = window.Bound.View;
            window.Tick();
            Assert.That((view.Count, view[3].Kind, view[4].Depth), Is.EqualTo((6, LogRowKind.Scope, 1)), "A repeat, a scope that closed, one open, and a row after.");
            Assert.That(window.Bound.Marks(window.State).Selection.Rows.IsNone, Is.True, "With nothing selected, where the rows are moves nothing the list is told.");

            // The row under the scope still open, so the details walk a chain with an open scope in it.
            window.Show(window.State.Select(Selection.Of(new[] { view[4].Sequence }, view[4].Sequence)));
            window.Tick();
            window.Tick();
            Assert.That((window.Drew, window.Counted), Is.EqualTo((false, false)), "Drawn once, nothing is drawn again.");

            Assert.That(window.Tick, Is.Not.AllocatingGCMemory());
            Assert.That((window.Change, window.Drew, window.Counted), Is.EqualTo((LogConsoleWindow.RowsChange.Nothing, false, false)));
            open.Dispose();
        }

        [Test]
        public void A_tick_with_records_the_rows_fold_away_allocates_nothing()
        {
            using var session = new Session();
            LogScope open = Write(session.Store);
            session.Pump();
            var window = new Window(session.Records);
            LogView view = window.Bound.View;
            window.Tick();
            long scope = view[3].Sequence;

            // The scope still open folded, and the row under the scope that closed selected.
            window.Show(window.State.ToggleFold(scope).Select(Selection.Of(new[] { view[2].Sequence }, view[2].Sequence)));
            int slot = 0;
            void Keep()
            {
                open.Log.Step(slot++);
                session.Pump();
            }

            // Past the scan the fold started, and past what the first records under it make.
            for (int i = 0; i < 8; i++)
            {
                Keep();
                window.Tick();
            }

            Assert.That((view.IsScanning, view.Count, view.IsFolded(scope)), Is.EqualTo((false, 5, true)), "The folded scope's rows are gone, and its own row stays.");

            Keep();
            Assert.That(window.Tick, Is.Not.AllocatingGCMemory());
            Assert.That((window.Change, window.Drew, window.Counted), Is.EqualTo((LogConsoleWindow.RowsChange.Rows, false, true)),
                "The records changed what the rows are of and what is counted, and nothing the rows show: a row held, an open scope's too, is drawn as it was.");
            open.Dispose();
        }

        [Test]
        public void A_tick_with_records_the_rows_show_draws_only_their_rows()
        {
            using var session = new Session();
            LogScope open = Write(session.Store);
            session.Pump();
            var window = new Window(session.Records);
            LogView view = window.Bound.View;
            window.Tick();
            window.Show(window.State.Select(Selection.Of(new[] { view[4].Sequence }, view[4].Sequence)));
            window.Tick();
            RowCells[] before = { window.Bound.Cells(0), window.Bound.Cells(3), window.Bound.Cells(5) };

            session.Store.Log.Wrote(5);
            session.Pump();
            window.Tick();

            Assert.That((window.Change, view.Count), Is.EqualTo((LogConsoleWindow.RowsChange.Rows, 7)));
            Assert.That(window.Bound.Cells(0), Is.SameAs(before[0]), "A row that was there is drawn of the cells read before.");
            Assert.That(window.Bound.Cells(3), Is.SameAs(before[1]), "An open scope's too, while it is open.");
            Assert.That(window.Bound.Cells(5), Is.SameAs(before[2]));
            Assert.That(window.Drew, Is.True, "The new row is.");

            open.Dispose();
            session.Pump();
            window.Tick();
            Assert.That(window.Bound.Cells(3), Is.Not.SameAs(before[1]), "The scope closed, so its row is read again, to say how long it took.");
            Assert.That(window.Bound.Cells(3).Duration, Is.Not.EqualTo("open"));
        }

        /// <summary>
        /// A repeat, a scope that closed with a row under it, a scope still open with a row under it,
        /// and a row after, all at the top: the scope still open is returned, for the test to close.
        /// </summary>
        private static LogScope Write(LogStore store)
        {
            Log log = store.Log;
            log.Saved(1, File);
            log.Saved(1, File);
            using (LogScope saving = log.Saving(2))
                saving.Log.Step(1);

            LogScope open = log.Saving(3);
            open.Log.Step(2);
            log.Wrote(4);
            return open;
        }

        /// <summary>A console session of its own, kept as the window's are: by a console sink a drain pumps from a store.</summary>
        private sealed class Session : IDisposable
        {
            private readonly ConsoleSessions _sessions = new ConsoleSessions(live: true);
            private readonly LogDrain _drain;

            public Session()
            {
                Store = Stores.Everything();
                _drain = new LogDrain(Store, (new LogConsoleSink(1024, "console", _sessions), new LogSinkOptions(LogLevel.Trace)));
            }

            public LogStore Store { get; }

            /// <summary>The session's records, as the window binds them.</summary>
            public LogArchive Records => _sessions.Sessions[0].Records;

            /// <summary>Keeps in the session what the store holds that it has not kept.</summary>
            public void Pump() => _drain.Pump();

            public void Dispose() => _drain.Dispose();
        }

        /// <summary>
        /// What the window asks of the rows of the session shown on a tick that draws, and what it last
        /// drew of them: a row's cells and what else its binding asks, each category's, and the marks
        /// every other part is drawn of.
        /// </summary>
        private sealed class Window
        {
            private RowCells?[] _cells = new RowCells?[8];
            private long[] _counts = new long[8];
            private Optional<LogConsoleWindow.RowsMarks> _marks;

            public Window(LogRecords records)
            {
                State = ConsoleState.Start.StopFollowing().Choose(new LogSessionId(1));
                Bound = LogConsoleWindow.Bound.Of(records, State);
            }

            public ConsoleState State { get; private set; }

            public LogConsoleWindow.Bound Bound { get; }

            /// <summary>What the last tick's update said changed.</summary>
            public LogConsoleWindow.RowsChange Change { get; private set; }

            /// <summary>Whether the last tick drew anything the rows show again: a row's cells, the selection, the details, the lines said, or the count of rows.</summary>
            public bool Drew { get; private set; }

            /// <summary>Whether the last tick drew a count again: a badge's, or a category's.</summary>
            public bool Counted { get; private set; }

            /// <summary>Makes <paramref name="next"/> the state, as an input does.</summary>
            public void Show(ConsoleState next)
            {
                Bound.Show(State, next);
                State = next;
            }

            /// <summary>A tick, as the window's: the rows updated, then all a drawing asks of them, each compared with what was drawn.</summary>
            public void Tick()
            {
                Change = Bound.Update();
                LogView view = Bound.View;
                bool drew = false;
                Grow(ref _cells, view.Count);
                for (int i = 0; i < view.Count; i++)
                {
                    RowCells cells = Bound.Cells(i);
                    drew |= !ReferenceEquals(cells, _cells[i]);
                    _cells[i] = cells;

                    // What else a row's binding asks of it.
                    _ = TextLinesSink.Short(cells.Row.Level);
                    for (int level = 0; level < LevelLook.Shown.Count; level++)
                        _ = LevelLook.Class(LevelLook.Shown[level]);
                    _ = CategoryHue.Colour(cells.Category, dark: true);
                }

                LogConsoleWindow.RowsMarks marks = Bound.Marks(State);
                bool wasDrawn = _marks.HasValue(out LogConsoleWindow.RowsMarks drawn);
                drew |= !wasDrawn || drawn.Selection != marks.Selection || drawn.Details != marks.Details || drawn.Said != marks.Said
                    || drawn.Status != marks.Status;
                bool counted = !wasDrawn || drawn.Badges != marks.Badges;
                _marks = marks;

                // What each category's item is bound of: a count's text is drawn again only as the count changes.
                CategoryTree tree = Bound.Tree;
                Grow(ref _counts, tree.Nodes.Count);
                for (int i = 0; i < tree.Nodes.Count; i++)
                {
                    CategoryNode node = tree.Nodes[i];
                    _ = tree.Shown(node, State.Filter);
                    _ = node.Label;
                    _ = CategoryHue.Colour(node.Category, dark: true);
                    long count = tree.Count(node, view);
                    counted |= count != _counts[i];
                    _counts[i] = count;
                }

                Drew = drew;
                Counted = counted;
            }

            /// <summary>Makes <paramref name="items"/> hold at least <paramref name="count"/>, which allocates only as the rows or the categories grow, as warming up does.</summary>
            private static void Grow<T>(ref T[] items, int count)
            {
                if (items.Length < count)
                    Array.Resize(ref items, Math.Max(count, items.Length * 2));
            }
        }
    }
}
