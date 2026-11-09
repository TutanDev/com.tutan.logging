using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Tutan.Functional;
using Tutan.Logging.Editor;

namespace Tutan.Logging.Tests
{
    public class RecordDetailsTests
    {
        private static readonly UnityPaths s_paths = new UnityPaths("D:/Game", new[] { ("D:/Game/Packages/com.tutan.logging", "com.tutan.logging") });

        private const string Time = @"\d\d:\d\d:\d\d\.\d\d\d";

        [Test]
        public void A_place_is_its_file_as_unity_opens_it_and_its_line()
        {
            SourcePlace asset = SourcePlace.Of("D:\\Game\\Assets\\Game\\SaveManager.cs", 42, s_paths).ValueUnsafe();
            SourcePlace package = SourcePlace.Of("D:/Game/Packages/com.tutan.logging/Runtime/Log.cs", 7, s_paths).ValueUnsafe();
            SourcePlace outside = SourcePlace.Of("F:\\Other\\Tool.cs", 3, s_paths).ValueUnsafe();

            Assert.That((asset.File, asset.Line, asset.InProject), Is.EqualTo(("Assets/Game/SaveManager.cs", 42, true)));
            Assert.That((package.File, package.InProject), Is.EqualTo(("Packages/com.tutan.logging/Runtime/Log.cs", true)));
            Assert.That((outside.File, outside.InProject), Is.EqualTo(("F:/Other/Tool.cs", false)), "Opened by its whole path.");
            Assert.That((asset.Short, asset.ToString()), Is.EqualTo(("SaveManager.cs:42", "Assets/Game/SaveManager.cs:42")));
        }

        [TestCase(null, 3)]
        [TestCase("", 3)]
        [TestCase("   ", 3)]
        [TestCase("D:/Game/Assets/A.cs", 0)]
        [TestCase("<filename unknown>", 3)]
        public void A_place_is_none_without_a_file_or_a_line(string? file, int line)
        {
            Assert.That(SourcePlace.Of(file, line, s_paths).IsNone, Is.True);
        }

        [Test]
        public void The_lines_around_a_line_are_those_the_file_has()
        {
            string[] lines = { "one", "two  ", "three", "four", "five" };

            Assert.That(SourceLines.Around(lines, 3, 1), Is.EqualTo(new[] { new SourceLine(2, "two"), new SourceLine(3, "three"), new SourceLine(4, "four") }),
                "Without what trails a line.");
            Assert.That(SourceLines.Around(lines, 1, 2).Select(shown => shown.Number), Is.EqualTo(new[] { 1, 2, 3 }));
            Assert.That(SourceLines.Around(lines, 5, 3).Select(shown => shown.Number), Is.EqualTo(new[] { 2, 3, 4, 5 }));
            Assert.That(SourceLines.Around(lines, 2, 0), Is.EqualTo(new[] { new SourceLine(2, "two") }));
            Assert.That(SourceLines.Around(lines, 6, 3), Is.Empty, "The file changed since the record was written: it is shorter than the line.");
            Assert.That(SourceLines.Around(lines, 0, 3), Is.Empty);
            Assert.Throws<ArgumentOutOfRangeException>(() => SourceLines.Around(lines, 2, -1));
        }

        [Test]
        public void A_stack_unity_handed_over_is_a_frame_a_line()
        {
            string stack = "Game.SaveManager:Save () (at D:/Game/Assets/Game/SaveManager.cs:42)\r\n"
                + "UnityEngine.Debug:Log (object)\n"
                + "\n"
                + "Game.Boot:Start () (at ./Assets/Game/Boot.cs:9)\n";

            Assert.That(Frames(StackFrames.OfUnity(stack, s_paths)), Is.EqualTo(new[]
            {
                ("Game.SaveManager:Save ()", "Assets/Game/SaveManager.cs:42"),
                ("UnityEngine.Debug:Log (object)", ""),
                ("Game.Boot:Start ()", "Assets/Game/Boot.cs:9"),
            }));
            Assert.That(StackFrames.OfUnity("", s_paths), Is.Empty);
        }

        [Test]
        public void An_exceptions_frames_are_its_lines_that_start_at_as_mono_and_dotnet_write_them()
        {
            string text = string.Join("\n",
                "System.IO.IOException: A.sav is locked. ---> System.InvalidOperationException: The disk is gone.",
                "  at Game.Disk.Open () [0x0001c] in D:\\Game\\Assets\\Game\\Disk.cs:12 ",
                "   --- End of inner exception stack trace ---",
                "  at Game.SaveManager.Save (System.Int32 slot) [0x00010] in D:\\Game\\Assets\\Game\\SaveManager.cs:42",
                "  at Game.Cloud.Upload() in E:\\Elsewhere\\Cloud.cs:line 7",
                "  at Game.Native.Call () [0x00000] in <filename unknown>:0",
                "  at Game.Boot.Start ()");

            Assert.That(Frames(StackFrames.OfException(text, s_paths)), Is.EqualTo(new[]
            {
                ("Game.Disk.Open ()", "Assets/Game/Disk.cs:12"),
                ("Game.SaveManager.Save (System.Int32 slot)", "Assets/Game/SaveManager.cs:42"),
                ("Game.Cloud.Upload()", "E:/Elsewhere/Cloud.cs:7"),
                ("Game.Native.Call ()", ""),
                ("Game.Boot.Start ()", ""),
            }));
            Assert.That(StackFrames.OfException("System.Exception: It broke.", s_paths), Is.Empty, "An exception never thrown has no frames.");
        }

