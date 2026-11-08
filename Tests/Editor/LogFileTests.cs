using System;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Tutan.Functional;

namespace Tutan.Logging.Tests
{
    public class LogFileTests
    {
        private string _folder = "";

        [SetUp]
        public void MakeFolder()
        {
            _folder = Path.Combine(Path.GetTempPath(), "Tutan.Logging.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        [TearDown]
        public void DeleteFolder()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }

        [Test]
        public void A_session_is_a_file_named_for_the_program_and_when_its_store_started()
        {
            var store = new LogStore(LogRules.Default, new LogStoreOptions(Program: "Sample Game"));

            JsonLinesSink sink = LogFile.Session(store, _folder).ValueUnsafe();
            using (var drain = new LogDrain(store, (sink, new LogSinkOptions())))
                store.Log.Saved(1, "a.sav");

            string expected = $"Sample Game-{store.Clock.Started:yyyyMMdd-HHmmss-fff}.jsonl";
            Assert.That(FileNames(), Is.EqualTo(new[] { expected }));
            Assert.That(sink.Name, Is.EqualTo(Path.Combine(_folder, expected)));

            string[] lines = File.ReadAllLines(Path.Combine(_folder, expected), Encoding.UTF8);
            Assert.That(lines[0], Does.StartWith("{\"format\":1,\"program\":\"Sample Game\","));
            Assert.That(lines, Has.Some.Contains("\"msg\":\"Saved slot 1 as a.sav\""));
        }

        [Test]
        public void A_session_keeps_the_programs_latest_files_and_leaves_other_programs_alone()
        {
            for (int i = 1; i <= 5; i++)
                File.WriteAllText(Path.Combine(_folder, $"Game-20200101-000000-00{i}.jsonl"), "");
            File.WriteAllText(Path.Combine(_folder, "Other-20200101-000000-000.jsonl"), "");
            File.WriteAllText(Path.Combine(_folder, "Game-notes.txt"), "");
            var store = new LogStore(LogRules.Default, new LogStoreOptions(Program: "Game"));

            LogFile.Session(store, _folder, keep: 3).ValueUnsafe().Dispose();

            string[] names = FileNames();
            Assert.That(names, Is.EqualTo(new[]
            {
                "Game-20200101-000000-004.jsonl",
                "Game-20200101-000000-005.jsonl",
                $"Game-{store.Clock.Started:yyyyMMdd-HHmmss-fff}.jsonl",
                "Game-notes.txt",
                "Other-20200101-000000-000.jsonl",
            }));
        }

        [Test]
        public void A_program_whose_name_a_file_cannot_have_is_written_with_underscores()
        {
            var store = new LogStore(LogRules.Default, new LogStoreOptions(Program: "Sample/Game"));

            LogFile.Session(store, _folder).ValueUnsafe().Dispose();

            Assert.That(FileNames().Single(), Does.StartWith("Sample_Game-"));
        }

        [Test]
        public void A_folder_that_cannot_be_made_fails_saying_so()
        {
            string blocker = Path.Combine(_folder, "blocker");
            File.WriteAllText(blocker, "");
            var store = new LogStore(LogRules.Default, new LogStoreOptions(Program: "Game"));

            Result<JsonLinesSink> session = LogFile.Session(store, Path.Combine(blocker, "Logs"));

            Assert.That(session.IsError, Is.True);
            Assert.That(session.ErrorUnsafe().Code, Is.EqualTo((int)LogFileError.NoFolder));
        }

        [Test]
        public void A_file_that_is_already_there_is_not_written_over()
        {
            var store = new LogStore(LogRules.Default, new LogStoreOptions(Program: "Game"));
            string taken = Path.Combine(_folder, $"Game-{store.Clock.Started:yyyyMMdd-HHmmss-fff}.jsonl");
            File.WriteAllText(taken, "another session's");

            Result<JsonLinesSink> session = LogFile.Session(store, _folder);

            Assert.That(session.IsError, Is.True);
            Assert.That(session.ErrorUnsafe().Code, Is.EqualTo((int)LogFileError.NoFile));
            Assert.That(File.ReadAllText(taken), Is.EqualTo("another session's"));
        }

        [Test]
        public void An_export_is_named_for_its_program_and_when_it_was_made_and_a_session_never_deletes_it()
        {
            var at = new DateTimeOffset(2026, 11, 4, 9, 30, 15, TimeSpan.FromHours(1));
            string export = LogFile.ExportName("Game", at);
            File.WriteAllText(Path.Combine(_folder, export), "");
            File.WriteAllText(Path.Combine(_folder, "Game-20200101-000000-001.jsonl"), "");
            var store = new LogStore(LogRules.Default, new LogStoreOptions(Program: "Game"));

            LogFile.Session(store, _folder, keep: 1).ValueUnsafe().Dispose();

            Assert.That(export, Is.EqualTo("Game export 20261104-093015.jsonl"), "At its own offset.");
            Assert.That(FileNames(), Is.EqualTo(new[]
            {
                "Game export 20261104-093015.jsonl",
                $"Game-{store.Clock.Started:yyyyMMdd-HHmmss-fff}.jsonl",
            }), "The program's oldest session went, and the export stayed.");
            Assert.That(LogFile.ExportName("Sample/Game", at), Is.EqualTo("Sample_Game export 20261104-093015.jsonl"));
            Assert.That(LogFile.ExportName("", at), Is.EqualTo("Program export 20261104-093015.jsonl"), "As a session of no program is named.");
            Assert.Throws<ArgumentNullException>(() => LogFile.ExportName(null!, at));
        }

        [Test]
        public void A_session_deletes_only_its_programs_session_files_and_not_those_of_a_program_whose_name_goes_on()
        {
            File.WriteAllText(Path.Combine(_folder, "Game-20200101-000000-001.jsonl"), "");
            File.WriteAllText(Path.Combine(_folder, "Game-Server-20200101-000000-001.jsonl"), "");
            File.WriteAllText(Path.Combine(_folder, "Game-Server export 20200101-000000.jsonl"), "");
            File.WriteAllText(Path.Combine(_folder, "Game-old.jsonl"), "");
            var store = new LogStore(LogRules.Default, new LogStoreOptions(Program: "Game"));

            LogFile.Session(store, _folder, keep: 1).ValueUnsafe().Dispose();

            Assert.That(FileNames(), Is.EqualTo(new[]
            {
                $"Game-{store.Clock.Started:yyyyMMdd-HHmmss-fff}.jsonl",
                "Game-Server export 20200101-000000.jsonl",
                "Game-Server-20200101-000000-001.jsonl",
                "Game-old.jsonl",
            }), "Game's oldest session went; Game-Server's files, and a file not named as a session's, stayed.");
        }

        [Test]
        public void A_session_never_prunes_its_own_file_even_when_it_sorts_among_the_oldest()
        {
            string[] names =
            {
                "Game-20261101-020000-000.jsonl", // its own, named after the clock went back an hour
                "Game-20261101-023000-000.jsonl",
                "Game-20261101-024000-000.jsonl",
                "Game-20261101-025000-000.jsonl",
                "Game-Server-20261101-010000-000.jsonl",
                "Game export 20261101-010000.jsonl",
            };

            Assert.That(LogFile.Pruned(names, "Game", names[0], keep: 3), Is.EqualTo(new[] { names[1] }), "Its own and the two newest stay.");
            Assert.That(LogFile.Pruned(names, "Game", names[0], keep: 1), Is.EqualTo(new[] { names[3], names[2], names[1] }));
            Assert.That(LogFile.Pruned(names, "Game", names[0], keep: 4), Is.Empty);
        }

        [Test]
        public void An_export_is_written_whole_and_one_that_fails_leaves_the_file_as_it_was()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(1, "A.sav");
            store.Log.SignedIn("ann@example.com", "C:/Saves");
            var view = new LogView(store, LogFilter.All);
            view.Update();
            string path = Path.Combine(_folder, "Game export 20261104-093015.jsonl");
            File.WriteAllText(path, "an export before");

            var written = LogFile.Export(view, path, new LogSinkOptions(LogLevel.Trace, 0, LogRedaction.Personal));

            Assert.That(written.IsError, Is.False);
            Assert.That(written.ValueUnsafe().NotDeleted.IsNone, Is.True, "The file it wrote over was deleted.");
            LogSessionFile file = LogFile.Read(path).Match(static error => throw new InvalidOperationException(error.Message), static read => read);
            var copy = new LogView(file.Archive, LogFilter.All);
            copy.Update();
            Assert.That((file.Skipped.Count, copy.Count, file.Archive.Redacted), Is.EqualTo((0, 2, LogRedaction.Personal)), "It was written over, whole.");
            Assert.That(FileNames(), Is.EqualTo(new[] { "Game export 20261104-093015.jsonl" }),
                "Nothing is left beside it.");

            // A folder where the export is written first, so it cannot be.
            string before = File.ReadAllText(path);
            Directory.CreateDirectory(path + ".partial");
            var failed = LogFile.Export(view, path, new LogSinkOptions());
            var nowhere = LogFile.Export(view, Path.Combine(_folder, "no folder", "x.jsonl"), new LogSinkOptions());

            Assert.That(failed.IsError, Is.True);
            Assert.That(failed.ErrorUnsafe().Code, Is.EqualTo((int)LogExportError.NotWritten));
            Assert.That(failed.ErrorUnsafe().Message, Does.StartWith($"The rows could not be exported to {path}: "));
            Assert.That(File.ReadAllText(path), Is.EqualTo(before), "The file is as it was.");
            Assert.That(nowhere.ErrorUnsafe().Code, Is.EqualTo((int)LogExportError.NotWritten));
            Assert.Throws<ArgumentNullException>(() => LogFile.Export(null!, path, new LogSinkOptions()));
            Assert.Throws<ArgumentNullException>(() => LogFile.Export(view, null!, new LogSinkOptions()));
            Assert.Throws<ArgumentNullException>(() => LogFile.Export(view, path, null!));
        }

