using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Tutan.Functional;

namespace Tutan.Logging.Tests
{
    public class LogViewTests
    {
        [Test]
        public void A_view_shows_what_its_filter_keeps_once_it_updates()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(3, "A.sav");
            store.Log.Heard(LogLevel.Warning, "the disk is nearly full");
            store.Log.Wrote(4);
            store.Log.Failed(3, new IOException("No room."));
            var view = new LogView(store, Parse("level>=warning"));

            Assert.That(view.Count, Is.EqualTo(0));
            Assert.That(view.IsScanning, Is.True);
            view.Update();

            Assert.That(view.IsScanning, Is.False);
            Assert.That(Messages(view), Is.EqualTo(new[] { "Heard the disk is nearly full", "Saving slot 3 failed" }));
            Assert.That(view[0], Is.EqualTo(new LogRow(2, 2, 1, 0, LogLevel.Warning, LogRowKind.Event, 0, 0)));
            Assert.Throws<ArgumentOutOfRangeException>(() => _ = view[2]);
        }

        [Test]
        public void A_view_takes_what_arrives_at_each_update()
        {
            LogStore store = Stores.Everything();
            var view = new LogView(store, LogFilter.All);
            view.Update();

            store.Log.Saved(3, "A.sav");
            Assert.That(view.Count, Is.EqualTo(0));
            view.Update();
            store.Log.Saved(1, "B.dat");
            view.Update();

            Assert.That(Messages(view), Is.EqualTo(new[] { "Saved slot 3 as A.sav", "Saved slot 1 as B.dat" }));
        }

        [Test]
        public void A_repeat_is_one_row_that_counts_its_records()
        {
            LogStore store = Stores.Everything();
            var view = new LogView(store, LogFilter.All);
            store.Log.Saved(3, "A.sav");
            store.Log.Saved(3, "A.sav");
            view.Update();
            store.Log.Saved(3, "A.sav");
            store.Log.Saved(4, "A.sav");
            store.Log.Saved(3, "A.sav");
            view.Update();

            Assert.That(Rows(view), Is.EqualTo(new[]
            {
                new LogRow(1, 3, 3, 0, LogLevel.Information, LogRowKind.Event, 0, 0),
                new LogRow(4, 4, 1, 0, LogLevel.Information, LogRowKind.Event, 0, 0),
                new LogRow(5, 5, 1, 0, LogLevel.Information, LogRowKind.Event, 0, 0),
            }));
        }

        [Test]
        public void A_repeat_spans_records_the_filter_hides()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(3, "A.sav");
            store.Log.Wrote(4);
            store.Log.Saved(3, new string("A.sav".ToCharArray()));
            var view = new LogView(store, Parse("tests.save/Saved"));
            view.Update();

