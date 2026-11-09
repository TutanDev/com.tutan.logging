using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Tutan.Functional;
using Tutan.Logging.Editor;

namespace Tutan.Logging.Tests
{
    // Each test keeps its sessions in a list of its own, so a run never touches the process's, which
    // a developer may be reading in the console.
    public class ConsoleStateTests
    {
        private static readonly LogClock s_clock = new LogClock(new DateTimeOffset(2026, 11, 4, 9, 0, 0, TimeSpan.Zero), 0);

        [Test]
        public void A_query_that_reads_is_the_filter_of_the_rows()
        {
            ConsoleState state = ConsoleState.Start.Type("level>=warning  tests.save.*");

            Assert.That(state.Query.Text, Is.EqualTo("level>=warning  tests.save.*"), "The line is as typed.");
            Assert.That(state.Filter, Is.EqualTo(Parse("level>=warning tests.save.*")));
            Assert.That(state.Query.Refused.IsNone, Is.True);
        }

        [Test]
        public void A_query_that_does_not_read_keeps_the_filter_it_had_and_says_why()
        {
            ConsoleState refused = ConsoleState.Start.Type("level>=warning").Type("level>=loud");

            Assert.That(refused.Query.Text, Is.EqualTo("level>=loud"));
            Assert.That(refused.Filter, Is.EqualTo(Parse("level>=warning")), "The rows stay those of the last query that read.");
            Assert.That(refused.Query.Refused.IsSome, Is.True);
            Assert.That(refused.Type("level>=error").Query.Refused.IsNone, Is.True);
        }

        [Test]
        public void A_badge_shows_only_its_level_and_again_every_level_keeping_the_other_terms()
        {
            ConsoleState once = ConsoleState.Start.Type("tests.save.* -\"slow\"").PressBadge(LogLevel.Warning);
            ConsoleState twice = once.PressBadge(LogLevel.Warning);

            Assert.That(once.Query.Text, Is.EqualTo("level=warning tests.save.* -\"slow\""));
            Assert.That(once.PressBadge(LogLevel.Error).Query.Text, Is.EqualTo("level=error tests.save.* -\"slow\""));
            Assert.That(twice.Query.Text, Is.EqualTo("tests.save.* -\"slow\""));
            Assert.That(twice.Filter.Levels, Is.EqualTo(LogLevelRange.All));
            Assert.That(ConsoleState.Start.Type("level>=warning").PressBadge(LogLevel.Warning).Query.Text, Is.EqualTo("level=warning"),
                "A range that holds the level is not the level alone.");
        }

        [Test]
        public void A_press_writes_the_line_anew_from_the_filter_the_rows_are_of()
        {
            ConsoleState pressed = ConsoleState.Start.Type("tests.save.*").Type("level>=loud").PressBadge(LogLevel.Error);

            Assert.That(pressed.Query.Text, Is.EqualTo("level=error tests.save.*"));
            Assert.That(pressed.Query.Refused.IsNone, Is.True, "The line that did not read is gone with its refusal.");
        }