        [Test]
        public void An_export_never_writes_over_a_file_an_earlier_export_left_aside()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(1, "A.sav");
            var view = new LogView(store, LogFilter.All);
            view.Update();
            string path = Path.Combine(_folder, "Game export 20261104-093015.jsonl");
            File.WriteAllText(path, "an export before");
            File.WriteAllText(path + ".replaced", "the file a failed export left aside");

            var written = LogFile.Export(view, path, new LogSinkOptions());

            Assert.That(written.IsError, Is.False);
            Assert.That(written.ValueUnsafe().NotDeleted.IsNone, Is.True, "The file it wrote over was deleted.");
            Assert.That(File.ReadAllText(path), Is.Not.EqualTo("an export before"), "It was written over.");
            Assert.That(File.ReadAllText(path + ".replaced"), Is.EqualTo("the file a failed export left aside"), "What was left aside is as it was.");
            Assert.That(FileNames(),
                Is.EqualTo(new[] { "Game export 20261104-093015.jsonl", "Game export 20261104-093015.jsonl.replaced" }),
                "The export let go of the file it moved aside, and only that one.");
        }

        [Test]
        public void A_name_no_file_has_is_the_name_or_the_first_numbered_after_it()
        {
            string name = Path.Combine(_folder, "x.jsonl.replaced");
            Assert.That(LogFile.Unused(name), Is.EqualTo(name));

            File.WriteAllText(name, "");
            Directory.CreateDirectory(name + "-2");
            Assert.That(LogFile.Unused(name), Is.EqualTo(name + "-3"), "A folder takes a name as a file does.");
        }

