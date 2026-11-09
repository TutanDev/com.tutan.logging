using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Tutan.Functional;
using Tutan.Logging.Editor;
using UnityEditor;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace Tutan.Logging.Tests
{
    // Each test keeps its sessions in a list of its own, so a run never touches the process's, which
    // a developer may be reading in the console, nor the chrome's count of them.
    public class ChromeTests
    {
        private static readonly InvalidOperationException s_full = new InvalidOperationException("The disk is full.");

        [Test]
        public void Errors_no_window_shows_are_unseen_and_the_first_is_in_the_oldest_session()
        {
            var sessions = new ConsoleSessions(live: true);
            using var first = new Run(sessions, "first");
            using var second = new Run(sessions, "second");
            second.Fail(1);
            first.Step(1);
            long oldest = first.Fail(2);
            first.Fail(3);

            ErrorsTicked ticked = ErrorsSeen.None.Tick(sessions.Sessions, ShownSessions.None);

            Assert.That(ticked.Unseen.Count, Is.EqualTo(3));
            Assert.That(ticked.Unseen.First, Is.EqualTo(F.Some(new UnseenError(sessions.Sessions[0].Id, oldest))),
                "The first unseen is the first of the oldest session that has one, though the newer session's came first.");
            Assert.That(ticked.Arrived, Is.True);
            Assert.That(ticked.Next.Sessions.Select(static session => session.Session), Is.EqualTo(sessions.Sessions.Select(static session => session.Id)));
        }

        [Test]
        public void A_window_that_shows_a_session_sees_its_errors_and_those_after_are_unseen()
        {
            var sessions = new ConsoleSessions(live: true);
            using var run = new Run(sessions, "console");
            run.Fail(1);
            LogSessionId id = sessions.Sessions[0].Id;
            ShownSessions shown = ShownSessions.None.Showing(new WindowNumber(1), id);

            ErrorsTicked seen = ErrorsSeen.None.Tick(sessions.Sessions, shown);
            Assert.That((seen.Unseen, seen.Arrived), Is.EqualTo((UnseenErrors.None, true)), "An error on screen is seen, and it arrived all the same.");

            long after = run.Fail(2);
            ErrorsTicked hidden = seen.Next.Tick(sessions.Sessions, shown.Showing(new WindowNumber(1), F.None));
            Assert.That(hidden.Unseen, Is.EqualTo(UnseenErrors.Of(1, new UnseenError(id, after))), "Only the error after it was seen.");
            Assert.That(hidden.Arrived, Is.True);

            ErrorsTicked again = hidden.Next.Tick(sessions.Sessions, ShownSessions.None);
            Assert.That((again.Unseen.Count, again.Arrived), Is.EqualTo((1L, false)), "An error arrives once.");
            Assert.That(again.Next, Is.SameAs(hidden.Next), "Nothing changed, so it is where it stood.");

            run.Step(3);
            Assert.That(again.Next.Tick(sessions.Sessions, ShownSessions.None).Next, Is.SameAs(hidden.Next), "A record that is no error changes nothing.");
        }

        [Test]
        public void An_ended_session_keeps_its_unseen_errors_and_none_arrives_in_it_and_a_files_are_not_counted()
        {
            var sessions = new ConsoleSessions(live: true);
            var ending = new Run(sessions, "ending");
            long failed = ending.Fail(1);
            ending.Dispose();
            sessions.Open(FileOf("errors.jsonl"));

            ErrorsTicked ticked = ErrorsSeen.None.Tick(sessions.Sessions, ShownSessions.None);

            Assert.That(sessions.Sessions.Select(static session => session.GetType()), Is.EqualTo(new[] { typeof(LogConsoleSession.Ended), typeof(LogConsoleSession.File) }));
            Assert.That(ticked.Unseen, Is.EqualTo(UnseenErrors.Of(1, new UnseenError(sessions.Sessions[0].Id, failed))), "The file's error arrived in no run the console watched.");
            Assert.That(ticked.Arrived, Is.False, "An error of an ended session does not pause a game that plays now.");
            Assert.That(ticked.Next.Sessions.Count, Is.EqualTo(1));
        }

        [Test]
        public void A_clear_lets_the_errors_go_and_those_after_are_unseen_again()
        {
            var sessions = new ConsoleSessions(live: true);
            var ending = new Run(sessions, "ending");
            ending.Fail(1);
            ending.Dispose();
            using var run = new Run(sessions, "live");
            run.Fail(1);
            ErrorsTicked before = ErrorsSeen.None.Tick(sessions.Sessions, ShownSessions.None);
            Assert.That(before.Unseen.Count, Is.EqualTo(2));

            sessions.Clear();
            ErrorsTicked cleared = before.Next.Tick(sessions.Sessions, ShownSessions.None);
            Assert.That((cleared.Unseen, cleared.Arrived), Is.EqualTo((UnseenErrors.None, false)));
            Assert.That(cleared.Next.Sessions, Is.EqualTo(new[] { before.Next.Sessions[1] }),
                "The ended session is let go, and the live one stands where it stood.");

            long after = run.Fail(2);
            ErrorsTicked ticked = cleared.Next.Tick(sessions.Sessions, ShownSessions.None);
            Assert.That(ticked.Unseen, Is.EqualTo(UnseenErrors.Of(1, new UnseenError(sessions.Sessions[0].Id, after))));
            Assert.That(ticked.Arrived, Is.True);
        }

        [Test]
        public void A_tick_with_no_new_error_allocates_nothing()
        {
            var sessions = new ConsoleSessions(live: true);
            using var first = new Run(sessions, "first");
            using var second = new Run(sessions, "second");
            first.Fail(1);
            second.Fail(1);
            IReadOnlyList<LogConsoleSession> list = sessions.Sessions;
            ShownSessions shown = ShownSessions.None.Showing(new WindowNumber(1), list[1].Id);
            ErrorsSeen seen = ErrorsSeen.None.Tick(list, shown).Next.Tick(list, shown).Next;

            second.Step(2);
            void Tick() => _ = seen.Tick(list, shown);
            Tick();
            Assert.That(Tick, Is.Not.AllocatingGCMemory(), "One session unseen, one shown, and a record that is no error.");
            Assert.That(seen.Tick(list, shown).Next, Is.SameAs(seen));
        }

        [Test]
        public void What_a_session_s_errors_say_moves_only_as_an_error_arrives_or_is_seen()
        {
            var id = new LogSessionId(2);
            var errors = new SessionErrors(id, UnseenFrom: 5, ArrivedThrough: 7);
            var held = new LogErrorsHeld(2, 6, 9, 12);

            Assert.That(errors.Read(held, live: true, shown: false), Is.EqualTo(new SessionRead(new SessionErrors(id, 5, 9), true, UnseenErrors.Of(2, new UnseenError(id, 6)))));
            Assert.That(errors.Read(held, live: false, shown: false).Arrived, Is.False, "Only a live session's error arrives.");
            Assert.That(errors.Read(held, live: true, shown: true), Is.EqualTo(new SessionRead(new SessionErrors(id, 12, 9), true, UnseenErrors.None)),
                "Shown, what it holds is seen, and what comes after is unseen.");
            Assert.That(errors.Read(new LogErrorsHeld(1, 7, 7, 12), live: true, shown: false).Arrived, Is.False, "It saw that one arrive already.");
            Assert.That(errors.Read(new LogErrorsHeld(12), live: true, shown: true).Now, Is.EqualTo(errors),
                "With no error held, a window showing it changes nothing.");
        }

        [Test]
        public void Each_visible_window_says_the_session_it_shows_and_one_hidden_says_none()
        {
            var two = new LogSessionId(2);
            var three = new LogSessionId(3);
            ShownSessions none = ShownSessions.None;
            ShownSessions one = none.Showing(new WindowNumber(1), two);

            Assert.That(none.Showing(new WindowNumber(1), F.None), Is.SameAs(none));
            Assert.That((one.Shows(two), one.Shows(three)), Is.EqualTo((true, false)));
            Assert.That(one.Showing(new WindowNumber(1), two), Is.SameAs(one), "As it was, it is this one.");

            ShownSessions moved = one.Showing(new WindowNumber(1), three);
            Assert.That((moved.Shows(two), moved.Shows(three)), Is.EqualTo((false, true)), "A window shows one session.");

            ShownSessions both = moved.Showing(new WindowNumber(2), three);
            Assert.That(both.Showing(new WindowNumber(1), F.None).Shows(three), Is.True, "The other window still shows it.");
            Assert.That(both.Showing(new WindowNumber(1), F.None).Showing(new WindowNumber(2), F.None), Is.EqualTo(none));
            Assert.That(none.Showing(new WindowNumber(1), two), Is.EqualTo(one), "Alike, they are equal.");
        }

        [TestCase(true, true, true, false, ExpectedResult = true)]
        [TestCase(false, true, true, false, ExpectedResult = false)]
        [TestCase(true, false, true, false, ExpectedResult = false)]
        [TestCase(true, true, false, false, ExpectedResult = false)]
        [TestCase(true, true, true, true, ExpectedResult = false)]
        public bool The_game_pauses_for_an_error_that_arrived_while_it_plays_when_Pause_on_Error_is_set(bool arrived, bool pause, bool playing, bool paused) =>
            ChromeRules.Pauses(arrived, PersonalSettings.Default with { PauseOnError = pause }, playing, paused);

        [TestCase(PlayModeStateChange.ExitingEditMode, true, ExpectedResult = true)]
        [TestCase(PlayModeStateChange.ExitingEditMode, false, ExpectedResult = false)]
        [TestCase(PlayModeStateChange.EnteredPlayMode, true, ExpectedResult = false)]
        [TestCase(PlayModeStateChange.ExitingPlayMode, true, ExpectedResult = false)]
        [TestCase(PlayModeStateChange.EnteredEditMode, true, ExpectedResult = false)]
        public bool The_console_clears_as_Play_Mode_is_asked_for_when_Clear_on_Play_is_set(PlayModeStateChange change, bool clear) =>
            ChromeRules.Clears(change, PersonalSettings.Default with { ClearOnPlay = clear });

        [Test]
        public void The_count_unseen_is_said_on_the_badge_and_in_the_title_below_Unity_6_3()
        {
            Assert.That(UnseenText.Title(0), Is.EqualTo("Log Console"));
            Assert.That(UnseenText.Title(3), Is.EqualTo("Log Console (3)"));
            Assert.That(UnseenText.Title(12_345), Is.EqualTo($"Log Console ({CountText.Compact(12_345)})"));
            Assert.That(UnseenText.Badge(12_345), Is.EqualTo(CountText.Compact(12_345)));
            Assert.That(UnseenText.BadgeTip(1), Is.EqualTo("An error the log console has not shown. Press to show it."));
            Assert.That(UnseenText.BadgeTip(12_345), Is.EqualTo("12,345 errors the log console has not shown. Press to show the first."));
            Assert.That(PersonalSettings.Default, Is.EqualTo(new PersonalSettings(ClearOnPlay: true, PauseOnError: false)), "As Unity's Console has it.");
        }

        /// <summary>A file of one error, named <paramref name="name"/>.</summary>
        private static LogSessionFile FileOf(string name)
        {
            LogStore store = Stores.Everything();
            store.Log.Failed(1, s_full);
            var view = new LogView(store, LogFilter.All);
            view.Update();
            var stream = new MemoryStream();
            using (var sink = new JsonLinesSink(stream, leaveOpen: true))
                view.Write(sink, 0, view.Count);

            return LogFile.Read(new MemoryStream(stream.ToArray()), name).Match(error => throw new InvalidOperationException(error.Message), file => file);
        }

        /// <summary>A run that a console sink keeps a session of, which it ends as it is disposed.</summary>
        private sealed class Run : IDisposable
        {
            private readonly LogStore _store = Stores.Everything();
            private readonly LogDrain _drain;
            private bool _ended;

            public Run(ConsoleSessions sessions, string name) =>
                _drain = new LogDrain(_store, (new LogConsoleSink(100, name, sessions), new LogSinkOptions(LogLevel.Trace)));

            /// <summary>Logs an error and keeps it: its sequence.</summary>
            public long Fail(int slot)
            {
                long sequence = _store.Next;
                _store.Log.Failed(slot, s_full);
                _drain.Pump();
                return sequence;
            }

            /// <summary>Logs a record that is no error, and keeps it.</summary>
            public void Step(int step)
            {
                _store.Log.Step(step);
                _drain.Pump();
            }

            public void Dispose()
            {
                if (_ended)
                    return;

                _ended = true;
                _drain.Dispose();
            }
        }
    }
}
