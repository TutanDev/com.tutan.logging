using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Tutan.Functional;

namespace Tutan.Logging.Tests
{
    // Each test keeps its sessions in a list of its own, so a run never touches the process's, which
    // a developer may be reading in the console.
    public class LogConsoleTests
    {
        private static readonly LogSinkOptions s_debug = new LogSinkOptions(LogLevel.Debug);

        [Test]
        public void A_console_sink_keeps_what_its_drain_hands_it_whole_in_a_live_session()
        {
            var sessions = new ConsoleSessions(live: true);
            LogStore store = Stores.Everything();
            using var drain = new LogDrain(store, (new LogConsoleSink(100, "console", sessions), s_debug));

            store.Log.Step(1);
            using (LogScope saving = store.Log.Saving(2))
                saving.Log.Wrote(2);
            store.Log.Saved(3, "C.sav");
            drain.Pump();

            var live = (LogConsoleSession.Live)sessions.Sessions.Single();
            Assert.That(live.Id.Value, Is.EqualTo(1));
            Assert.That((live.Records.Program, live.Records.Clock, live.Records.Capacity), Is.EqualTo(("Tests", store.Clock, 100)));
            Assert.That(live.Records.Redacted, Is.EqualTo(LogRedaction.None));
            Assert.That(Kept(live.Records), Is.EqualTo(new[]
            {
                "ScopeBegin Saving slot 2", "Event Wrote 2 bytes", "ScopeEnd", "Event Saved slot 3 as C.sav",
            }), "Its level leaves the step out, and the scope's opening it was written is not kept twice.");
        }

        [Test]
        public void A_record_under_scopes_the_sink_was_not_written_is_kept_after_their_openings()
        {
            var sessions = new ConsoleSessions(live: true);
            LogStore store = Stores.Everything();
            using var drain = new LogDrain(store, (new LogConsoleSink(100, "console", sessions), new LogSinkOptions(LogLevel.Warning)));

            using (LogScope outer = store.Log.Saving(1))
            {
                outer.Log.Saved(1, "A.sav");
                using (LogScope inner = outer.Log.Saving(2))
                    inner.Log.Failed(2, new InvalidOperationException("The disk is full."));
            }

            drain.Pump();

            LogArchive records = sessions.Sessions.Single().Records;
            Assert.That(Kept(records), Is.EqualTo(new[] { "ScopeBegin Saving slot 1", "ScopeBegin Saving slot 2", "Event Saving slot 2 failed" }),
                "The openings are kept outermost first, and nothing else below the sink's level.");
            Assert.That(Backlogs(records), Is.EqualTo(new[] { false, false, false }), "An opening kept to name a scope is not an error's context.");
            Assert.That(records.LateMark.Placed, Is.EqualTo(0), "The openings come before the record, in order.");

            var view = new LogView(records, LogFilter.All);
            view.Update();
            Assert.That(Enumerable.Range(0, view.Count).Select(i => (view[i].Kind, view[i].Depth)), Is.EqualTo(new[]
            {
                (LogRowKind.Scope, 0), (LogRowKind.Scope, 1), (LogRowKind.Event, 2),
            }));
            Assert.That(view.Scopes.IsOpen(1), Is.True, "Its closing is below the sink's level, so the session has none.");
        }

        [Test]
        public void A_record_written_as_an_errors_context_is_kept_as_a_backlog()
        {
            var sessions = new ConsoleSessions(live: true);
            LogStore store = Stores.Everything();
            using var drain = new LogDrain(store, (new LogConsoleSink(100, "console", sessions), new LogSinkOptions(LogLevel.Warning, Backlog: 2)));

            store.Log.Step(1);
            store.Log.Step(2);
            store.Log.Step(3);
            store.Log.Failed(1, new InvalidOperationException("The disk is full."));
            drain.Pump();

            LogArchive records = sessions.Sessions.Single().Records;
            Assert.That(Kept(records), Is.EqualTo(new[] { "Event Step 2", "Event Step 3", "Event Saving slot 1 failed" }));
            Assert.That(Backlogs(records), Is.EqualTo(new[] { true, true, false }));
        }