        [Test]
        public void A_records_details_are_its_fields_the_scopes_it_is_under_and_where_it_was_written_and_declared()
        {
            LogStore store = Stores.Everything();
            store.EnterFrame(12);
            LogScope outer = store.Log.Saving(1);
            int written;
            using (LogScope inner = outer.Log.Saving(2))
            {
                // On one line, so the call site's line is known.
                inner.Log.Saved(2, "B.sav"); written = Line();
            }

            var view = new LogView(store, LogFilter.All);
            view.Update();

            RecordDetails saved = Details(view, 2);
            outer.Dispose();

            Assert.That((saved.Key, saved.Level, saved.Kind, saved.Message), Is.EqualTo(("tests.save/Saved", LogLevel.Information, LogRecordKind.Event, "Saved slot 2 as B.sav")));
            Assert.That((saved.Frame, saved.Thread, saved.Records, saved.Suppressed, saved.Context), Is.EqualTo((12L, Environment.CurrentManagedThreadId, 1, 0L, false)));
            Assert.That(saved.At, Is.GreaterThanOrEqualTo(store.Clock.Started));
            Assert.That(saved.Stamp, Is.EqualTo(
                $"{saved.At.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}  ·  frame 12  ·  thread {Environment.CurrentManagedThreadId}"));
            Assert.That(Details(view, 0).Stamp, Does.EndWith("  ·  a scope's opening"));
            Assert.That(saved.OfFirst, Is.True);
            Assert.That(saved.Notes, Is.Empty);
            Assert.That(saved.Scopes[1].Line, Is.EqualTo("Saving slot 1  ·  open"));
            Assert.That(saved.Scopes[0].Line, Does.Match(@"^Saving slot 2  ·  [\d.]+ m?s$"));
            Assert.That(saved.Fields.Select(field => (field.Name, field.Value, field.Personal)), Is.EqualTo(new[] { ("Slot", "2", false), ("File", "B.sav", false) }));
            Assert.That(saved.Scopes.Select(scope => (scope.Scope, scope.Text)), Is.EqualTo(new[] { (2L, F.Some("Saving slot 2")), (1L, F.Some("Saving slot 1")) }),
                "The innermost first.");
            Assert.That(saved.Scopes[0].Time, Is.InstanceOf<ScopeTime.Took>());
            Assert.That(saved.Scopes[1].Time, Is.SameAs(ScopeTime.Open));
            Assert.That(saved.CallSite.Map(at => at.File).Or(""), Does.EndWith(nameof(RecordDetailsTests) + ".cs"));
            Assert.That(saved.CallSite.Map(at => at.Line).Or(0), Is.EqualTo(written));
            Assert.That(saved.Declaration.Map(at => at.File).Or(""), Does.EndWith("TestEvents.cs"));
            Assert.That(saved.Stack, Is.Empty);
            Assert.That(saved.Sequence, Is.EqualTo(view[2].Sequence));
            Assert.That(saved.Opens, Is.EqualTo(saved.CallSite), "Where it was written opens.");
            Assert.That(saved.ScopeToShow, Is.EqualTo(F.Some(2L)), "The innermost scope it is under.");
            Assert.That(Details(view, 0).ScopeToShow, Is.EqualTo(F.Some(1L)), "A scope's opening shows its own scope.");
            Assert.That(Details(view, 2), Is.EqualTo(saved).And.Not.SameAs(saved), "Read again, they show the same.");
            Assert.That(Details(view, 1), Is.Not.EqualTo(saved));
        }

        [Test]
        public void A_personal_field_is_shown_whole_and_marked_personal()
        {
            LogStore store = Stores.Everything();
            store.Log.SignedIn("ann@example.com", "C:/Saves");
            var view = new LogView(store, LogFilter.All);
            view.Update();

            RecordDetails signedIn = Details(view, 0);

            Assert.That(signedIn.Fields.Select(field => (field.Name, field.Value, field.Personal)),
                Is.EqualTo(new[] { ("Email", "ann@example.com", true), ("Folder", "C:/Saves", false) }), "The details are the developer's: a copy or an export is what leaves it out.");
            Assert.That(signedIn.ScopeToShow.IsNone, Is.True, "It is under no scope.");
        }

        [Test]
        public void A_scope_whose_opening_is_gone_is_shown_without_its_message()
        {
            LogStore store = Stores.Everything(Stores.Tiny);
            using (LogScope saving = store.Log.Saving(1))
            {
                for (int i = 0; i < LogStore.MinRecords; i++)
                    saving.Log.Step(i);
            }

            var view = new LogView(store, LogFilter.All);
            view.Update();
            RecordDetails step = Details(view, view.Count - 1);

            Assert.That(step.Scopes.Select(scope => (scope.Scope, scope.Text)), Is.EqualTo(new[] { (1L, default(Optional<string>)) }));
            Assert.That(step.Scopes[0].Time, Is.InstanceOf<ScopeTime.Took>(), "Its closing is held, and says how long it took.");
            Assert.That(step.Scopes[0].Line, Does.StartWith("scope:1, whose opening is gone  ·  "));
        }

        [Test]
        public void An_exceptions_stack_is_the_details_stack_and_its_field_is_its_whole_text()
        {
            LogStore store = Stores.Everything();
            Exception error = Thrown();
            store.Log.Failed(1, error);
            var view = new LogView(store, LogFilter.All);
            view.Update();

            RecordDetails failed = Details(view, 0);

            Assert.That(failed.Fields.Select(field => (field.Name, field.Value)), Is.EqualTo(new[] { ("Slot", "1"), ("Error", error.ToString()) }));
            Assert.That(failed.Fields[1].Kind, Is.EqualTo(LogValueKind.Exception));
            Assert.That(failed.Stack.Any(frame => frame.Method.Contains(nameof(Thrown))), Is.True, "Its frames, read from its text.");
        }