        [Test]
        public void An_export_to_a_new_file_moves_it_into_place()
        {
            LogView view = Saved();
            string path = Path.Combine(_folder, "Game export 20261104-093015.jsonl");

            var written = LogFile.Export(view, path, new LogSinkOptions());

            Assert.That(written.IsError, Is.False);
            Assert.That(written.ValueUnsafe().NotDeleted.IsNone, Is.True);
            Assert.That(LogFile.Read(path).IsSuccess, Is.True, "It reads back.");
            Assert.That(FileNames(), Is.EqualTo(new[] { "Game export 20261104-093015.jsonl" }), "Nothing is left beside it.");
        }

        [Test]
        public void An_export_says_the_file_it_wrote_over_when_it_cannot_delete_it()
        {
            LogView view = Saved();
            string path = Path.Combine(_folder, "Game export 20261104-093015.jsonl");
            File.WriteAllText(path, "an export before");

            var written = LogFile.Export(view, path, new LogSinkOptions(), LogFile.Replace, static _ => F.Error("Held by a scanner"));

            Assert.That(written.IsError, Is.False, "It was written.");
            LogNotDeleted notDeleted = written.ValueUnsafe().NotDeleted.ValueUnsafe();
            Assert.That((notDeleted.Path, notDeleted.Reason), Is.EqualTo((path + ".replaced", "Held by a scanner.")), "Where it is, and why, as a sentence.");
            Assert.That(File.ReadAllText(path + ".replaced"), Is.EqualTo("an export before"), "The file it wrote over is as it was, where it says.");
            Assert.That(LogFile.Read(path).IsSuccess, Is.True, "The export is in its place.");
        }