        [Test]
        public void Past_its_cap_a_session_lets_its_oldest_go_and_counts_them()
        {
            var sessions = new ConsoleSessions(live: true);
            LogStore store = Stores.Everything();
            using var drain = new LogDrain(store, (new LogConsoleSink(8, "console", sessions), s_debug));

            for (int i = 1; i <= 20; i++)
                store.Log.Wrote(i);
            drain.Pump();

            LogArchive records = sessions.Sessions.Single().Records;
            Assert.That(records.Count, Is.LessThanOrEqualTo(8));
            Assert.That(records.Count + records.Dropped, Is.EqualTo(20));
            Assert.That(Kept(records).Last(), Is.EqualTo("Event Wrote 20 bytes"), "The newest are the ones it holds.");
            Assert.That(records.Read(1, new LogBatch()).Lost, Is.EqualTo(0), "What it let go is dropped, not lost.");
        }

        [Test]
        public void Records_the_store_overwrote_before_the_drain_read_them_are_lost_before_the_next_kept()
        {
            var sessions = new ConsoleSessions(live: true);
            LogStore store = Stores.Everything(Stores.Tiny);
            using var drain = new LogDrain(store, (new LogConsoleSink(100, "console", sessions), s_debug));

            for (int i = 1; i <= 40; i++)
                store.Log.Wrote(i);
            drain.Pump();

            LogArchive records = sessions.Sessions.Single().Records;
            Assert.That(records.Read(records.First, new LogBatch()), Is.EqualTo(new LogRead(25, 16, 41, 24)));
        }

        [Test]
        public void When_its_drain_lets_the_sink_go_the_session_has_ended_and_keeps_its_records()
        {
            var sessions = new ConsoleSessions(live: true);
            LogStore store = Stores.Everything();
            var sink = new LogConsoleSink(100, "console", sessions);
            var drain = new LogDrain(store, (sink, s_debug));
            store.Log.Saved(1, "A.sav");
            store.Log.Saved(2, "B.sav");
            LogConsoleSession live = sessions.Sessions.Single();

            DateTimeOffset before = DateTimeOffset.Now;
            drain.Dispose();
            DateTimeOffset after = DateTimeOffset.Now;
            sink.Dispose();

            var ended = (LogConsoleSession.Ended)sessions.Sessions.Single();
            Assert.That(ended.Id, Is.EqualTo(live.Id));
            Assert.That(ended.Records, Is.SameAs(live.Records));
            Assert.That(ended.At, Is.InRange(before, after), "It ended as the drain let it go, and disposing the sink again changed nothing.");
            Assert.That(Kept(ended.Records), Is.EqualTo(new[] { "Event Saved slot 1 as A.sav", "Event Saved slot 2 as B.sav" }),
                "What the drain had not handed it yet, it was handed as the drain was disposed.");
            Assert.That(ended.Records.Capacity, Is.EqualTo(2), "An ended session takes nothing after, so it holds what it holds.");
            Assert.That(ended.Records.CanKeep(3), Is.False);
        }

        [Test]
        public void Each_list_of_sessions_stays_as_it_was_and_the_sessions_are_numbered_as_they_start()
        {
            var sessions = new ConsoleSessions(live: true);
            IReadOnlyList<LogConsoleSession> none = sessions.Sessions;
            var first = new LogDrain(Stores.Everything(), (new LogConsoleSink(100, "first", sessions), s_debug));
            using var second = new LogDrain(Stores.Everything(), (new LogConsoleSink(100, "second", sessions), s_debug));
            IReadOnlyList<LogConsoleSession> both = sessions.Sessions;

            first.Dispose();

            Assert.That(none, Is.Empty);
            Assert.That(both.Select(s => (s.Id.Value, State(s))), Is.EqualTo(new[] { (1L, "live"), (2L, "live") }),
                "A list a reader holds does not change under it.");
            Assert.That(sessions.Sessions.Select(s => (s.Id.Value, State(s))), Is.EqualTo(new[] { (1L, "ended"), (2L, "live") }));
            Assert.That(sessions.Find(both[1].Id), Is.EqualTo(F.Some(both[1])));
            Assert.That(sessions.Find(default).IsNone, Is.True, "The default id is no session.");
        }