        [Test]
        public void The_stack_is_of_the_first_exception_that_has_frames()
        {
            LogStore store = Stores.Everything();
            Exception thrown = Thrown();
            store.Log.Lost("ann@example.com", new InvalidOperationException("Made and never thrown."));
            store.Log.Lost("ann@example.com", thrown);
            var view = new LogView(store, LogFilter.All);
            view.Update();

            Assert.That(Details(view, 0).Stack, Is.Empty, "An exception never thrown has no frames.");
            Assert.That(Details(view, 1).Stack.Any(frame => frame.Method.Contains(nameof(Thrown))), Is.True, "A personal exception's frames are shown too.");
        }

        [Test]
        public void A_message_unity_handed_over_shows_its_stack_as_frames_and_not_as_a_field()
        {
            LogStore store = Stores.Everything();
            store.Log.Logged(LogLevel.Warning, "from Unity", "Game.Boot:Start () (at Assets/Game/Boot.cs:9)\n", UnityEngine.LogType.Warning);
            var view = new LogView(store, LogFilter.All);
            view.Update();

            RecordDetails logged = Details(view, 0);

            Assert.That((logged.Key, logged.Message), Is.EqualTo(("unity/Logged", "from Unity")));
            Assert.That(logged.Fields.Select(field => field.Name), Is.EqualTo(new[] { "Message", "Type" }));
            Assert.That(Frames(logged.Stack), Is.EqualTo(new[] { ("Game.Boot:Start ()", "Assets/Game/Boot.cs:9") }));
        }

        [Test]
        public void A_record_with_no_call_site_opens_the_first_frame_in_the_project_or_else_its_declaration()
        {
            LogStore store = Stores.Everything();

            // As the bridge writes Unity's messages: with no call site of their own.
            store.Log.Logged(LogLevel.Warning, "from Unity", "Tool:Run () (at F:/Other/Tool.cs:3)\nGame.Boot:Start () (at Assets/Game/Boot.cs:9)\n",
                UnityEngine.LogType.Warning, "", 0);
            store.Log.Logged(LogLevel.Warning, "from Unity", "", UnityEngine.LogType.Warning, "", 0);
            var view = new LogView(store, LogFilter.All);
            view.Update();

            RecordDetails framed = Details(view, 0);
            RecordDetails bare = Details(view, 1);

            Assert.That(framed.CallSite.IsNone, Is.True);
            Assert.That(framed.Opens.Map(at => at.ToString()).Or(""), Is.EqualTo("Assets/Game/Boot.cs:9"), "Not the frame outside the project.");
            Assert.That((bare.Opens, bare.Opens.IsSome), Is.EqualTo((bare.Declaration, true)));
        }

        [Test]
        public void Details_and_cells_are_of_a_rows_record_and_refuse_another()
        {
            LogStore store = Stores.Everything();
            for (int i = 0; i < 3; i++)
                store.Log.Saved(1, "A.sav");
            store.Log.Saved(2, "B.sav");
            var view = new LogView(store, LogFilter.All);
            view.Update();
            LogRow repeated = view[0];
            Assert.That((repeated.Records, repeated.Last - repeated.Sequence), Is.EqualTo((3, 2L)));

            Assert.That(DetailsOf(store, view, repeated.Last, repeated).Sequence, Is.EqualTo(repeated.Last), "Its last, as read once its first is gone.");
            Assert.That((Details(view, 0).OfFirst, DetailsOf(store, view, repeated.Last, repeated).OfFirst), Is.EqualTo((true, false)),
                "They say which of the repeat's records they are of.");
            Assert.That(DetailsOf(store, view, repeated.Last, repeated), Is.Not.EqualTo(Details(view, 0)));
            Assert.That(Details(view, 0).Notes, Is.EqualTo(new[] { "Repeated 3 times: these are the first record's details." }));
            Assert.Throws<ArgumentException>(() => DetailsOf(store, view, repeated.Sequence + 1, repeated), "One of the records between.");
            Assert.Throws<ArgumentException>(() => DetailsOf(store, view, view[1].Sequence, repeated));
            Assert.Throws<ArgumentException>(() => CellsOf(store, view, view[1].Sequence, repeated));
        }

        [Test]
        public void A_repeat_whose_first_record_was_overwritten_shows_its_last()
        {
            LogStore store = Stores.Everything(Stores.Tiny);
            var view = new LogView(store, LogFilter.All);
            for (int i = 0; i < LogStore.MinRecords * 2; i++)
            {
                store.Log.Saved(1, "A.sav");
                view.Update();
            }

            Assert.That((view.Count, view[0].Sequence < view.Source.First), Is.EqualTo((1, true)), "One row, whose first record the session holds no longer.");

            RecordDetails shown = Details(view, 0);

            Assert.That((shown.OfFirst, shown.Sequence, shown.Records), Is.EqualTo((false, view[0].Last, LogStore.MinRecords * 2)));
            Assert.That(shown.Notes, Is.EqualTo(new[] { $"Repeated {LogStore.MinRecords * 2} times: these are the last record's details, the first being overwritten since." }));
        }