        [Test]
        public void An_export_whose_replace_failed_leaves_the_file_as_it_was_and_deletes_what_it_wrote()
        {
            LogView view = Saved();
            string path = Path.Combine(_folder, "Game export 20261104-093015.jsonl");
            File.WriteAllText(path, "an export before");

            var held = LogFile.Export(view, path, new LogSinkOptions(),
                static (_, _, _) => throw new IOException("Held by a scanner."), LogFile.Delete);
            var halfWay = LogFile.Export(view, path, new LogSinkOptions(), static (_, at, aside) =>
            {
                File.Move(at, aside);
                throw new IOException("Held by a scanner.");
            }, LogFile.Delete);

            Assert.That(held.ErrorUnsafe().Code, Is.EqualTo((int)LogExportError.NotWritten));
            Assert.That(held.ErrorUnsafe().Message, Is.EqualTo($"The rows could not be exported to {path}: Held by a scanner."));
            Assert.That(halfWay.ErrorUnsafe().Code, Is.EqualTo((int)LogExportError.NotWritten), "What the replace moved aside was put back.");
            Assert.That(File.ReadAllText(path), Is.EqualTo("an export before"), "The file is as it was.");
            Assert.That(FileNames(), Is.EqualTo(new[] { "Game export 20261104-093015.jsonl" }), "What was written was deleted.");

            // A replace that left a copy at the name it moves aside to, beside the file it left as it was.
            var copied = LogFile.Export(view, path, new LogSinkOptions(), static (_, at, aside) =>
            {
                File.Copy(at, aside);
                throw new IOException("Held by a scanner.");
            }, LogFile.Delete);

            Assert.That(copied.ErrorUnsafe().Code, Is.EqualTo((int)LogExportError.LeftBeside), "A code of its own, so a caller tells a file left beside from nothing left.");
            Assert.That(copied.ErrorUnsafe().Message, Is.EqualTo(
                $"The rows could not be exported to {path}: Held by a scanner. The replace left {path}.replaced beside it, which may be the only copy of the file that was there."));
            Assert.That(File.ReadAllText(path), Is.EqualTo("an export before"), "The file is as it was.");
            Assert.That(File.ReadAllText(path + ".replaced"), Is.EqualTo("an export before"), "What the replace left beside it is kept.");

            // The same, with what was written held too: it is said before the file left beside, which ends the error.
            var stuck = LogFile.Export(view, path, new LogSinkOptions(), static (_, at, aside) =>
            {
                File.Copy(at, aside);
                throw new IOException("Held by a scanner.");
            }, static _ => F.Error("Held by a scanner"));

            Assert.That(stuck.ErrorUnsafe().Code, Is.EqualTo((int)LogExportError.LeftBeside));
            Assert.That(stuck.ErrorUnsafe().Message, Is.EqualTo(
                $"The rows could not be exported to {path}: Held by a scanner. What was written could not be deleted, and is {path}.partial: Held by a scanner. "
                + $"The replace left {path}.replaced-2 beside it, which may be the only copy of the file that was there."),
                "The first file left beside is kept, so the replace moves aside to the next name no file has.");
        }

        [Test]
        public void An_export_says_where_what_it_moved_aside_and_what_it_wrote_are_when_it_cannot_put_them_right()
        {
            LogView view = Saved();
            string path = Path.Combine(_folder, "Game export 20261104-093015.jsonl");
            File.WriteAllText(path, "an export before");

            // A replace that moved the file aside and failed, with a folder now at the path, so it cannot be
            // put back, and a delete that cannot delete what was written.
            var failed = LogFile.Export(view, path, new LogSinkOptions(), static (_, at, aside) =>
            {
                File.Move(at, aside);
                Directory.CreateDirectory(at);
                throw new IOException("Held by a scanner");
            }, static _ => F.Error("Held by a scanner"));

            Assert.That(failed.ErrorUnsafe().Code, Is.EqualTo((int)LogExportError.NotPutBack));
            Assert.That(failed.ErrorUnsafe().Message, Does.StartWith(
                $"The rows could not be exported to {path}: Held by a scanner. The file that was there is now {path}.replaced, and could not be put back: "));
            Assert.That(failed.ErrorUnsafe().Message, Does.EndWith($" What was written could not be deleted, and is {path}.partial: Held by a scanner."),
                "Why it could not be put back is in the disk's own words, between, and each part is closed as a sentence.");
            Assert.That(File.ReadAllText(path + ".replaced"), Is.EqualTo("an export before"), "The file is where the error says.");
            Assert.That(File.Exists(path + ".partial"), Is.True, "So is what was written.");
        }

        private static LogView Saved()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(1, "A.sav");
            var view = new LogView(store, LogFilter.All);
            view.Update();
            return view;
        }

        private string[] FileNames() =>
            Directory.GetFiles(_folder).Select(static file => Path.GetFileName(file)).OrderBy(static name => name, StringComparer.Ordinal).ToArray();

        [Test]
        public void Keeping_fewer_than_one_file_is_refused()
        {
            var store = new LogStore(LogRules.Default, new LogStoreOptions(Program: "Game"));

            Assert.That(() => LogFile.Session(store, _folder, keep: 0), Throws.InstanceOf<ArgumentOutOfRangeException>());
        }
    }
}