        [Test]
        public void A_badge_is_of_a_level_a_record_has()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => ConsoleState.Start.PressBadge(LogLevel.None));
        }

        [Test]
        public void Showing_only_a_scope_replaces_the_scope_kept_before()
        {
            ConsoleState state = ConsoleState.Start.Type("tests.save.* scope:4 -thread:2");

            Assert.That(state.FilterToScope(9).Query.Text, Is.EqualTo("tests.save.* scope:9 -thread:2"));
            Assert.That(ConsoleState.Start.FilterToScope(3).Query.Text, Is.EqualTo("scope:3"));
        }

        [Test]
        public void Choosing_a_session_reads_its_rows_afresh_and_keeps_the_query()
        {
            var first = new LogSessionId(1);
            var second = new LogSessionId(2);
            ConsoleState shown = ConsoleState.Start.Type("level>=warning").Choose(first).ToggleFold(5).Select(Selection.Of(new[] { 3L }, 3));

            ConsoleState chosen = shown.Choose(second);

            Assert.That(chosen.Session, Is.EqualTo(F.Some(second)));
            Assert.That(chosen.Folded, Is.EqualTo(ScopeSet.Empty));
            Assert.That(chosen.Selection, Is.SameAs(Selection.None));
            Assert.That(chosen.Query.Text, Is.EqualTo("level>=warning"));
            Assert.That(shown.Choose(first), Is.SameAs(shown), "The session shown, chosen again, changes nothing.");
        }

        [Test]
        public void A_live_session_that_starts_is_chosen_while_it_follows()
        {
            var sessions = new ConsoleSessions(live: true);
            LogConsoleSession.Live first = Start(sessions);
            ConsoleState state = ConsoleState.Start.SessionsChanged(sessions.Sessions);
            Assert.That(state.Session, Is.EqualTo(F.Some(first.Id)));

            LogConsoleSession.Live second = Start(sessions);

            Assert.That(state.SessionsChanged(sessions.Sessions).Session, Is.EqualTo(F.Some(second.Id)));
            ConsoleState still = state.StopFollowing().SessionsChanged(sessions.Sessions);
            Assert.That(still.Session, Is.EqualTo(F.Some(first.Id)), "Not following, the session shown stays.");
            Assert.That(still.NewestSeen, Is.EqualTo(F.Some(new LogSessionId(2))), "It saw the new one start.");
            Assert.That(still.Follow().SessionsChanged(sessions.Sessions).Session, Is.EqualTo(F.Some(first.Id)),
                "A session seen start is not chosen as new later.");
        }

        [Test]
        public void A_session_chosen_stays_though_a_newer_one_was_seen()
        {
            var sessions = new ConsoleSessions(live: true);
            LogConsoleSession.Live first = Start(sessions);
            Start(sessions);
            ConsoleState state = ConsoleState.Start.SessionsChanged(sessions.Sessions).Choose(first.Id);

            Assert.That(state.SessionsChanged(sessions.Sessions).Session, Is.EqualTo(F.Some(first.Id)));
        }

        [Test]
        public void With_nothing_shown_the_newest_live_session_is_chosen_before_a_newer_one_that_is_not()
        {
            var sessions = new ConsoleSessions(live: true);
            sessions.End(Start(sessions), s_clock.Started);
            LogConsoleSession.Live live = Start(sessions);
            sessions.Open(FileOf("a.jsonl"));

            ConsoleState state = ConsoleState.Start.StopFollowing().SessionsChanged(sessions.Sessions);

            Assert.That(state.Session, Is.EqualTo(F.Some(live.Id)));
            Assert.That(state.NewestSeen, Is.EqualTo(F.Some(new LogSessionId(3))));
            Assert.That(ConsoleState.Start.NewestSeen.IsNone, Is.True, "Before any session, none was seen.");
        }

        [Test]
        public void A_session_shown_that_ends_stays_shown_with_its_folds_and_its_selection()
        {
            var sessions = new ConsoleSessions(live: true);
            LogConsoleSession.Live live = Start(sessions);
            ConsoleState state = ConsoleState.Start.SessionsChanged(sessions.Sessions).ToggleFold(2).Select(Selection.Of(new[] { 4L }, 4));

            sessions.End(live, s_clock.Started);

            Assert.That(state.SessionsChanged(sessions.Sessions), Is.SameAs(state), "It ended under its id, so nothing the window shows changed.");
        }

        [Test]
        public void When_the_session_shown_is_gone_the_newest_of_all_is_chosen_or_none()
        {
            var sessions = new ConsoleSessions(live: true);
            sessions.End(Start(sessions), s_clock.Started);
            LogConsoleSession.File older = sessions.Open(FileOf("a.jsonl"));
            LogConsoleSession.File newer = sessions.Open(FileOf("b.jsonl"));
            ConsoleState state = ConsoleState.Start.SessionsChanged(sessions.Sessions);
            Assert.That(state.Session, Is.EqualTo(F.Some(newer.Id)), "With no live session, the newest of all.");

            sessions.Close(newer);
            ConsoleState gone = state.ToggleFold(4).Select(Selection.Of(new[] { 1L }, 1)).SessionsChanged(sessions.Sessions);

            Assert.That(gone.Session, Is.EqualTo(F.Some(older.Id)));
            Assert.That((gone.Folded, gone.Selection), Is.EqualTo((ScopeSet.Empty, Selection.None)), "Its folds and selection went with it.");

            sessions.Close(older);
            sessions.Close((LogConsoleSession.Ended)sessions.Sessions[0]);
            ConsoleState none = gone.ToggleFold(2).SessionsChanged(sessions.Sessions);

            Assert.That(none.Session.IsNone, Is.True);
            Assert.That(none.Folded, Is.EqualTo(ScopeSet.Empty));
            Assert.That(none.SessionsChanged(sessions.Sessions), Is.SameAs(none), "Nothing changed.");
        }

        [Test]
        public void A_fold_toggles_and_is_of_a_scope()
        {
            ConsoleState folded = ConsoleState.Start.Choose(new LogSessionId(1)).ToggleFold(7).ToggleFold(3);

            Assert.That(folded.Folded.Scopes, Is.EqualTo(new[] { 3L, 7L }));
            Assert.That(folded.ToggleFold(7).Folded.Scopes, Is.EqualTo(new[] { 3L }));
            Assert.Throws<ArgumentOutOfRangeException>(() => ConsoleState.Start.ToggleFold(0));
        }

        [Test]
        public void With_no_session_shown_there_is_nothing_to_fold_select_or_clear()
        {
            ConsoleState start = ConsoleState.Start;

            Assert.That(start.ToggleFold(5), Is.SameAs(start));
            Assert.That(start.Select(Selection.Of(new[] { 3L }, 3)), Is.SameAs(start));
            Assert.That(start.Cleared(Start(new ConsoleSessions(live: true))), Is.SameAs(start));
            Assert.That((start.Folded, start.Selection), Is.EqualTo((ScopeSet.Empty, Selection.None)));
            Assert.Throws<ArgumentException>(() => SessionShown.Of(default), "A session shown is one the console numbered.");
            Assert.Throws<ArgumentException>(() => start.Choose(default));
        }

        [Test]
        public void A_state_read_back_after_a_reload_is_the_state_saved()
        {
            ConsoleState saved = ConsoleState.Start.Type("tests.save.* level>=warning").Type("level>=loud").StopFollowing();

            ConsoleState restored = ConsoleState.Restored(saved.Query.Text, saved.Filter.ToString(), saved.Following);

            Assert.That(restored, Is.EqualTo(saved));
            Assert.That(restored.Query.Refused.IsSome, Is.True, "The line that did not read still does not, and the rows keep the filter they were of.");
            Assert.That(ConsoleState.Restored("", "", following: true), Is.EqualTo(ConsoleState.Start));
            Assert.That(ConsoleState.Restored("tests.*", "level>=loud", following: true).Filter, Is.EqualTo(Parse("tests.*")),
                "A filter saved that does not read keeps every record, and the line reads over it.");
            Assert.Throws<ArgumentNullException>(() => ConsoleState.Restored(null!, "", following: true));
            Assert.Throws<ArgumentNullException>(() => ConsoleState.Restored("", null!, following: true));
        }

        [Test]
        public void What_the_window_saves_reads_back_as_the_state_saved_and_what_another_version_saved_as_none()
        {
            ConsoleState state = ConsoleState.Start.Type("tests.save.* level>=warning").Type("level>=loud").StopFollowing();

            SavedConsole saved = SavedConsole.FromDomain(state);

            Assert.That((saved.Version, saved.Query, saved.Filter, saved.Following),
                Is.EqualTo((SavedConsole.Current, "level>=loud", state.Filter.ToString(), false)));
            Assert.That(saved.ToDomain(), Is.EqualTo(F.Some(state)));
            Assert.That(new SavedConsole().ToDomain().IsNone, Is.True, "Nothing saved, as a window's first opening has.");
            Assert.That(new SavedConsole { Version = SavedConsole.Current + 1, Query = "tests.*" }.ToDomain().IsNone, Is.True,
                "Saved by a version this never wrote, as a newer package's.");
            Assert.That(new SavedConsole { Version = -1, Query = "tests.*" }.ToDomain().IsNone, Is.True, "No version wrote it.");
            Assert.That(new SavedConsole { Version = SavedConsole.Current, Query = null!, Filter = null! }.ToDomain(), Is.EqualTo(F.Some(ConsoleState.Start)),
                "A field not written reads as empty.");
            Assert.Throws<ArgumentNullException>(() => SavedConsole.FromDomain(null!));
        }

        [Test]
        public void A_console_saved_by_version_1_reads_back_as_it_was()
        {
            // Version 1 as Unity's serializer writes it: renaming a field breaks this, as it would every layout saved.
            // Its query does not read, so the rows are of the filter saved, and each field changes what is read;
            // Following is read both ways, so a rename shows whether a field missing keeps its initializer or not.
            const string v1 = "{\"Version\":1,\"Query\":\"level>=loud\",\"Filter\":\"level>=warning tests.save.*\",\"Following\":false}";
            const string v1Following = "{\"Version\":1,\"Query\":\"level>=loud\",\"Filter\":\"level>=warning tests.save.*\",\"Following\":true}";
            ConsoleState following = ConsoleState.Start.Type("level>=warning tests.save.*").Type("level>=loud");
            ConsoleState state = following.StopFollowing();
            Assert.That(state.Query.Refused.IsSome, Is.True);
            Assert.That(following.Following, Is.True);

            SavedConsole read = UnityEngine.JsonUtility.FromJson<SavedConsole>(v1);
            SavedConsole again = UnityEngine.JsonUtility.FromJson<SavedConsole>(UnityEngine.JsonUtility.ToJson(SavedConsole.FromDomain(state)));

            Assert.That(read.ToDomain(), Is.EqualTo(F.Some(state)));
            Assert.That(UnityEngine.JsonUtility.FromJson<SavedConsole>(v1Following).ToDomain(), Is.EqualTo(F.Some(following)));
            Assert.That(again.ToDomain(), Is.EqualTo(F.Some(state)), "What is written reads back.");
            Assert.That(UnityEngine.JsonUtility.FromJson<SavedConsole>("{}").ToDomain().IsNone, Is.True, "A layout of no version is none.");
        }

        [Test]
        public void Scrolling_up_stops_following_and_scrolling_to_the_newest_follows_again()
        {
            ConsoleState start = ConsoleState.Start;
            ConsoleState up = start.Scrolled(toNewest: false);

            Assert.That(start.Following, Is.True);
            Assert.That(up.Following, Is.False);
            Assert.That(up.Scrolled(toNewest: true).Following, Is.True);
            Assert.That(start.Follow(), Is.SameAs(start));
            Assert.That(up.StopFollowing(), Is.SameAs(up));
        }

        [Test]
        public void Clearing_a_live_session_shown_lets_its_folds_and_its_selection_go_and_keeps_the_rest()
        {
            var sessions = new ConsoleSessions(live: true);
            LogConsoleSession.Live live = Start(sessions);
            ConsoleState state = ConsoleState.Start.Type("level>=error").StopFollowing().Choose(live.Id).ToggleFold(2)
                .Select(Selection.Of(new[] { 4L }, 4));

            ConsoleState cleared = state.Cleared(live);

            Assert.That((cleared.Folded, cleared.Selection), Is.EqualTo((ScopeSet.Empty, Selection.None)), "They went with its records.");
            Assert.That((cleared.Session, cleared.Query.Text, cleared.Following), Is.EqualTo((F.Some(live.Id), "level>=error", false)));
        }

        [Test]
        public void Clearing_keeps_all_a_file_or_an_ended_session_shown_has_and_all_another_session_cleared_has()
        {
            var sessions = new ConsoleSessions(live: true);
            sessions.End(Start(sessions), s_clock.Started);
            var ended = (LogConsoleSession.Ended)sessions.Sessions[0];
            LogConsoleSession.File file = sessions.Open(FileOf("a.jsonl"));
            LogConsoleSession.Live live = Start(sessions);
            ConsoleState Folded(LogSessionId id) => ConsoleState.Start.StopFollowing().Choose(id).ToggleFold(2).Select(Selection.Of(new[] { 1L }, 1));
            ConsoleState onFile = Folded(file.Id);
            ConsoleState onEnded = Folded(ended.Id);

            Assert.That(onFile.Cleared(file), Is.SameAs(onFile), "A file's session stays until it is closed, with all it had.");
            Assert.That(onEnded.Cleared(ended), Is.SameAs(onEnded), "An ended session is let go, as the sessions changing then says.");
            Assert.That(onFile.Cleared(live), Is.SameAs(onFile), "Another session than the one shown.");
            Assert.That(Folded(live.Id).Cleared(live).Folded, Is.EqualTo(ScopeSet.Empty));
            Assert.Throws<ArgumentNullException>(() => onFile.Cleared((LogConsoleSession)null!));
            Assert.Throws<ArgumentNullException>(() => onFile.Cleared((IReadOnlyList<LogConsoleSession>)null!));
        }

        [Test]
        public void States_made_alike_are_equal_so_a_window_draws_only_what_changed()
        {
            ConsoleState Made() => ConsoleState.Start.Type("tests.*").Choose(new LogSessionId(1)).ToggleFold(2).Select(Selection.Of(new[] { 5L, 3L }, 3));

            Assert.That(Made(), Is.EqualTo(Made()));
            Assert.That(Made().ToggleFold(8), Is.Not.EqualTo(Made()));
            Assert.That(Made().Select(Selection.Of(new[] { 5L, 3L }, 5)), Is.Not.EqualTo(Made()));
        }

        [Test]
        public void A_selection_is_its_rows_once_each_in_order_focused_on_one_of_them()
        {
            var rows = (Selection.Rows)Selection.Of(new[] { 9L, 3L, 9L, 5L }, 5);

            Assert.That(rows.Sequences, Is.EqualTo(new[] { 3L, 5L, 9L }));
            Assert.That(rows.Focus, Is.EqualTo(5));
            Assert.That(((Selection.Rows)Selection.Of(new[] { 3L, 9L }, 4)).Focus, Is.EqualTo(9), "A focus not among them is the last.");
            Assert.That(Selection.Of(Array.Empty<long>(), 1), Is.SameAs(Selection.None));
            Assert.That(Selection.Of(new[] { 3L, 5L }, 5), Is.EqualTo(Selection.Of(new[] { 5L, 3L }, 5)));
            Assert.That(Selection.Of(new[] { 3L, 5L }, 5).GetHashCode(), Is.EqualTo(Selection.Of(new[] { 5L, 3L }, 5).GetHashCode()));
            Assert.That(Selection.Of(new[] { 3L, 5L }, 5), Is.Not.EqualTo(Selection.Of(new[] { 3L, 5L }, 3)));
            Assert.Throws<ArgumentNullException>(() => Selection.Of(null!, 1));
        }

        [Test]
        public void The_rows_a_list_selects_are_a_selection_focused_on_the_last_and_read_back_as_the_view_has_them()
        {
            LogView view = ViewOf(3);

            Selection selection = Selection.Of(view, new[] { 2, 0, 7 });

            var rows = (Selection.Rows)selection;
            Assert.That(rows.Sequences, Is.EqualTo(new[] { view[0].Sequence, view[2].Sequence }), "An index the rows do not have is skipped.");
            Assert.That(rows.Focus, Is.EqualTo(view[0].Sequence), "The row selected last.");
            Assert.That(Selection.Of(view, new[] { 7, -1 }), Is.SameAs(Selection.None));

            SelectedRows selected = SelectedRows.Of(selection, view);
            Assert.That(selected.Indexes, Is.EqualTo(new[] { 0, 2 }));
            Assert.That(selected.Focus, Is.EqualTo(F.Some(0)));
            Assert.That(selected.FocusLast(), Is.EqualTo(new[] { 2, 0 }), "A list is told the focus last.");
            Assert.That(SelectedRows.Of(Selection.None, view), Is.SameAs(SelectedRows.Nothing));

            SelectedRows only = SelectedRows.Of(Selection.Of(new[] { view[1].Sequence }, view[1].Sequence), view);
            Assert.That(only.Indexes, Is.EqualTo(new[] { 1 }));
            Assert.That(only.FocusLast(), Is.EqualTo(new[] { 1 }), "The focus alone.");

            SelectedRows gone = SelectedRows.Of(Selection.Of(new[] { 999L }, 999), view);
            Assert.That((gone.Indexes.Count, gone.Focus.IsNone), Is.EqualTo((0, true)), "A row the view does not have is not among them.");
            Assert.Throws<ArgumentNullException>(() => Selection.Of(null!, new[] { 0 }));
            Assert.Throws<ArgumentNullException>(() => SelectedRows.Of(null!, view));
        }

        [Test]
        public void Records_of_one_repeat_selected_are_its_row_once()
        {
            // Records 1 to 3 are one repeat, then 4 and 5 a row each.
            LogStore store = Stores.Everything();
            store.Log.Saved(3, "A.sav");
            store.Log.Saved(3, "A.sav");
            store.Log.Saved(3, "A.sav");
            store.Log.Saved(4, "A.sav");
            store.Log.Saved(5, "A.sav");
            var view = new LogView(store, LogFilter.All);
            view.Update();
            Assert.That((view.Count, view[0].Last), Is.EqualTo((3, 3L)));

            SelectedRows selected = SelectedRows.Of(Selection.Of(new[] { 1L, 2L, 3L, 5L }, 2), view);

            Assert.That(selected.Indexes, Is.EqualTo(new[] { 0, 2 }), "In order, and the repeat once.");
            Assert.That(selected.Focus, Is.EqualTo(F.Some(0)), "A record a repeat spans focuses its row.");
            Assert.That(selected.FocusLast(), Is.EqualTo(new[] { 2, 0 }));
        }

        [Test]
        public void A_selection_is_equal_to_itself_at_once_and_says_what_it_focuses()
        {
            Selection rows = Selection.Of(new[] { 3L, 5L }, 3);

            Assert.That(rows.Equals(rows), Is.True);
            Assert.That(rows.Focused, Is.EqualTo(F.Some(3L)));
            Assert.That(Selection.None.Focused.IsNone, Is.True);
        }

        [Test]
        public void A_set_of_scopes_holds_each_once_in_order_and_is_a_value()
        {
            ScopeSet set = ScopeSet.Empty.With(7).With(3).With(7);

            Assert.That(set.Scopes, Is.EqualTo(new[] { 3L, 7L }));
            Assert.That((set.Contains(3), set.Contains(5)), Is.EqualTo((true, false)));
            Assert.That(set.With(3), Is.SameAs(set));
            Assert.That(set.Without(5), Is.SameAs(set));
            Assert.That(set.Without(3).Without(7), Is.EqualTo(ScopeSet.Empty));
            Assert.That(set, Is.EqualTo(ScopeSet.Empty.With(3).With(7)));
            Assert.That(set.GetHashCode(), Is.EqualTo(ScopeSet.Empty.With(3).With(7).GetHashCode()));
            Assert.That(set.ToString(), Is.EqualTo("3 7"));
        }

        [Test]
        public void A_line_written_from_a_filter_is_the_query_that_reads_back_as_it()
        {
            QueryLine written = QueryLine.Written(Parse("tests.*  level>=error"));

            Assert.That(written.Text, Is.EqualTo("level>=error tests.*"));
            Assert.That(QueryLine.Of(written.Text, LogFilter.All).Filter, Is.EqualTo(written.Filter));
            Assert.That(QueryLine.Written(LogFilter.All), Is.EqualTo(QueryLine.Empty));
        }

        [Test]
        public void A_saved_filter_pressed_shows_its_rows_and_pressed_again_what_was_shown_before()
        {
            SavedFilter errors = Saved("Errors", "level>=error");
            SavedFilter saves = Saved("Saves", "tests.save.*");
            ConsoleState typed = ConsoleState.Start.Type("tests.*");

            ConsoleState on = typed.PressSaved(errors);
            Assert.That((on.Query.Text, on.Filter), Is.EqualTo(("level>=error", errors.Filter)));
            Assert.That(on.PressSaved(errors).Query, Is.EqualTo(typed.Query), "Pressed again, the line is what it was.");

            ConsoleState other = on.PressSaved(saves);
            Assert.That(other.Query.Text, Is.EqualTo("tests.save.*"), "Another saved filter's press shows its rows in place.");
            Assert.That(other.PressSaved(saves).Query, Is.EqualTo(typed.Query), "Back to the line before the first saved filter was pressed.");
            Assert.That(other.PressSaved(errors).PressSaved(errors).Query, Is.EqualTo(typed.Query));
        }

        [Test]
        public void A_line_typed_or_pressed_after_a_saved_filter_is_the_line_to_go_back_to()
        {
            SavedFilter errors = Saved("Errors", "level>=error");
            ConsoleState on = ConsoleState.Start.Type("tests.*").PressSaved(errors);

            Assert.That(on.Type("tests.heard").PressSaved(errors).PressSaved(errors).Query.Text, Is.EqualTo("tests.heard"));
            Assert.That(on.PressBadge(LogLevel.Warning).PressSaved(errors).PressSaved(errors).Query.Text, Is.EqualTo("level=warning"));
            Assert.That(on.FilterToScope(3).PressSaved(errors).PressSaved(errors).Query.Text, Is.EqualTo("level>=error scope:3"),
                "Every press that writes the line, a category's as a scope's, forgets the line before the saved filter.");
            Assert.That(ConsoleState.Start.Type("level>=error").PressSaved(errors).Query, Is.EqualTo(QueryLine.Empty),
                "A line that is a saved filter's is that filter on, and pressing it shows everything.");
        }

        [Test]
        public void A_filter_saved_from_the_line_is_on_while_the_rows_are_of_it()
        {
            ConsoleState[] lines =
            {
                ConsoleState.Start.Type("tests.save.* -\"Shader warning\"").PressBadge(LogLevel.Warning),
                ConsoleState.Start.Type("level>=warning tests.* -thread:2").FilterToScope(4),
                ConsoleState.Start.Type("frame:1..9 tests.save.*"),
            };

            foreach (ConsoleState line in lines)
                Assert.That(Saved("Line", line.Filter.ToString()).IsOn(line.Filter), Is.True, $"Save the Query as a Filter saves {line.Query.Text}, which is then on.");
        }

        [Test]
        public void An_error_to_show_stops_following_and_selects_its_row_once_the_rows_have_it()
        {
            LogView view = ViewOf(5);
            var id = new LogSessionId(1);
            ConsoleState shown = ConsoleState.Start.Choose(id).ToggleFold(2).Select(Selection.Of(new[] { view[1].Sequence }, view[1].Sequence));

            ConsoleState revealing = shown.Reveal(new UnseenError(id, view[3].Sequence));

            Assert.That((revealing.Session, revealing.Revealing, revealing.Following), Is.EqualTo((F.Some(id), F.Some(view[3].Sequence), false)),
                "Following would take the rows away from it.");
            Assert.That((revealing.Folded, revealing.Selection), Is.EqualTo((shown.Folded, shown.Selection)), "The session shown keeps what is folded and selected of it, until its row is found.");

            RevealStep.Found found = revealing.RevealIn(view).Match(
                static _ => throw new AssertionException("It waits."), static found => found, static _ => throw new AssertionException("It is hidden."),
                static _ => throw new AssertionException("It is gone."));
            Assert.That(found.Index, Is.EqualTo(3));
            Assert.That(found.Next.Selection.Focused, Is.EqualTo(F.Some(view[3].Sequence)));
            Assert.That(found.Next.Revealing.IsNone, Is.True, "It is brought into sight once.");
            Assert.That(found.Next.RevealIn(view), Is.SameAs(RevealStep.Waiting.Instance));
            Assert.That(ConsoleState.Start.RevealIn(view), Is.SameAs(RevealStep.Waiting.Instance), "Nothing to bring into sight.");
        }

        [Test]
        public void An_error_of_another_session_shows_that_session_afresh()
        {
            ConsoleState shown = ConsoleState.Start.Choose(new LogSessionId(1)).Select(Selection.Of(new[] { 2L }, 2L));

            ConsoleState revealing = shown.Reveal(new UnseenError(new LogSessionId(2), 4));

            Assert.That((revealing.Session, revealing.Selection, revealing.Revealing), Is.EqualTo((F.Some(new LogSessionId(2)), Selection.None, F.Some(4L))));
            Assert.Throws<ArgumentException>(() => shown.Reveal(new UnseenError(default, 4)));
            Assert.Throws<ArgumentOutOfRangeException>(() => shown.Reveal(new UnseenError(new LogSessionId(1), 0)));
        }

        [Test]
        public void An_error_no_row_holds_is_said_to_be_hidden_and_is_brought_into_sight_no_more()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(1, "A.sav");
            long step = store.Next;
            store.Log.Step(2);
            var view = new LogView(store, Parse("level>=information"));
            view.Update();
            var id = new LogSessionId(1);

            ConsoleState revealing = ConsoleState.Start.Choose(id).Reveal(new UnseenError(id, step));
            RevealStep.Hidden hidden = revealing.RevealIn(view).Match(
                static _ => throw new AssertionException("It waits."), static _ => throw new AssertionException("It is found."), static hidden => hidden,
                static _ => throw new AssertionException("It is gone."));

            Assert.That(hidden.Next.Revealing.IsNone, Is.True);
            Assert.That(hidden.Next.Selection, Is.SameAs(revealing.Selection), "What was selected stays.");
        }

        [Test]
        public void An_error_is_brought_into_sight_only_once_the_rows_were_scanned()
        {
            LogStore store = Stores.Everything();
            long saved = store.Next;
            store.Log.Saved(1, "A.sav");
            var view = new LogView(store, LogFilter.All);
            var id = new LogSessionId(1);
            ConsoleState revealing = ConsoleState.Start.Choose(id).Reveal(new UnseenError(id, saved));

            Assert.That(view.IsScanning, Is.True, "A view scans until it is first updated.");
            Assert.That(revealing.RevealIn(view), Is.SameAs(RevealStep.Waiting.Instance));

            view.Update();
            Assert.That(revealing.RevealIn(view), Is.InstanceOf<RevealStep.Found>());
        }

        [Test]
        public void An_error_let_go_or_of_a_session_let_go_is_said_to_be_gone_and_is_brought_into_sight_no_more()
        {
            LogStore store = Stores.Everything(Stores.Tiny);
            long oldest = store.Next;
            for (int slot = 0; slot < LogStore.MinRecords + 4; slot++)
                store.Log.Saved(slot + 1, "A.sav");
            var view = new LogView(store, LogFilter.All);
            view.Update();
            var id = new LogSessionId(1);
            ConsoleState revealing = ConsoleState.Start.Choose(id).Reveal(new UnseenError(id, oldest));

            Assert.That(store.First, Is.GreaterThan(oldest), "The store let its oldest records go.");
            RevealStep.Gone gone = revealing.RevealIn(view).Match(
                static _ => throw new AssertionException("It waits."), static _ => throw new AssertionException("It is found."),
                static _ => throw new AssertionException("It is hidden."), static gone => gone);
            Assert.That(gone.Next.Revealing.IsNone, Is.True);
            Assert.That(gone.Next.Selection, Is.SameAs(revealing.Selection), "What was selected stays.");
            Assert.That(revealing.RevealIn(F.None), Is.InstanceOf<RevealStep.Gone>(), "No rows while a session is to be shown: the process let it go.");
            Assert.That(ConsoleState.Start.RevealIn(F.None), Is.SameAs(RevealStep.Waiting.Instance), "Nothing to bring into sight.");
        }

        [Test]
        public void An_error_of_a_session_the_sessions_let_go_is_said_to_be_gone_before_another_is_chosen()
        {
            var sessions = new ConsoleSessions(live: true);
            sessions.End(Start(sessions), s_clock.Started);
            LogConsoleSession.Ended ended = (LogConsoleSession.Ended)sessions.Sessions[0];
            LogConsoleSession.File file = sessions.Open(FileOf("a.jsonl"));
            ConsoleState revealing = ConsoleState.Start.SessionsChanged(sessions.Sessions).Reveal(new UnseenError(file.Id, 1));
            Assert.That(revealing.Session, Is.EqualTo(F.Some(file.Id)));
            Assert.That(revealing.RevealAmong(sessions.Sessions), Is.SameAs(RevealStep.Waiting.Instance), "Its session is there, so the rows say.");

            sessions.Close(file);
            RevealStep.Gone gone = revealing.RevealAmong(sessions.Sessions).Match(
                static _ => throw new AssertionException("It waits."), static _ => throw new AssertionException("It is found."),
                static _ => throw new AssertionException("It is hidden."), static gone => gone);
            Assert.That(gone.Next.Revealing.IsNone, Is.True);
            Assert.That(gone.Next.Session, Is.EqualTo(F.Some(file.Id)), "The sessions choose another after.");
            Assert.That(gone.Next.SessionsChanged(sessions.Sessions).Session, Is.EqualTo(F.Some(ended.Id)));

            ConsoleState chosen = revealing.SessionsChanged(sessions.Sessions);
            Assert.That((chosen.Session, chosen.Revealing.IsNone), Is.EqualTo((F.Some(ended.Id), true)),
                "Choosing another ends the reveal saying nothing, which is why it is asked first.");
            Assert.That(ConsoleState.Start.RevealAmong(sessions.Sessions), Is.SameAs(RevealStep.Waiting.Instance), "Nothing to bring into sight.");
            Assert.Throws<ArgumentNullException>(() => revealing.RevealAmong(null!));
        }

        [Test]
        public void Following_selecting_or_a_clear_ends_bringing_an_error_into_sight()
        {
            var sessions = new ConsoleSessions(live: true);
            LogConsoleSession.Live live = Start(sessions);
            ConsoleState revealing = ConsoleState.Start.Choose(live.Id).Reveal(new UnseenError(live.Id, 4));

            ConsoleState followed = revealing.Follow();
            Assert.That((followed.Revealing.IsNone, followed.Following), Is.EqualTo((true, true)));
            Assert.That(revealing.Select(Selection.Of(new[] { 2L }, 2L)).Revealing.IsNone, Is.True, "The developer chose another row.");
            Assert.That(revealing.Cleared(live).Revealing.IsNone, Is.True, "The rows it was in are gone.");
            Assert.That(revealing.Cleared(sessions.Sessions), Is.EqualTo(revealing.Cleared(live)), "Of the sessions before a clear, the one shown is the one cleared.");
            Assert.That(revealing.Cleared(Array.Empty<LogConsoleSession>()), Is.SameAs(revealing), "A clear of sessions it does not show changes nothing.");
            Assert.That(ConsoleState.Start.Cleared(sessions.Sessions), Is.SameAs(ConsoleState.Start));
            Assert.That(revealing.StopFollowing(), Is.SameAs(revealing));
            Assert.That(ConsoleState.Start.Follow(), Is.SameAs(ConsoleState.Start));
        }

        /// <summary>A view of <paramref name="records"/> records, a row each.</summary>
        private static LogView ViewOf(int records)
        {
            LogStore store = Stores.Everything();
            for (int slot = 1; slot <= records; slot++)
                store.Log.Saved(slot, "A.sav");

            var view = new LogView(store, LogFilter.All);
            view.Update();
            return view;
        }

        private static LogConsoleSession.Live Start(ConsoleSessions sessions) =>
            sessions.Start("Tests", s_clock, 100, LogRedaction.None).Match(() => throw new InvalidOperationException("The sessions are live."), live => live);

        /// <summary>A file of one record, named <paramref name="name"/>.</summary>
        private static LogSessionFile FileOf(string name)
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(1, "A.sav");
            var view = new LogView(store, LogFilter.All);
            view.Update();
            var stream = new MemoryStream();
            using (var sink = new JsonLinesSink(stream, leaveOpen: true))
                view.Write(sink, 0, view.Count);

            return LogFile.Read(new MemoryStream(stream.ToArray()), name).Match(error => throw new InvalidOperationException(error.Message), file => file);
        }

        private static SavedFilter Saved(string name, string query) =>
            SavedFilter.Of(name, query, F.None).Match(error => throw new InvalidOperationException(error.Message), saved => saved);

        private static LogFilter Parse(string query) =>
            LogFilter.Parse(query).Match(error => throw new InvalidOperationException(error.Message), filter => filter);
    }
}