        [Test]
        public void Cells_held_are_read_again_when_their_row_its_fold_its_scopes_time_or_its_record_may_have_changed()
        {
            LogStore store = Stores.Everything();
            for (int step = 1; step <= 5; step++)
                store.Log.Step(step);
            var view = new LogView(store, LogFilter.All);
            view.Update();
            Assert.That(view.Count, Is.EqualTo(5));
            LogRow row = view[4];
            long first = view.Source.First;
            LogScopes scopes = view.Scopes;
            HeldCells held = HeldCells.Of(Cells(view, 4, folded: false), first);
            HeldCells gone = HeldCells.Of(RowCells.Gone(row, F.None), first);

            Assert.That(held.Final, Is.True, "A row that is not a scope's has no time to change.");
            Assert.That(held.Still(row, folded: false, first, scopes), Is.True, "Nothing changed.");
            Assert.That(held.Still(row, folded: true, first, scopes), Is.True, "A row that is not a scope's has no fold to change.");
            Assert.That(held.Still(row with { Records = 2, Last = row.Last + 1 }, folded: false, first, scopes), Is.False, "A repeat was added to it.");
            Assert.That(held.Still(row, folded: false, row.Sequence, scopes), Is.True, "The session let go of records before it, and holds its first still.");
            Assert.That(held.Still(row, folded: false, row.Sequence + 1, scopes), Is.False, "The session let go of its first record, so its last shows.");
            Assert.That(gone.Still(row, folded: false, row.Sequence + 1, scopes), Is.True, "Cells of no record stay so.");
            Assert.Throws<ArgumentNullException>(() => HeldCells.Of(null!, first));
            Assert.Throws<ArgumentNullException>(() => held.Still(row, folded: false, first, null!));

            // A scope's row: final once the time its cells were read with is the one its scope took, and
            // else still while the scopes say what they said, as for a record that opened no scope they know.
            LogRow scope = row with { Kind = LogRowKind.Scope };
            HeldCells closed = HeldCells.Of(RowCells.Gone(scope, Shows(new ScopeTime.Took(TimeSpan.FromMilliseconds(2)), folded: false)), first);
            Assert.That(closed.Still(scope, folded: false, first, scopes), Is.True);
            Assert.That(closed.Still(scope, folded: true, first, scopes), Is.False, "It was folded since.");
            Assert.That(HeldCells.Of(RowCells.Gone(scope, Shows(ScopeTime.Open, folded: false)), first).Still(scope, folded: false, first, scopes), Is.False,
                "Read open, and the scopes say otherwise now.");
            HeldCells unknown = HeldCells.Of(RowCells.Gone(scope, Shows(ScopeTime.Unknown, folded: false)), first);
            Assert.That(unknown.Final, Is.False, "Read not known, its closing may be read since.");
            Assert.That(unknown.Still(scope, folded: false, first, scopes), Is.True, "The scopes know it no better yet.");
        }

        [Test]
        public void An_open_scopes_cells_stay_while_it_is_open_and_are_read_again_once_it_closes()
        {
            LogStore store = Stores.Everything();
            LogScope saving = store.Log.Saving(1);
            saving.Log.Step(1);
            var view = new LogView(store, LogFilter.All);
            view.Update();
            LogRow row = view[0];
            Assert.That(row.Kind, Is.EqualTo(LogRowKind.Scope));
            HeldCells open = HeldCells.Of(Cells(view, 0, folded: false), view.Source.First);
            Assert.That((open.Final, open.Cells.Duration), Is.EqualTo((false, "open")));

            saving.Log.Step(2);
            view.Update();
            Assert.That(open.Still(view[0], folded: false, view.Source.First, view.Scopes), Is.True, "Still open, so it shows what it would.");

            saving.Dispose();
            view.Update();
            Assert.That(view[0], Is.EqualTo(row), "Its row is as it was.");
            Assert.That(open.Still(view[0], folded: false, view.Source.First, view.Scopes), Is.False, "It closed, so how long it took shows now.");
            Assert.That(HeldCells.Of(Cells(view, 0, folded: false), view.Source.First).Final, Is.True);
        }

        [Test]
        public void A_chains_mark_changes_when_what_the_chain_shows_does()
        {
            LogStore store = Stores.Everything(Stores.Tiny);
            LogScope outer = store.Log.Saving(1);
            LogScope inner = outer.Log.Saving(2);
            inner.Log.Step(1);
            var view = new LogView(store, LogFilter.All);
            view.Update();
            var batch = new LogBatch(1);
            Assert.That(view.Read(2, 1, batch), Is.EqualTo(1));
            long scope = batch[0].Scope;
            long outermost = view[0].Sequence;
            (ChainMark Mark, string[] Lines) Now()
            {
                view.Update();
                return (ChainMark.Of(view.Scopes, scope, view.Source.First), ScopeShown.Chain(view, scope).Select(static shown => shown.Line).ToArray());
            }

            var open = Now();
            Assert.That(open.Mark, Is.EqualTo(new ChainMark(2, 2, 0, 2)), "Two scopes, both open, both openings held.");
            var again = Now();
            Assert.That(again.Mark, Is.EqualTo(open.Mark), "Nothing changed, so neither did.");
            Assert.That(again.Lines, Is.EqualTo(open.Lines));

            inner.Dispose();
            var closed = Now();
            Assert.That(closed.Mark, Is.EqualTo(new ChainMark(2, 1, 1, 2)), "The inner scope closed.");
            Assert.That(closed.Lines, Is.Not.EqualTo(open.Lines), "It says how long it took.");

            // Closed first, as a store keeps the opening of a scope still open past what its ring holds.
            outer.Dispose();
            var bothClosed = Now();
            Assert.That(bothClosed.Mark, Is.EqualTo(new ChainMark(2, 0, 2, 2)), "The outer scope closed.");
            Assert.That(bothClosed.Lines, Is.Not.EqualTo(closed.Lines), "It says how long it took.");

            // Written past what the store holds, so the outer scope's opening is overwritten.
            for (int i = 0; i < 1000 && view.Source.First <= outermost; i++)
                store.Log.Wrote(i);
            var gone = Now();
            Assert.That(view.Source.First, Is.GreaterThan(outermost));
            Assert.That(gone.Mark, Is.EqualTo(new ChainMark(2, 0, 2, 1)), "An opening the chain shows is gone.");
            Assert.That(gone.Lines, Is.Not.EqualTo(bothClosed.Lines), "It shows the scope's id in place of its message.");
        }