            LogRow row = Rows(view).Single();
            Assert.That((row.Sequence, row.Last, row.Records), Is.EqualTo((1L, 3L, 2)));
        }

        [Test]
        public void A_repeat_adds_up_what_a_limit_suppressed()
        {
            var clock = new TestClock();
            LogStore store = Stores.Everything(clock: clock);
            for (int i = 0; i < 5; i++)
                store.Log.Hit(1);
            clock.Advance(TimeSpan.FromSeconds(1));
            store.Log.Hit(1);
            var view = new LogView(store, LogFilter.All);
            view.Update();

            LogRow row = Rows(view).Single();
            Assert.That((row.Records, row.Suppressed), Is.EqualTo((3, 3L)));
        }

        [Test]
        public void A_scopes_records_are_not_repeats()
        {
            LogStore store = Stores.Everything();
            for (int i = 0; i < 2; i++)
            {
                using (store.Log.Saving(1))
                {
                }
            }

            var view = new LogView(store, LogFilter.All);
            view.Update();

            Assert.That(Rows(view).Select(r => r.Sequence), Is.EqualTo(new[] { 1L, 3L }));
            Assert.That(Rows(view).Select(r => r.Kind), Is.All.EqualTo(LogRowKind.Scope));
        }

        [Test]
        public void A_loss_is_said_on_the_row_after_it_and_a_repeat_does_not_span_it()
        {
            LogStore store = Stores.Everything(Stores.Tiny);
            var view = new LogView(store, LogFilter.All);
            store.Log.Saved(3, "A.sav");
            view.Update();

            for (int i = 0; i < 20; i++)
                store.Log.Saved(3, "A.sav");
            view.Update();

            Assert.That(Rows(view).Single(), Is.EqualTo(new LogRow(6, 21, 16, 0, LogLevel.Information, LogRowKind.Event, 0, 4)));
            Assert.That(view.Lost, Is.EqualTo(4));
        }

        [Test]
        public void The_rows_of_records_the_store_overwrote_are_dropped()
        {
            LogStore store = Stores.Everything(Stores.Tiny);
            var view = new LogView(store, LogFilter.All);
            for (int i = 0; i < 400; i++)
            {
                store.Log.Step(i);
                view.Update();
            }

            Assert.That(view.Count, Is.EqualTo(16));
            Assert.That(view[0].Sequence, Is.EqualTo(store.First));
            Assert.That(view[15].Sequence, Is.EqualTo(store.Next - 1));
            Assert.That(view.Lost, Is.EqualTo(0));
        }

        [Test]
        public void Counts_are_of_what_matches_but_for_the_level()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(3, "A.sav");
            store.Log.Wrote(1);
            store.Log.Wrote(2);
            store.Log.Failed(1, new IOException("No room."));
            store.Log.Failed(2, new IOException("No room."));
            store.Log.Said("not a save");
            var view = new LogView(store, Parse("level>=warning tests.save"));
            view.Update();

            Assert.That(view.Count, Is.EqualTo(2));
            Assert.That(view.Counts, Is.EqualTo(new LogLevelCounts(0, 2, 1, 0, 2, 0)));
            Assert.That(view.Counts[LogLevel.Debug], Is.EqualTo(2));
            Assert.That(view.Counts[LogLevel.None], Is.EqualTo(0));
            Assert.That(view.Counts.Total, Is.EqualTo(5));
        }

        [Test]
        public void Counts_drop_with_the_records_the_store_overwrites()
        {
            LogStore store = Stores.Everything(Stores.Tiny);
            var view = new LogView(store, Parse("level>=warning"));
            store.Log.Wrote(1);
            store.Log.Wrote(2);
            view.Update();
            Assert.That(view.Counts.Debug, Is.EqualTo(2));

            for (int i = 0; i < 100; i++)
            {
                store.Log.Step(i);
                if (i % 10 == 0)
                    view.Update();
            }

            view.Update();
            Assert.That(view.Counts, Is.EqualTo(new LogLevelCounts(16, 0, 0, 0, 0, 0)));
            Assert.That(view.Count, Is.EqualTo(0));
        }

        [Test]
        public void A_new_filter_is_scanned_over_updates_while_the_rows_stay_as_they_were()
        {
            LogStore store = Stores.Everything();
            for (int i = 0; i < 100; i++)
                store.Log.Step(i);
            var view = new LogView(store, LogFilter.All, budget: 16);

            int updates = 0;
            for (; view.IsScanning; updates++)
            {
                Assert.That(view.Count, Is.EqualTo(0));
                view.Update();
            }

            Assert.That(updates, Is.EqualTo(7));
            Assert.That(view.Count, Is.EqualTo(100));

            view.Filter = Parse("Step:1*");
            view.Update();
            Assert.That(view.IsScanning, Is.True);
            Assert.That(view.Count, Is.EqualTo(100));
            while (view.IsScanning)
                view.Update();

            Assert.That(view.Count, Is.EqualTo(11));
            Assert.That(view.Filter.ToString(), Is.EqualTo("Step:1*"));
        }

        [Test]
        public void While_a_new_filter_is_scanned_the_rows_are_written_as_they_were_made()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(3, "A.sav");
            store.Log.Wrote(1);
            store.Log.Saved(3, "A.sav");
            store.Log.Saved(3, "A.sav");
            store.Log.Failed(1, new IOException("No room."));
            LogFilter withoutWrites = Parse("-tests.save/Wrote");
            var view = new LogView(store, withoutWrites, budget: 2);
            while (view.IsScanning)
                view.Update();

            view.Filter = LogFilter.All;
            view.Update();

            Assert.That(view.IsScanning, Is.True);
            Assert.That(view.Filter, Is.EqualTo(LogFilter.All));
            Assert.That(view.Shown, Is.EqualTo(withoutWrites));
            var shown = new MemorySink();
            view.Write(shown, 0, view.Count);
            Assert.That(shown.Records.Select(r => r.Sequence), Is.EqualTo(new[] { 1L, 3L, 4L, 5L }));

            while (view.IsScanning)
                view.Update();

            Assert.That(view.Shown, Is.EqualTo(LogFilter.All));
            var all = new MemorySink();
            view.Write(all, 0, view.Count);
            Assert.That(all.Records.Select(r => r.Sequence), Is.EqualTo(new[] { 1L, 2L, 3L, 4L, 5L }));
        }

        [Test]
        public void A_filter_equal_to_the_one_it_has_does_not_scan()
        {
            LogStore store = Stores.Everything();
            var view = new LogView(store, Parse("level>=warning"));
            view.Update();

            view.Filter = Parse("level>=warning level<=critical");

            Assert.That(view.IsScanning, Is.False);
            Assert.That(view.GaveUp, Is.Empty);
        }

        [Test]
        public void A_regular_expression_that_took_too_long_is_said_and_a_retry_tries_it_afresh()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(1, new string('a', 40) + "!");
            store.Log.Saved(2, "aaa");
            var view = new LogView(store, Parse("File:/^(a+)+$/"));

            view.Update();

            Assert.That(view.Count, Is.EqualTo(0), "Kept, it keeps nothing from the record it took too long over.");
            Assert.That(view.GaveUp, Is.EqualTo(new[] { new LogFilterTerm.FieldRegex("File", "^(a+)+$") }));

            view.Filter = Parse("File:/^(a+)+$/");
            Assert.That(view.IsScanning, Is.False, "An equal filter is the filter it has.");

            view.Retry();

            Assert.That(view.IsScanning, Is.True);
            while (view.IsScanning)
                view.Update();
            Assert.That(view.GaveUp, Has.Count.EqualTo(1), "The rows are the new scan's, which took too long again.");
        }

        [Test]
        public void A_retry_with_nothing_given_up_does_not_scan()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(2, "aaa");
            var view = new LogView(store, Parse("File:/^(a+)+$/"));
            view.Update();

            view.Retry();

            Assert.That(view.IsScanning, Is.False);
            Assert.That(view.Count, Is.EqualTo(1));
        }

        [Test]
        public void A_regular_expression_that_took_too_long_still_matches_what_came_before_when_the_rows_are_read_again()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(1, "aaa");
            store.Log.Saved(2, "aaa");
            store.Log.Saved(3, new string('a', 40) + "!");
            store.Log.Saved(4, "aaa");
            LogBatch records = All(store);
            LogArchive archive = Archive(store);
            archive.Keep(records[0], backlog: false);
            archive.Keep(records[2], backlog: false);
            archive.Keep(records[3], backlog: false);
            var view = new LogView(archive, Parse("File:/^(a+)+$/"));
            view.Update();
            Assert.That(Rows(view).Select(r => r.Sequence), Is.EqualTo(new[] { 1L }));

            // Placed before the record it took too long over, so the rows are read again from it.
            archive.Keep(records[1], backlog: true);
            view.Update();

            Assert.That(Rows(view).Select(r => r.Sequence), Is.EqualTo(new[] { 1L, 2L }), "It gave up from the record it took too long over, and no sooner.");
            Assert.That(view.GaveUp, Has.Count.EqualTo(1));
        }

        [Test]
        public void A_row_is_as_deep_as_the_scopes_with_rows_it_is_under()
        {
            LogStore store = Stores.Everything();
            Nested(store);

            Assert.That(Depths(store, LogFilter.All), Is.EqualTo(new[] { 0, 1, 1, 2 }));
            Assert.That(Depths(store, Parse("level>=information")), Is.EqualTo(new[] { 0, 1, 1 }));
            Assert.That(Depths(store, Parse("-\"slot 2\"")), Is.EqualTo(new[] { 0, 1, 1 }));
        }

        [Test]
        public void A_folded_scope_hides_what_is_under_it_and_keeps_its_own_row()
        {
            LogStore store = Stores.Everything();
            long outer = Nested(store);
            store.Log.Saved(1, "B.dat");
            var view = new LogView(store, LogFilter.All);
            view.Update();

            view.Fold(outer);
            view.Update();
            Assert.That(view.IsFolded(outer), Is.True);
            Assert.That(Messages(view), Is.EqualTo(new[] { "Saving slot 1", "Saved slot 1 as B.dat" }));
            Assert.That(view.Counts, Is.EqualTo(new LogLevelCounts(1, 0, 4, 0, 0, 0)));

            view.Unfold(outer);
            view.Update();
            Assert.That(view.IsFolded(outer), Is.False);
            Assert.That(view.Count, Is.EqualTo(5));
        }

        [Test]
        public void A_fold_is_the_rows_once_the_scan_for_it_catches_up()
        {
            LogStore store = Stores.Everything();
            long outer;
            using (LogScope saving = store.Log.Saving(1))
            {
                outer = saving.Id;
                saving.Log.Step(1);
                saving.Log.Step(2);
            }

            store.Log.Saved(1, "B.dat");
            var view = new LogView(store, LogFilter.All, budget: 2);
            while (view.IsScanning)
                view.Update();

            view.Fold(outer);
            view.Update();

            Assert.That(view.IsScanning, Is.True);
            Assert.That(view.IsFolded(outer), Is.False);
            Assert.That(view.Count, Is.EqualTo(4));
            while (view.IsScanning)
                view.Update();
            Assert.That(view.IsFolded(outer), Is.True);
            Assert.That(Messages(view), Is.EqualTo(new[] { "Saving slot 1", "Saved slot 1 as B.dat" }));
        }

        [Test]
        public void Only_a_scope_folds()
        {
            var view = new LogView(Stores.Everything(), LogFilter.All);
            view.Update();

            Assert.Throws<ArgumentOutOfRangeException>(() => view.Fold(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => view.Unfold(-1));
            Assert.That(view.IsScanning, Is.False);
        }

        [Test]
        public void Clearing_starts_the_view_at_the_newest_record()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(3, "A.sav");
            var view = new LogView(store, LogFilter.All);
            view.Update();

            view.Clear();
            view.Update();
            Assert.That(view.Count, Is.EqualTo(0));
            store.Log.Saved(1, "B.dat");
            view.Update();
            view.Filter = Parse("tests.save");
            view.Update();

            Assert.That(Messages(view), Is.EqualTo(new[] { "Saved slot 1 as B.dat" }));
            Assert.That(view.Counts.Total, Is.EqualTo(1));
        }

        [Test]
        public void A_row_is_found_by_the_sequence_of_a_record_it_holds()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(3, "A.sav");
            store.Log.Wrote(1);
            store.Log.Saved(3, "A.sav");
            store.Log.Saved(1, "B.dat");
            var view = new LogView(store, Parse("tests.save/Saved"));
            view.Update();

            Assert.That(view.Find(1).Or(-1), Is.EqualTo(0));
            Assert.That(view.Find(3).Or(-1), Is.EqualTo(0));
            Assert.That(view.Find(4).Or(-1), Is.EqualTo(1));
            Assert.That(view.Find(5).IsSome, Is.False);
            Assert.That(view.Find(0).IsSome, Is.False);
        }

        [Test]
        public void Reading_rows_gives_a_record_for_each()
        {
            LogStore store = Stores.Everything();
            for (int i = 0; i < 100; i++)
                store.Log.Step(i);
            var view = new LogView(store, LogFilter.All);
            view.Update();
            var batch = new LogBatch();

            Assert.That(view.Read(10, 80, batch), Is.EqualTo(80));

            Assert.That(batch[0].Sequence, Is.EqualTo(11));
            Assert.That(batch[79].ToString(), Is.EqualTo("Step 89"));
            Assert.Throws<ArgumentOutOfRangeException>(() => view.Read(90, 11, batch));
        }

        [Test]
        public void Writing_rows_writes_every_record_they_stand_for()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(3, "A.sav");
            store.Log.Wrote(1);
            store.Log.Saved(3, "A.sav");
            store.Log.Saved(3, "A.sav");
            store.Log.Failed(1, new IOException("No room."));
            var view = new LogView(store, Parse("-tests.save/Wrote"));
            view.Update();
            Assert.That(view.Count, Is.EqualTo(2));

            var all = new MemorySink();
            view.Write(all, 0, 2);
            var last = new MemorySink();
            view.Write(last, 1, 1);
            var none = new MemorySink();
            view.Write(none, 0, 0);

            Assert.That(all.Records.Select(r => r.Sequence), Is.EqualTo(new[] { 1L, 3L, 4L, 5L }));
            Assert.That(last.Records.Select(r => r.Sequence), Is.EqualTo(new[] { 5L }));
            Assert.That(none.Records, Is.Empty);
            Assert.That(none.Flushes, Is.EqualTo(1));
        }

        [Test]
        public void Written_rows_leave_out_what_the_rows_do_once_a_regular_expression_gave_up()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(1, new string('a', 40) + "!");
            store.Log.Saved(2, "aaa");
            var view = new LogView(store, Parse("-File:/^(a+)+$/"));
            view.Update();
            Assert.That(Rows(view).Select(r => r.Sequence), Is.EqualTo(new[] { 1L, 2L }), "Left out, it leaves nothing out from the record it took too long over.");

            var copy = new MemorySink();
            view.Write(copy, 1, 1);

            Assert.That(copy.Records.Select(r => r.Sequence), Is.EqualTo(new[] { 2L }), "The copy is of the rows as they are.");
        }

        [Test]
        public void Written_rows_are_a_file_the_view_can_read_back()
        {
            LogStore store = Stores.Everything();
            using (LogScope saving = store.Log.Saving(1))
            {
                saving.Log.Saved(1, "A.sav");
                saving.Log.SignedIn("ann@example.com", "C:/Saves");
            }

            var view = new LogView(store, LogFilter.All);
            view.Update();
            var stream = new MemoryStream();

            using (var sink = new JsonLinesSink(stream, leaveOpen: true))
                view.Write(sink, 0, view.Count, new LogSinkOptions(LogLevel.Trace, Redact: LogRedaction.Personal));

            LogSessionFile file = LogFile.Read(new MemoryStream(stream.ToArray()), "copy.jsonl")
                .Match(error => throw new InvalidOperationException(error.Message), read => read);
            Assert.That(file.Skipped, Is.Empty);
            Assert.That(file.Archive.Redacted, Is.EqualTo(LogRedaction.Personal));
            var copy = new LogView(file.Archive, LogFilter.All);
            copy.Update();
            Assert.That(Messages(copy), Is.EqualTo(new[] { "Saving slot 1", "Saved slot 1 as A.sav", "Signed in as [personal] from C:/Saves" }));
            Assert.That(copy[1].Depth, Is.EqualTo(1));
            Assert.That(copy.Scopes.Elapsed(copy[0].Sequence).IsSome, Is.True, "The scope closed after the last row, and the copy says how long it took.");
        }

        [Test]
        public void Writing_rows_writes_the_closing_of_each_opening_written_and_of_no_other()
        {
            LogStore store = Stores.Everything();
            using (LogScope outer = store.Log.Saving(1))
            {
                outer.Log.Saved(1, "A.sav");
                using (LogScope inner = outer.Log.Saving(2))
                    inner.Log.Wrote(2);
            }

            store.Log.Step(3);
            var view = new LogView(store, LogFilter.All);
            view.Update();
            Assert.That(Rows(view).Select(r => r.Sequence), Is.EqualTo(new[] { 1L, 2L, 3L, 4L, 7L }));

            var all = new MemorySink();
            view.Write(all, 0, view.Count);
            var outerOnly = new MemorySink();
            view.Write(outerOnly, 0, 1);
            var under = new MemorySink();
            view.Write(under, 1, 2);
            var apart = new MemorySink();
            view.Write(apart, new[] { 0, 4 });

            Assert.That(all.Records.Select(r => r.Sequence), Is.EqualTo(new[] { 1L, 2L, 3L, 4L, 5L, 6L, 7L }));
            Assert.That(outerOnly.Records.Select(r => r.Sequence), Is.EqualTo(new[] { 1L, 6L }),
                "The closing lies after the row, and says how long the scope took.");
            Assert.That(under.Records.Select(r => r.Sequence), Is.EqualTo(new[] { 2L, 3L, 5L }),
                "The inner scope's opening is written, and so is its closing past the rows; the outer's opening is not, and nor is its closing.");
            Assert.That(apart.Records.Select(r => r.Sequence), Is.EqualTo(new[] { 1L, 6L, 7L }),
                "The outer scope's closing lies between the rows, where it is written in its place.");
        }

        [Test]
        public void Rows_written_apart_are_written_through_one_opening_of_the_sink()
        {
            LogStore store = Stores.Everything();
            for (int i = 1; i <= 5; i++)
                store.Log.Step(i);
            var view = new LogView(store, LogFilter.All);
            view.Update();
            var counting = new OpeningSink();
            var stream = new MemoryStream();

            view.Write(counting, new[] { 0, 2, 3 });
            using (var sink = new JsonLinesSink(stream, leaveOpen: true))
                view.Write(sink, new[] { 1, 4 });

            Assert.That((counting.Opened, counting.Flushed), Is.EqualTo((1, 1)));
            Assert.That(counting.Sequences, Is.EqualTo(new[] { 1L, 3L, 4L }));
            LogSessionFile file = LogFile.Read(new MemoryStream(stream.ToArray()), "apart.jsonl")
                .Match(error => throw new InvalidOperationException(error.Message), read => read);
            Assert.That(file.Skipped, Is.Empty, "One header, and the records after it.");
            var copy = new LogView(file.Archive, LogFilter.All);
            copy.Update();
            Assert.That(Messages(copy), Is.EqualTo(new[] { "Step 2", "Step 5" }));
        }

        [Test]
        public void Rows_written_apart_are_in_order_each_once_and_of_the_view()
        {
            LogStore store = Stores.Everything();
            for (int i = 1; i <= 3; i++)
                store.Log.Step(i);
            var view = new LogView(store, LogFilter.All);
            view.Update();

            Assert.Throws<ArgumentException>(() => view.Write(new OpeningSink(), new[] { 2, 1 }));
            Assert.Throws<ArgumentException>(() => view.Write(new OpeningSink(), new[] { 1, 1 }));
            Assert.Throws<ArgumentOutOfRangeException>(() => view.Write(new OpeningSink(), new[] { 0, 3 }));
            Assert.Throws<ArgumentNullException>(() => view.Write(new OpeningSink(), (IReadOnlyList<int>)null!));
            var none = new OpeningSink();
            view.Write(none, Array.Empty<int>());
            Assert.That((none.Opened, none.Flushed, none.Sequences.Count), Is.EqualTo((1, 1, 0)), "No rows is a sink opened and flushed with nothing in it.");
        }

        [Test]
        public void A_view_counts_each_event_as_if_its_event_terms_kept_it()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(1, "A.sav");
            store.Log.Saved(1, "A.sav");
            store.Log.Wrote(4);
            store.Log.Heard(LogLevel.Warning, "a slot came loose");
            var view = new LogView(store, Parse("tests.heard.* level>=information \"slot\""));
            view.Update();

            Assert.That(Messages(view), Is.EqualTo(new[] { "Heard a slot came loose" }));
            Assert.That(view.EventCount(Place(store, "tests.save/Saved")), Is.EqualTo(2), "A repeat's every record, which its event terms alone leave out.");
            Assert.That(view.EventCount(Place(store, "tests.save/Wrote")), Is.EqualTo(0), "Its level and its text leave it out.");
            Assert.That(view.EventCount(Place(store, "tests.heard/Heard")), Is.EqualTo(1));
            Assert.That(view.EventCount(store.Events.Count + 5), Is.EqualTo(0), "An event declared since the view read.");
            Assert.Throws<ArgumentOutOfRangeException>(() => view.EventCount(-1));
        }

        [Test]
        public void An_events_count_drops_what_the_store_overwrites()
        {
            LogStore store = Stores.Everything(Stores.Tiny);
            for (int i = 0; i < 10; i++)
                store.Log.Step(i);
            var view = new LogView(store, LogFilter.All);
            view.Update();
            int step = Place(store, "tests.save/Step");
            Assert.That(view.EventCount(step), Is.EqualTo(10));

            for (int i = 0; i < 10; i++)
                store.Log.Wrote(i);
            view.Update();

            Assert.That(store.First, Is.EqualTo(5), "The store holds sixteen records.");
            Assert.That(view.EventCount(step), Is.EqualTo(6));
            Assert.That(view.EventCount(Place(store, "tests.save/Wrote")), Is.EqualTo(10));
            Assert.That(view.Counts.Trace, Is.EqualTo(6), "The counts by level drop the same records.");
        }

        [Test]
        public void An_events_count_takes_in_a_record_placed_late_once()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(1, "A.sav");
            store.Log.Step(2);
            store.Log.Saved(3, "C.sav");
            LogBatch records = All(store);
            LogArchive archive = Archive(store);
            archive.Keep(records[0], backlog: false);
            archive.Keep(records[2], backlog: false);
            var view = new LogView(archive, LogFilter.All);
            view.Update();
            int saved = Place(archive, "tests.save/Saved");
            Assert.That(view.EventCount(saved), Is.EqualTo(2));

            archive.Keep(records[1], backlog: true);
            view.Update();

            Assert.That(view.EventCount(Place(archive, "tests.save/Step")), Is.EqualTo(1));
            Assert.That(view.EventCount(saved), Is.EqualTo(2), "The records after it are read again, and counted once.");
        }

        [Test]
        public void Written_rows_say_what_they_are_under_though_its_opening_is_not_written()
        {
            LogStore store = Stores.Everything();
            using (LogScope a = store.Log.Saving(1))
            using (LogScope b = a.Log.Saving(2))
                b.Log.Saved(2, "B.sav");

            var view = new LogView(store, Parse("tests.save/Saved"));
            view.Update();
            var sink = new ScopeNamingSink();

            view.Write(sink, 0, view.Count);

            Assert.That(sink.Lines, Is.EqualTo(new[] { "Saved slot 2 as B.sav < Saving slot 2" }));
        }

        [Test]
        public void Writing_rows_says_what_the_store_overwrote_since()
        {
            LogStore store = Stores.Everything(Stores.Tiny);
            for (int i = 0; i < 10; i++)
                store.Log.Step(i);
            var view = new LogView(store, LogFilter.All);
            view.Update();
            for (int i = 0; i < 10; i++)
                store.Log.Wrote(i);
            var memory = new MemorySink();

            view.Write(memory, 0, view.Count);

            Assert.That(memory.Overwritten, Is.EqualTo(4));
            Assert.That(memory.Records.Select(r => r.Sequence), Is.EqualTo(new[] { 5L, 6L, 7L, 8L, 9L, 10L }));
        }

        [Test]
        public void Writing_rows_of_an_archive_says_where_records_were_lost_between_them()
        {
            var stream = new MemoryStream();
            LogStore store = Stores.Everything();
            using (new LogDrain(store, (new JsonLinesSink(stream, leaveOpen: true), new LogSinkOptions(LogLevel.Trace))))
            {
                store.Log.Step(1);
                store.Log.Step(2);
            }

            // A loss just before the last record, the second step.
            string[] lines = Encoding.UTF8.GetString(stream.ToArray()).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            string text = string.Join("\n", lines.Take(lines.Length - 1).Append("{\"lost\":7}").Append(lines[lines.Length - 1])) + "\n";
            LogSessionFile file = LogFile.Read(new MemoryStream(Encoding.UTF8.GetBytes(text)), "gap.jsonl")
                .Match(error => throw new InvalidOperationException(error.Message), read => read);
            var view = new LogView(file.Archive, LogFilter.All);
            view.Update();
            var memory = new MemorySink();

            view.Write(memory, 0, view.Count);

            Assert.That(Rows(view).Select(r => r.LostBefore), Is.EqualTo(new[] { 0L, 7L }));
            Assert.That(memory.Overwritten, Is.EqualTo(7));
            Assert.That(memory.Records, Has.Count.EqualTo(2));
        }

        [Test]
        public void A_view_of_a_scope_is_the_scope_on_every_thread()
        {
            LogStore store = Stores.Everything();
            long scope;
            store.Log.Saved(0, "Before.sav");
            using (LogScope saving = store.Log.Saving(1))
            {
                scope = saving.Id;
                Stores.OnAnotherThread(() => saving.Log.Saved(1, "A.sav"));
            }

            store.Log.Saved(9, "After.sav");
            var view = new LogView(store, Parse($"scope:{scope}"));
            view.Update();

            Assert.That(Messages(view), Is.EqualTo(new[] { "Saving slot 1", "Saved slot 1 as A.sav" }));
            Assert.That(view.Scopes.Elapsed(scope).IsSome, Is.True);
        }

        [Test]
        public void A_record_an_archive_places_late_is_shown_where_its_sequence_puts_it()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(1, "A.sav");
            store.Log.Saved(2, "B.sav");
            store.Log.Step(3);
            store.Log.Failed(4, new IOException("No room."));
            LogBatch records = All(store);
            LogArchive archive = Archive(store);
            archive.Keep(records[0], backlog: false);
            archive.Keep(records[1], backlog: false);
            archive.Keep(records[3], backlog: false);
            var view = new LogView(archive, LogFilter.All);
            view.Update();
            Assert.That(Rows(view).Select(r => r.Sequence), Is.EqualTo(new[] { 1L, 2L, 4L }));

            // The step, kept when the failure called for what came before it.
            archive.Keep(records[2], backlog: true);
            view.Update();

            Assert.That(Rows(view).Select(r => r.Sequence), Is.EqualTo(new[] { 1L, 2L, 3L, 4L }));
            Assert.That(view.Counts, Is.EqualTo(new LogLevelCounts(1, 0, 2, 0, 1, 0)));
            var batch = new LogBatch();
            view.Read(2, 1, batch);
            Assert.That(batch[0].IsBacklog, Is.True);
        }

        [Test]
        public void A_backlog_record_placed_late_inside_a_repeat_splits_it()
        {
            LogStore store = Stores.Everything();
            for (int i = 0; i < 3; i++)
                store.Log.Saved(3, "A.sav");
            LogBatch records = All(store);
            LogArchive archive = Archive(store);
            archive.Keep(records[0], backlog: false);
            archive.Keep(records[2], backlog: false);
            var view = new LogView(archive, LogFilter.All);
            view.Update();
            Assert.That(Rows(view).Single(), Is.EqualTo(new LogRow(1, 3, 2, 0, LogLevel.Information, LogRowKind.Event, 0, 0)));

            // Context is drawn apart from what was written, so it repeats neither record beside it.
            archive.Keep(records[1], backlog: true);
            view.Update();

            Assert.That(Rows(view).Select(r => (r.Sequence, r.Records)), Is.EqualTo(new[] { (1L, 1), (2L, 1), (3L, 1) }));
            Assert.That(view.Counts.Information, Is.EqualTo(3));
        }

        [Test]
        public void A_loss_is_counted_once_when_a_late_record_rewinds_the_rows_after_it()
        {
            LogStore store = Stores.Everything();
            for (int i = 1; i <= 4; i++)
                store.Log.Step(i);
            LogBatch records = All(store);
            LogArchive archive = Archive(store);
            archive.Keep(records[0], backlog: false);
            archive.Lose(2);
            archive.Keep(records[3], backlog: false);
            var view = new LogView(archive, LogFilter.All);
            view.Update();
            Assert.That(view.Lost, Is.EqualTo(2));

            archive.Keep(records[2], backlog: true);
            view.Update();

            Assert.That(Rows(view).Select(r => (r.Sequence, r.LostBefore)), Is.EqualTo(new[] { (1L, 0L), (3L, 0L), (4L, 2L) }));
            Assert.That(view.Lost, Is.EqualTo(2));
        }

        [Test]
        public void A_view_made_after_records_were_placed_late_does_not_rewind()
        {
            LogStore store = Stores.Everything();
            for (int i = 1; i <= 3; i++)
                store.Log.Step(i);
            LogBatch records = All(store);
            LogArchive archive = Archive(store);
            archive.Keep(records[0], backlog: false);
            archive.Keep(records[2], backlog: false);
            archive.Keep(records[1], backlog: true);

            var view = new LogView(archive, LogFilter.All);
            view.Update();
            view.Update();

            Assert.That(Rows(view).Select(r => r.Sequence), Is.EqualTo(new[] { 1L, 2L, 3L }));
            Assert.That(view.Counts.Trace, Is.EqualTo(3));
        }

        /// <summary>Opens a scope with a record and a scope with a step under it; says the outer scope.</summary>
        private static long Nested(LogStore store)
        {
            using LogScope saving = store.Log.Saving(1);
            saving.Log.Saved(1, "A.sav");
            using (LogScope nested = saving.Log.Saving(2))
                nested.Log.Step(5);

            return saving.Id;
        }

        private static LogFilter Parse(string query) =>
            LogFilter.Parse(query).Match(error => throw new InvalidOperationException(error.Message), filter => filter);

        private static LogRow[] Rows(LogView view) => Enumerable.Range(0, view.Count).Select(i => view[i]).ToArray();

        private static int[] Depths(LogStore store, LogFilter filter)
        {
            var view = new LogView(store, filter);
            view.Update();
            return Rows(view).Select(r => r.Depth).ToArray();
        }

        /// <summary>The message of each row's record.</summary>
        private static string[] Messages(LogView view)
        {
            var batch = new LogBatch();
            int count = view.Read(0, view.Count, batch);
            return Enumerable.Range(0, count).Select(i => batch[i].ToString()).ToArray();
        }

        /// <summary>Every record the store holds, in one batch.</summary>
        private static LogBatch All(LogStore store)
        {
            var batch = new LogBatch();
            store.Read(store.First, batch);
            return batch;
        }

        /// <summary>An archive of the session <paramref name="store"/> writes, which the test hands records by hand.</summary>
        private static LogArchive Archive(LogStore store) => new LogArchive(store.Program, store.Clock, cap: 1000);

        /// <summary>The place of the event <paramref name="key"/> in the events of <paramref name="records"/>.</summary>
        private static int Place(LogRecords records, string key)
        {
            for (int place = 0; place < records.Events.Count; place++)
            {
                if (records.Events[place].Key == key)
                    return place;
            }

            throw new InvalidOperationException($"The records have no event {key}.");
        }

        /// <summary>A sink that counts how often it is opened and flushed, and keeps the sequences written.</summary>
        private sealed class OpeningSink : ILogSink
        {
            public int Opened { get; private set; }

            public int Flushed { get; private set; }

            public List<long> Sequences { get; } = new List<long>();

            public string Name => "opening";

            public void Open(LogContext context) => Opened++;

            public void Write(LogRecord record, bool backlog, LogContext context) => Sequences.Add(record.Sequence);

            public void Lost(long count, LogContext context)
            {
            }

            public void Flush() => Flushed++;
        }

        /// <summary>A sink that writes each record's message and, after a <c>&lt;</c>, the opening of the scope it is under.</summary>
        private sealed class ScopeNamingSink : ILogSink
        {
            public List<string> Lines { get; } = new List<string>();

            public string Name => "scopes";

            public void Open(LogContext context)
            {
            }

            public void Write(LogRecord record, bool backlog, LogContext context) =>
                Lines.Add(context.TryScope(record.Scope, out LogRecord begin) ? record.ToString() + " < " + begin.ToString() : record.ToString());

            public void Lost(long count, LogContext context)
            {
            }

            public void Flush()
            {
            }
        }
    }
}