        [Test]
        public void Clearing_lets_ended_sessions_go_empties_live_ones_and_leaves_files()
        {
            var sessions = new ConsoleSessions(live: true);
            LogStore ending = Stores.Everything();
            using (new LogDrain(ending, (new LogConsoleSink(100, "ending", sessions), s_debug)))
                ending.Log.Saved(1, "A.sav");
            LogStore store = Stores.Everything();
            using var drain = new LogDrain(store, (new LogConsoleSink(4, "live", sessions), s_debug));
            for (int i = 1; i <= 6; i++)
                store.Log.Wrote(i);
            store.Log.Saved(7, "G.sav");
            drain.Pump();
            LogConsoleSession.File file = sessions.Open(FileOf(log => log.Saved(1, "A.sav")));

            sessions.Clear();

            Assert.That(sessions.Sessions.Select(s => s.Id.Value), Is.EqualTo(new[] { 2L, 3L }));
            LogArchive records = sessions.Sessions[0].Records;
            Assert.That((records.Count, records.Dropped, records.First, records.Next), Is.EqualTo((0, 0L, 8L, 8L)),
                "It stops where it was, and what it dropped was before the clear.");
            Assert.That(Kept(file.Records), Is.EqualTo(new[] { "Event Saved slot 1 as A.sav" }));

            store.Log.Saved(8, "H.sav");
            drain.Pump();
            Assert.That(Kept(records), Is.EqualTo(new[] { "Event Saved slot 8 as H.sav" }), "It keeps what it is handed after.");
        }

        [Test]
        public void A_view_of_a_live_session_drops_its_rows_when_the_session_is_cleared()
        {
            var sessions = new ConsoleSessions(live: true);
            LogStore store = Stores.Everything();
            using var drain = new LogDrain(store, (new LogConsoleSink(100, "console", sessions), s_debug));
            store.Log.Saved(1, "A.sav");
            store.Log.Saved(2, "B.sav");
            drain.Pump();
            var view = new LogView(sessions.Sessions.Single().Records, LogFilter.All);
            view.Update();
            Assert.That(view.Count, Is.EqualTo(2));

            sessions.Clear();
            view.Update();
            Assert.That((view.Count, view.Counts.Total), Is.EqualTo((0, 0L)));

            store.Log.Saved(3, "C.sav");
            drain.Pump();
            view.Update();
            Assert.That((view.Count, view.Counts.Total), Is.EqualTo((1, 1L)));
        }

        [Test]
        public void After_a_clear_a_record_under_a_scope_opened_before_it_is_kept_without_the_opening()
        {
            var sessions = new ConsoleSessions(live: true);
            LogStore store = Stores.Everything();
            using var drain = new LogDrain(store, (new LogConsoleSink(100, "console", sessions), s_debug));
            using LogScope saving = store.Log.Saving(1);
            drain.Pump();

            sessions.Clear();
            saving.Log.Wrote(1);
            drain.Pump();

            Assert.That(Kept(sessions.Sessions.Single().Records), Is.EqualTo(new[] { "Event Wrote 1 bytes" }),
                "The opening was let go with the rest, as a view's Clear hides it.");
        }

        [Test]
        public void Opening_a_file_adds_its_session_once_and_closing_lets_it_go()
        {
            var sessions = new ConsoleSessions(live: true);
            LogSessionFile read = FileOf(log => log.Saved(1, "A.sav"));

            LogConsoleSession.File file = sessions.Open(read);
            LogConsoleSession.File again = sessions.Open(read);

            Assert.That(again, Is.EqualTo(file));
            Assert.That(sessions.Sessions, Is.EqualTo(new[] { file }));
            Assert.That((file.Path, file.Records, file.Skipped), Is.EqualTo((read.Path, read.Archive, read.Skipped)));

            sessions.Close(file);
            sessions.Close(file);
            Assert.That(sessions.Sessions, Is.Empty, "Closing what is gone does nothing.");
            Assert.That(sessions.Open(FileOf(log => log.Saved(1, "A.sav"))).Id.Value, Is.EqualTo(2), "Another read of a file is another session.");
        }