        [Test]
        public void A_scopes_time_and_fold_are_given_for_a_scopes_row_and_for_no_other()
        {
            LogStore store = Stores.Everything();
            store.Log.Step(1);
            var view = new LogView(store, LogFilter.All);
            view.Update();
            var batch = new LogBatch(1);
            Assert.That(view.Read(0, 1, batch), Is.EqualTo(1));
            LogRow row = view[0];
            LogRow scope = row with { Kind = LogRowKind.Scope };
            Optional<RowScope> shows = Shows(new ScopeTime.Took(TimeSpan.FromMilliseconds(2)), folded: true);

            RowCells cells = RowCells.Gone(row, F.None);
            Assert.That((cells.ScopeTimeRead.IsNone, cells.Duration, cells.Folded, cells.Fold, cells.FoldTip), Is.EqualTo((true, "", false, "", "")));
            Assert.That(HeldCells.Of(cells, 1).Final, Is.True);
            Assert.That((RowCells.Gone(scope, shows).Duration, RowCells.Gone(scope, shows).Folded), Is.EqualTo(("2.0 ms", true)));

            Assert.Throws<ArgumentException>(() => RowCells.Gone(row, shows), "An event's row has no time and no fold.");
            Assert.Throws<ArgumentException>(() => RowCells.Gone(scope, F.None), "A scope's row shows both.");
            Assert.Throws<ArgumentException>(() => RowCells.Of(batch[0], row, view.Source.Clock, shows));
            Assert.Throws<ArgumentException>(() => RowCells.Of(batch[0], scope, view.Source.Clock, F.None));
            Assert.Throws<ArgumentNullException>(() => new RowScope(null!, folded: false));
        }

        [Test]
        public void A_count_is_shown_whole_under_ten_thousand_then_in_thousands_then_in_millions()
        {
            Assert.That(new[] { 0L, 9_999, 10_000, 9_999_999, 10_000_000, long.MaxValue }.Select(CountText.Compact),
                Is.EqualTo(new[] { "0", "9999", "10k", "9999k", "10M", "9223372036854M" }));
            Assert.That((CountText.Rows(0, scanning: false), CountText.Rows(1, scanning: false), CountText.Rows(1_234, scanning: true)),
                Is.EqualTo(("0 rows", "1 row", "1,234 rows · reading")));
        }

