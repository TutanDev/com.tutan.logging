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
            Assert.That(Directory.GetFiles(_folder).Select(f => Path.GetFileName(f)), Is.EqualTo(new[] { expected }));
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

            string[] names = Directory.GetFiles(_folder).Select(f => Path.GetFileName(f)).OrderBy(n => n, StringComparer.Ordinal).ToArray();
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

            Assert.That(Path.GetFileName(Directory.GetFiles(_folder).Single()), Does.StartWith("Sample_Game-"));
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
        public void Keeping_fewer_than_one_file_is_refused()
        {
            var store = new LogStore(LogRules.Default, new LogStoreOptions(Program: "Game"));

            Assert.That(() => LogFile.Session(store, _folder, keep: 0), Throws.InstanceOf<ArgumentOutOfRangeException>());
        }
    }
}