        [Test]
        public void Closing_an_ended_session_lets_it_go_and_leaves_the_others()
        {
            var sessions = new ConsoleSessions(live: true);
            var ending = new LogDrain(Stores.Everything(), (new LogConsoleSink(100, "ending", sessions), s_debug));
            using var drain = new LogDrain(Stores.Everything(), (new LogConsoleSink(100, "live", sessions), s_debug));
            ending.Dispose();
            LogConsoleSession live = sessions.Sessions[1];

            sessions.Close((LogConsoleSession.Ended)sessions.Sessions[0]);

            Assert.That(sessions.Sessions, Is.EqualTo(new[] { live }));
            Assert.That(live, Is.InstanceOf<LogConsoleSession.Live>(), "A live session ends when its drain lets its sink go, and is closed only then.");
        }

        [Test]
        public void A_console_sink_that_throws_ends_its_session_and_the_drain_says_why()
        {
            var sessions = new ConsoleSessions(live: true);
            LogStore store = Stores.Everything();
            var memory = new MemorySink();
            using var drain = new LogDrain(store, (new LogConsoleSink(100, "console", sessions), s_debug), (memory, new LogSinkOptions(LogLevel.Trace)));
            store.Log.Saved(1, "A.sav");
            drain.Pump();
            // The one way to make it throw from outside: its records take nothing more.
            ((LogConsoleSession.Live)sessions.Sessions.Single()).Records.Seal();

            store.Log.Saved(2, "B.sav");
            drain.Pump();
            // The drain says the sink failed in the store, which the next pump reads.
            drain.Pump();

            var ended = (LogConsoleSession.Ended)sessions.Sessions.Single();
            Assert.That(Kept(ended.Records), Is.EqualTo(new[] { "Event Saved slot 1 as A.sav" }));
            Assert.That(memory.Records.Of(SelfLog.Events.SinkFailed).Single().Field("Exception"), Is.InstanceOf<InvalidOperationException>());
        }

        [Test]
        public void A_session_of_a_cap_of_one_holds_only_the_newest()
        {
            var sessions = new ConsoleSessions(live: true);
            LogStore store = Stores.Everything();
            using var drain = new LogDrain(store, (new LogConsoleSink(1, "console", sessions), s_debug));

            store.Log.Saved(1, "A.sav");
            store.Log.Saved(2, "B.sav");
            store.Log.Saved(3, "C.sav");
            drain.Pump();

            LogArchive records = sessions.Sessions.Single().Records;
            Assert.That(Kept(records), Is.EqualTo(new[] { "Event Saved slot 3 as C.sav" }));
            Assert.That((records.Count, records.Dropped), Is.EqualTo((1, 2L)));
        }

        [Test]
        public void Outside_the_editor_a_console_sink_keeps_nothing()
        {
            var sessions = new ConsoleSessions(live: false);
            LogStore store = Stores.Everything();
            var drain = new LogDrain(store, (new LogConsoleSink(100, "console", sessions), s_debug));

            store.Log.Saved(1, "A.sav");
            drain.Pump();
            drain.Dispose();

            Assert.That(sessions.Sessions, Is.Empty);
            Assert.That(sessions.Open(FileOf(log => log.Saved(1, "A.sav"))), Is.Not.Null, "A file's session is opened anywhere.");
        }

        [Test]
        public void A_console_sink_given_to_a_second_drain_fails_there_and_keeps_its_one_session()
        {
            var sessions = new ConsoleSessions(live: true);
            var sink = new LogConsoleSink(100, "console", sessions);
            LogStore store = Stores.Everything();
            using var first = new LogDrain(store, (sink, s_debug));
            var memory = new MemorySink();
            LogStore other = Stores.Everything();
            using var second = new LogDrain(other, (sink, s_debug), (memory, new LogSinkOptions(LogLevel.Trace)));

            other.Log.Saved(1, "A.sav");
            second.Pump();

            MemoryRecord failed = memory.Records.Of(SelfLog.Events.SinkFailed).Single();
            Assert.That(failed.Field("Exception"), Is.InstanceOf<InvalidOperationException>());
            LogConsoleSession session = sessions.Sessions.Single();
            Assert.That(Kept(session.Records), Is.Empty, "The second drain writes nothing to it.");
        }