        [Test]
        public void A_level_is_drawn_with_its_class_and_its_badge_says_its_letter_and_count()
        {
            Assert.That(LevelLook.Shown, Is.EqualTo(new[] { LogLevel.Trace, LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error, LogLevel.Critical }));
            Assert.That(LevelLook.Shown.Select(LevelLook.Class),
                Is.EqualTo(new[] { "level-trace", "level-debug", "level-information", "level-warning", "level-error", "level-critical" }));
            Assert.That(LevelLook.Shown.Select(level => LevelLook.Badge(level, 12_345)), Is.EqualTo(new[] { "T 12k", "D 12k", "I 12k", "W 12k", "E 12k", "C 12k" }));
            Assert.That(LevelLook.BadgeTip(LogLevel.Warning, 12_345),
                Is.EqualTo("Warning: 12,345 records match but for the level. Click to show only Warning, and again to show every level."));
            Assert.That(LevelLook.BadgeTip(LogLevel.Error, 1), Does.StartWith("Error: 1 record matches but for the level."));
            Assert.Throws<ArgumentOutOfRangeException>(() => LevelLook.Class(LogLevel.None));
            Assert.Throws<ArgumentOutOfRangeException>(() => LevelLook.Badge(LogLevel.None, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => LevelLook.BadgeTip(LogLevel.None, 1));
        }

        [Test]
        public void A_rows_cells_are_read_from_its_record_once()
        {
            var clock = new TestClock();
            LogStore store = Stores.Everything(clock: clock);
            store.EnterFrame(7);
            for (int i = 0; i < 3; i++)
                store.Log.Saved(1, "A.sav");
            store.EnterFrame(0);
            using (LogScope saving = store.Log.Saving(2))
                clock.Advance(TimeSpan.FromTicks(124_000));
            store.Log.Said("first\nsecond");
            var view = new LogView(store, LogFilter.All);
            view.Update();

            RowCells saved = Cells(view, 0, folded: false);
            RowCells scope = Cells(view, 1, folded: true);
            RowCells said = Cells(view, 2, folded: false);

            Assert.That(saved.Time, Does.Match("^" + Time + "$"));
            Assert.That((saved.Frame, saved.Category, saved.Message, saved.Repeats, saved.Suppressed, saved.LostBefore),
                Is.EqualTo(("7", "tests.save", "Saved slot 1 as A.sav", "×3", "", "")));
            Assert.That((saved.Held, saved.Context, saved.Duration, saved.Folded), Is.EqualTo((true, false, "", false)));
            Assert.That((scope.Frame, scope.Message, scope.Duration, scope.Folded), Is.EqualTo(("", "Saving slot 2", "12.4 ms", true)), "No frame, as the store had none.");
            Assert.That(said.Message, Is.EqualTo("first …"), "A row shows a message's first line.");
        }

        [Test]
        public void A_rows_cells_say_what_it_adds_and_what_is_known_of_it_when_its_records_are_gone()
        {
            var row = new LogRow(5, 9, 1234, 1500, LogLevel.Information, LogRowKind.Event, 0, 1);
            RowCells gone = RowCells.Gone(row, F.None);
            RowCells scope = RowCells.Gone(row with { Kind = LogRowKind.Scope }, Shows(ScopeTime.Open, folded: true));

            Assert.That((gone.Repeats, gone.Suppressed, gone.LostBefore), Is.EqualTo(("×1,234", "+1,500", "1 record was lost here")));
            Assert.That((gone.Held, gone.Time, gone.Category, gone.Duration), Is.EqualTo((false, "", "", "")));
            Assert.That(gone.Message, Does.StartWith("Overwritten since"));
            Assert.That((scope.Duration, scope.Folded), Is.EqualTo(("open", true)));
            Assert.That(RowCells.Lost(2_500), Is.EqualTo("2,500 records were lost here"));
            Assert.That((gone.SuppressedTip, gone.Fold, gone.FoldTip), Is.EqualTo(("A limit dropped 1,500 records of this event before it.", "", "")));
            Assert.That((gone.LostMark, RowCells.Gone(row with { LostBefore = 0 }, F.None).LostMark), Is.EqualTo(("⚠", "")));
            Assert.That((scope.Fold, scope.FoldTip), Is.EqualTo(("▸", "Shows what is under this scope.")));
            Assert.That(RowCells.Gone(row with { Kind = LogRowKind.Scope }, Shows(ScopeTime.Open, folded: false)).Fold, Is.EqualTo("▾"));
            Assert.That(RowCells.Gone(row with { Suppressed = 0 }, F.None).SuppressedTip, Is.Empty);
            Assert.That(RowCells.Limited(1), Is.EqualTo("A limit dropped 1 record of this event before it."));
        }

        [Test]
        public void Times_are_shown_to_the_millisecond_and_durations_to_a_tenth_of_one_or_a_hundredth_of_a_second()
        {
            Assert.That(RowCells.Clock(new DateTimeOffset(2026, 11, 4, 14, 3, 12, 481, TimeSpan.Zero)), Is.EqualTo("14:03:12.481"));
            Assert.That(RowCells.Elapsed(TimeSpan.FromTicks(124_300)), Is.EqualTo("12.4 ms"));
            Assert.That(RowCells.Elapsed(TimeSpan.FromTicks(9_999_000)), Is.EqualTo("999.9 ms"));
            Assert.That(RowCells.Elapsed(TimeSpan.FromSeconds(1.5)), Is.EqualTo("1.50 s"));
            Assert.That(RowCells.Elapsed(TimeSpan.FromMinutes(2)), Is.EqualTo("120.00 s"));
            Assert.That(RowCells.DurationOf(new ScopeTime.Took(TimeSpan.FromMilliseconds(2))), Is.EqualTo("2.0 ms"));
            Assert.That(RowCells.DurationOf(ScopeTime.Open), Is.EqualTo("open"));
            Assert.That(RowCells.DurationOf(ScopeTime.Unknown), Is.EqualTo(""));
        }

        [Test]
        public void Rows_copied_as_text_are_a_line_each_with_an_exception_indented_after()
        {
            var clock = new TestClock();
            LogStore store = Stores.Everything(clock: clock);
            using (LogScope saving = store.Log.Saving(3))
            {
                saving.Log.Failed(3, new IOException("A.sav is locked.\r\nTry again."));
                clock.Advance(TimeSpan.FromMilliseconds(5));
            }

            store.Log.Heard(LogLevel.Warning, "loud");
            var view = new LogView(store, LogFilter.All);
            view.Update();
            var sink = new TextLinesSink();

            view.Write(sink, 0, view.Count);
            string[] lines = sink.Text.Split('\n');

            Assert.That(lines.Length, Is.EqualTo(7), sink.Text);
            Assert.That(lines[0], Does.Match("^" + Time + @" INF \[tests\.save\] Saving slot 3$"));
            Assert.That(lines[1], Does.Match("^" + Time + @" ERR \[tests\.save\] Saving slot 3 failed  < Saving slot 3$"), "What it is under.");
            Assert.That(lines[2], Is.EqualTo("    System.IO.IOException: A.sav is locked."));
            Assert.That(lines[3], Is.EqualTo("    Try again."));
            Assert.That(lines[4], Does.Match("^" + Time + @" INF \[tests\.save\] .*: done in 5\.0 ms$"), "The closing says how long the scope took.");
            Assert.That(lines[5], Does.Match("^" + Time + @" WRN \[tests\.heard\] Heard loud$"));
            Assert.That(lines[6], Is.Empty, "Each line ends.");
        }

        [Test]
        public void A_record_copied_as_text_says_when_it_was_an_errors_context_and_what_was_lost()
        {
            LogStore store = Stores.Everything();
            store.Log.Heard(LogLevel.Debug, "before it broke");
            var batch = new LogBatch(1);
            store.Read(store.First, batch);
            var context = new LogContext(store, new LogSinkOptions(LogLevel.Trace), new ScopeTable());
            var sink = new TextLinesSink();

            sink.Write(batch[0], backlog: true, context);
            sink.Lost(2, context);

            Assert.That(sink.Text, Does.Match("^" + Time + @" DBG \(context\) \[tests\.heard\] Heard before it broke\n2 records were lost here\n$"));
            Assert.That(TextLinesSink.Short(LogLevel.Critical), Is.EqualTo("CRT"));
            Assert.Throws<ArgumentOutOfRangeException>(() => TextLinesSink.Short(LogLevel.None));
        }

        [Test]
        public void A_session_is_called_by_its_number_its_program_and_how_it_is_or_its_files_name()
        {
            var sessions = new ConsoleSessions(live: true);
            LogConsoleSession.Live live = Start(sessions, "Sample Game");
            Start(sessions, "");
            sessions.End(live, new DateTimeOffset(2026, 11, 4, 9, 30, 15, TimeSpan.FromHours(1)));
            LogConsoleSession.File file = sessions.Open(FileOf("D:\\Logs\\run.jsonl"));

            Assert.That(sessions.Sessions.Select(SessionLabel.Of), Is.EqualTo(new[]
            {
                "1. Sample Game (ended 09:30:15)",
                "2. A program (live)",
                "3. run.jsonl",
            }));
            Assert.That(file.Id.Value, Is.EqualTo(3));
        }

        [Test]
        public void A_categorys_hue_is_the_same_every_time_and_differs_by_a_letter()
        {
            Assert.That(CategoryHue.Of("game.save"), Is.EqualTo(118 / 360f), "The hash is pinned: a category keeps its colour across versions.");
            Assert.That(CategoryHue.Of("game.saves"), Is.EqualTo(103 / 360f));
            Assert.That(CategoryHue.Of(""), Is.InRange(0f, 359 / 360f));
            Assert.Throws<ArgumentNullException>(() => CategoryHue.Of(null!));
        }

        [Test]
        public void A_categorys_colour_is_of_its_hue_lighter_on_the_dark_skin_and_deeper_on_the_light()
        {
            UnityEngine.Color dark = CategoryHue.Colour("game.save", dark: true);
            UnityEngine.Color light = CategoryHue.Colour("game.save", dark: false);

            Assert.That(CategoryHue.Colour("game.save", dark: true), Is.EqualTo(dark), "The same every time.");
            Assert.That(CategoryHue.Colour("game.saves", dark: true), Is.Not.EqualTo(dark));
            UnityEngine.Color.RGBToHSV(dark, out float darkHue, out float darkSaturation, out float darkValue);
            UnityEngine.Color.RGBToHSV(light, out float lightHue, out float lightSaturation, out float lightValue);
            Assert.That(darkHue, Is.EqualTo(CategoryHue.Of("game.save")).Within(0.001f));
            Assert.That(lightHue, Is.EqualTo(darkHue).Within(0.001f));
            Assert.That((darkSaturation < lightSaturation, darkValue > lightValue), Is.EqualTo((true, true)));
        }

        [Test]
        public void What_is_said_under_the_query_line_is_what_went_wrong_then_what_the_query_and_the_rows_lack()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(1, "A.sav");
            ConsoleState state = ConsoleState.Start.Type("nothing.here").Type("level>=loud");
            var view = new LogView(store, state.Filter);
            view.Update();

            ReadOnlyCollection<SaidLine> said = SaidLines.Of(F.Some<Told>(new Told.Trouble("The file is locked")), state.Query, F.Some(view), F.None, LeftOut.None);

            Assert.That(said.Select(static line => line.Kind), Is.EqualTo(new[] { SaidKind.Trouble, SaidKind.Refused, SaidKind.MatchingNothing }));
            Assert.That(said[0].Text, Is.EqualTo("The file is locked."));
            Assert.That(said[1].Text, Does.Match(@"(?<![.?!])[.?!] The rows are of the last query that read\.$"),
                "The query's words, an open clause, are closed before the line goes on.");
            Assert.That(said[2].Text, Does.StartWith("No event of this session is or has nothing.here."));
            Assert.That(SaidLines.Of(F.None, state.Query, F.None, F.None, LeftOut.None).Select(static line => line.Kind), Is.EqualTo(new[] { SaidKind.Refused }),
                "With no rows, only what the query says.");
            Assert.That(SaidLines.Of(F.None, QueryLine.Empty, F.Some(view), F.None, LeftOut.None), Is.Empty);
            Assert.That(SaidLines.Of(F.None, QueryLine.Empty, F.Some(view), F.None, LeftOut.None), Is.EqualTo(SaidLines.Of(F.None, QueryLine.Empty, F.Some(view), F.None, LeftOut.None)),
                "Said alike, they are equal, so the window draws them again only when they changed.");
            Assert.Throws<ArgumentNullException>(() => SaidLines.Of(F.None, null!, F.None, F.None, LeftOut.None));
        }

        [Test]
        public void An_error_let_go_before_it_could_be_shown_is_said_with_no_rows()
        {
            ReadOnlyCollection<SaidLine> said = SaidLines.Of(F.Some<Told>(Told.Gone.Instance), QueryLine.Empty, F.None, F.None, LeftOut.None);

            Assert.That(said.Select(static line => line.Kind), Is.EqualTo(new[] { SaidKind.Gone }), "As a press for a session let go draws it.");
            Assert.That(said[0].Text, Is.EqualTo("The error the toolbar showed was let go before it could be shown: past its session's cap, by a clear, or with its session."));
        }

        [Test]
        public void What_the_project_always_leaves_out_is_said_last_while_rows_are_shown()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(1, "A.sav");
            LeftOut always = LeftOut.Parse("-unity/Logged").Match(error => throw new InvalidOperationException(error.Message), leftOut => leftOut);
            var view = new LogView(store, always.From(LogFilter.All));
            view.Update();

            ReadOnlyCollection<SaidLine> said = SaidLines.Of(F.Some<Told>(Told.Hidden.Instance), QueryLine.Empty, F.Some(view), F.None, always);

            Assert.That(said.Select(static line => line.Kind), Is.EqualTo(new[] { SaidKind.Hidden, SaidKind.LeftOut }));
            Assert.That(said[0].Text, Does.StartWith("The error the toolbar showed is not among the rows: "));
            Assert.That(said[1].Text, Is.EqualTo("The project always leaves out -unity/Logged."), "The line does not show it, so it is said.");
            Assert.That(SaidLines.Of(F.None, QueryLine.Empty, F.Some(view), F.None, LeftOut.None), Is.Empty, "Nothing left out says nothing.");
            Assert.That(SaidLines.Of(F.None, QueryLine.Empty, F.None, F.None, always), Is.Empty, "With no rows, nothing is left out of them.");
            Assert.Throws<ArgumentNullException>(() => SaidLines.Of(F.None, QueryLine.Empty, F.None, F.None, null!));
        }

        [TestCase("Held by a scanner", "Held by a scanner.")]
        [TestCase("Held by a scanner.", "Held by a scanner.")]
        [TestCase("Is it held?", "Is it held?")]
        [TestCase("Held by a scanner.\r\n", "Held by a scanner.")]
        [TestCase("  ", "")]
        public void A_clause_from_elsewhere_is_closed_once_and_alike_by_the_runtime_and_the_window(string clause, string closed) =>
            Assert.That((LogText.Closed(clause), SaidLines.Closed(clause)), Is.EqualTo((closed, closed)),
                "The window's copy closes it as the runtime does.");