        [Test]
        public void A_console_sink_whose_options_redact_fails_and_says_why()
        {
            var sessions = new ConsoleSessions(live: true);
            var memory = new MemorySink();
            LogStore store = Stores.Everything();
            using var drain = new LogDrain(store,
                (new LogConsoleSink(100, "console", sessions), new LogSinkOptions(LogLevel.Debug, Redact: LogRedaction.Personal)),
                (memory, new LogSinkOptions(LogLevel.Trace)));

            drain.Pump();

            MemoryRecord failed = memory.Records.Of(SelfLog.Events.SinkFailed).Single();
            Assert.That(failed.Field("Exception"), Is.InstanceOf<ArgumentException>());
            Assert.That(((Exception)failed.Field("Exception")!).Message, Does.Contain("keeps records whole"));
            Assert.That(sessions.Sessions, Is.Empty);
        }

        [Test]
        public void A_session_of_records_that_already_lack_fields_says_so()
        {
            var sessions = new ConsoleSessions(live: true);
            LogSessionFile file = FileOf(log => log.SignedIn("ann@example.com", "C:/Saves"), new LogSinkOptions(LogLevel.Trace, Redact: LogRedaction.Personal));
            var view = new LogView(file.Archive, LogFilter.All);
            view.Update();

            using var sink = new LogConsoleSink(100, "console", sessions);
            view.Write(sink, 0, view.Count);

            LogArchive records = sessions.Sessions.Single().Records;
            Assert.That(records.Redacted, Is.EqualTo(LogRedaction.Personal));
            Assert.That(Kept(records, "tests.people"), Is.EqualTo(new[] { "Event Signed in as [personal] from C:/Saves" }));
        }

        [Test]
        public void A_disposed_console_sink_is_not_opened()
        {
            var sessions = new ConsoleSessions(live: true);
            var sink = new LogConsoleSink(100, "console", sessions);
            sink.Dispose();
            var memory = new MemorySink();

            using var drain = new LogDrain(Stores.Everything(), (sink, s_debug), (memory, new LogSinkOptions(LogLevel.Trace)));
            drain.Pump();

            Assert.That(memory.Records.Of(SelfLog.Events.SinkFailed).Single().Field("Exception"), Is.InstanceOf<ObjectDisposedException>());
            Assert.That(sessions.Sessions, Is.Empty);
        }

        [Test]
        public void A_cap_is_at_least_one_record_and_one_million_unless_given()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new LogConsoleSink(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ConsoleSessions(live: false).Start("Tests", default, 0, LogRedaction.None),
                "A cap is refused where no session starts, too.");
            Assert.That(new LogConsoleSink().Cap, Is.EqualTo(1_000_000));
            Assert.That(LogConsoleSink.DefaultCap, Is.EqualTo(1_000_000));
        }

        private static string State(LogConsoleSession session) =>
            session.Match(static live => "live", static ended => "ended", static file => "file");

        /// <summary>
        /// Each record of <paramref name="category"/> the records hold, as its kind and, but for a
        /// closing, its message: what a test asserts on, whatever the store said of itself.
        /// </summary>
        private static string[] Kept(LogRecords records, string category = "tests.save") =>
            Of(records, category).Select(r => r.Kind == LogRecordKind.ScopeEnd ? "ScopeEnd" : $"{r.Kind} {r.Message}").ToArray();

        /// <summary>Whether each record of tests.save the records hold was kept as an error's context.</summary>
        private static bool[] Backlogs(LogRecords records) => Of(records, "tests.save").Select(r => r.Backlog).ToArray();

        private static IEnumerable<MemoryRecord> Of(LogRecords records, string category) =>
            Held.Records(records).Where(r => r.Event.Category == category);

        /// <summary>The file a JSON lines sink writes of what <paramref name="write"/> logs, read back.</summary>
        private static LogSessionFile FileOf(Action<Log> write, LogSinkOptions? options = null)
        {
            var stream = new MemoryStream();
            LogStore store = Stores.Everything();
            using (new LogDrain(store, (new JsonLinesSink(stream, leaveOpen: true), options ?? new LogSinkOptions(LogLevel.Trace))))
                write(store.Log);

            stream.Position = 0;
            return LogFile.Read(stream, "test.jsonl").Match(error => throw new InvalidOperationException(error.Message), file => file);
        }
    }
}