        [Test]
        public void An_export_that_could_not_delete_the_file_it_wrote_over_says_where_that_file_is()
        {
            Told told = new Told.NotDeleted("C:/Logs/Game export.jsonl", new LogNotDeleted("C:/Logs/Game export.jsonl.replaced-2", "The file is in use."));
            SaidLine said = told.Line;

            Assert.That(said.Kind, Is.EqualTo(SaidKind.NotDeleted), "A warning, not trouble: the export was written.");
            Assert.That(said.Text, Does.StartWith("The rows were exported to C:/Logs/Game export.jsonl."));
            Assert.That(said.Text, Does.Contain("is now C:/Logs/Game export.jsonl.replaced-2, and could not be deleted: The file is in use. "));
            Assert.That(SaidLines.Of(F.Some(told), QueryLine.Empty, F.None, F.None, LeftOut.None), Is.EqualTo(new[] { said }), "It is said as it was told.");
        }

        [Test]
        public void A_files_lines_that_could_not_be_read_are_said()
        {
            var sessions = new ConsoleSessions(live: true);
            LogConsoleSession.File file = sessions.Open(CutShort("D:\\Logs\\cut.jsonl"));
            var view = new LogView(file.Records, LogFilter.All);
            view.Update();

            ReadOnlyCollection<SaidLine> said = SaidLines.Of(F.None, QueryLine.Empty, F.Some(view), F.Some<LogConsoleSession>(file), LeftOut.None);

            Assert.That(said.Select(static line => line.Kind), Is.EqualTo(new[] { SaidKind.Skipped }));
            Assert.That(said[0].Text, Does.StartWith("A line of the file could not be read: Line "));
        }

        private static RecordDetails Details(LogView view, int index)
        {
            var batch = new LogBatch(1);
            Assert.That(view.Read(index, 1, batch), Is.EqualTo(1));
            LogRecord record = batch[0];
            return RecordDetails.Of(record, view[index], view.Source.Clock, ScopeShown.Chain(view, record.Scope), s_paths);
        }

        /// <summary>The cells of the row at <paramref name="index"/>, with its scope's time as the view's scopes say, for a scope's row.</summary>
        private static RowCells Cells(LogView view, int index, bool folded)
        {
            var batch = new LogBatch(1);
            Assert.That(view.Read(index, 1, batch), Is.EqualTo(1));
            LogRow row = view[index];
            return RowCells.Of(batch[0], row, view.Source.Clock, ShowsOf(row, ScopeTime.Of(view.Scopes, row.Sequence), folded));
        }

        private static Optional<RowScope> Shows(ScopeTime time, bool folded) => F.Some(new RowScope(time, folded));

        /// <summary>What <paramref name="row"/> shows of its scope: <paramref name="time"/> and the fold for a scope's row, and nothing for another.</summary>
        private static Optional<RowScope> ShowsOf(LogRow row, ScopeTime time, bool folded) =>
            row.Kind == LogRowKind.Scope ? Shows(time, folded) : F.None;

        /// <summary>The details of <paramref name="row"/>, given the record at <paramref name="sequence"/>, whichever it is.</summary>
        private static RecordDetails DetailsOf(LogStore store, LogView view, long sequence, LogRow row)
        {
            var batch = new LogBatch(1);
            store.Read(sequence, batch);
            LogRecord record = batch[0];
            Assert.That(record.Sequence, Is.EqualTo(sequence));
            return RecordDetails.Of(record, row, view.Source.Clock, ScopeShown.Chain(view, record.Scope), s_paths);
        }

        /// <summary>The cells of <paramref name="row"/>, given the record at <paramref name="sequence"/>, whichever it is.</summary>
        private static RowCells CellsOf(LogStore store, LogView view, long sequence, LogRow row)
        {
            var batch = new LogBatch(1);
            store.Read(sequence, batch);
            LogRecord record = batch[0];
            Assert.That(record.Sequence, Is.EqualTo(sequence));
            return RowCells.Of(record, row, view.Source.Clock, ShowsOf(row, ScopeTime.Unknown, folded: false));
        }

        private static (string, string)[] Frames(IEnumerable<StackFrame> frames) =>
            frames.Select(frame => (frame.Method, frame.At.Map(at => at.ToString()).Or(""))).ToArray();

        private static int Line([CallerLineNumber] int line = 0) => line;

        private static Exception Thrown()
        {
            try
            {
                throw new IOException("A.sav is locked.");
            }
            catch (IOException thrown)
            {
                return thrown;
            }
        }

        private static LogConsoleSession.Live Start(ConsoleSessions sessions, string program) =>
            sessions.Start(program, new LogClock(DateTimeOffset.UnixEpoch, 0), 100, LogRedaction.None)
                .Match(() => throw new InvalidOperationException("The sessions are live."), started => started);

        /// <summary>A file of one record, at <paramref name="path"/>.</summary>
        private static LogSessionFile FileOf(string path)
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(1, "A.sav");
            var view = new LogView(store, LogFilter.All);
            view.Update();
            var stream = new MemoryStream();
            using (var sink = new JsonLinesSink(stream, leaveOpen: true))
                view.Write(sink, 0, view.Count);

            return LogFile.Read(new MemoryStream(stream.ToArray()), path).Match(error => throw new InvalidOperationException(error.Message), file => file);
        }

        /// <summary>A file of two records, at <paramref name="path"/>, its last line cut short.</summary>
        private static LogSessionFile CutShort(string path)
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(1, "A.sav");
            store.Log.Saved(2, "B.sav");
            var view = new LogView(store, LogFilter.All);
            view.Update();
            var stream = new MemoryStream();
            using (var sink = new JsonLinesSink(stream, leaveOpen: true))
                view.Write(sink, 0, view.Count);

            byte[] written = stream.ToArray();
            return LogFile.Read(new MemoryStream(written, 0, written.Length - 10), path)
                .Match(error => throw new InvalidOperationException(error.Message), file => file);
        }
    }
}
