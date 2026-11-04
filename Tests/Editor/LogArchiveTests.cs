using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using Tutan.Functional;
using UnityEngine;

namespace Tutan.Logging.Tests
{
    public class LogArchiveTests
    {
        private static readonly Guid s_id = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        private static readonly DateTime s_when = new DateTime(2026, 10, 29, 10, 24, 11, 123, DateTimeKind.Utc);
        private static readonly DateTimeOffset s_at = new DateTimeOffset(2026, 10, 29, 10, 24, 11, 456, TimeSpan.FromHours(1));

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
        public void Every_kind_of_field_reads_back_as_it_was_written()
        {
            (LogStore store, string[] lines) = Write(store =>
            {
                store.EnterFrame(1);
                store.Log.Numbers(true, 'x', -5, 250, -30_000, 60_000, -2, 4_000_000_000u, long.MinValue, ulong.MaxValue);
                store.Log.Numbers(false, 'é', 5, 0, 1, 2, 3, 4, 5, 6);
                store.Log.Reals(float.NaN, double.PositiveInfinity, 12.50m);
                store.Log.Reals(1.25f, -0.1, -7m);
                store.Log.Reals(float.NegativeInfinity, double.NaN, 0m);
                store.Log.Times(s_id, s_when, s_at, TimeSpan.FromSeconds(1.5));
                store.Log.Times(Guid.Empty, DateTime.MinValue, DateTimeOffset.MaxValue, TimeSpan.MinValue);
                store.Log.Things(Doors.Front | Doors.Back, "a \"quote\"", "chars", new Vector3(1, 2, 3));
                store.Log.Things(Doors.None, "", "", Vector3.zero);
                store.Log.Things((Doors)8, "é\n\t", "\u0001", Vector3.one);
                store.Log.Optionals(3, 4, "note", new Vector3(1, 2, 3));
                store.Log.Optionals(F.None, null, F.None, F.None);
                store.Log.Aligned("left", 1, 0.25, Doors.Side);
                store.Log.Said(null);
                store.Log.Said("there");
                store.Log.SignedIn("ann@example.com", "C:/Saves");
                store.Log.Heard(LogLevel.Warning, "the disk is nearly full");
                store.Log.Strike();
            });

            LogSessionFile file = Read(lines);

            Assert.That(file.Skipped, Is.Empty);
            MemoryRecord[] written = Held.Records(store);
            MemoryRecord[] read = Held.Records(file.Archive);
            Assert.That(read.Length, Is.EqualTo(written.Length));
            for (int i = 0; i < written.Length; i++)
            {
                Assert.That(read[i].Message, Is.EqualTo(written[i].Message));
                Assert.That(read[i].Event.Key, Is.EqualTo(written[i].Event.Key));
                Assert.That(read[i].Fields.Select(f => (f.Name, f.Text)), Is.EqualTo(written[i].Fields.Select(f => (f.Name, f.Text))));
                for (int f = 0; f < written[i].Fields.Count; f++)
                {
                    // Read back as their text, which the loop above compares.
                    LogValueKind kind = written[i].Event.Fields[f].Kind;
                    if (kind is not (LogValueKind.Chars or LogValueKind.Formattable))
                        Assert.That(read[i].Fields[f].Value, Is.EqualTo(written[i].Fields[f].Value), $"{written[i].Event.Key} {written[i].Fields[f].Name}");
                }
            }
        }

        [Test]
        public void A_field_is_read_back_as_the_kind_it_was_declared_as()
        {
            string[] lines = Write(store =>
            {
                store.Log.Things(Doors.Front, "text", "chars", new Vector3(1, 2, 3));
                store.Log.Optionals(1, 2, "note", Vector3.one);
                store.Log.SignedIn("ann@example.com", "C:/Saves");
            }).Lines;

            LogArchive archive = Read(lines).Archive;

            LogEventSpec Spec(string key) => archive.Events.Single(e => e.Key == key);
            LogEventSpec things = Spec("tests.fields/Things");
            Assert.That(things.Fields.Select(f => f.Kind), Is.EqualTo(new[] { LogValueKind.Enum, LogValueKind.String, LogValueKind.String, LogValueKind.String }));
            Assert.That(things.Fields[0].Names!.Flags, Is.True);
            Assert.That(things.Fields[3].Type, Is.EqualTo(FieldLog.Events.Things.Fields[3].Type));
            Assert.That(things.Source, Is.EqualTo(FieldLog.Events.Things.Source));
            Assert.That(things, Is.Not.SameAs(FieldLog.Events.Things));
            LogEventSpec optionals = Spec("tests.fields/Optionals");
            Assert.That(optionals.Fields.Select(f => f.Kind), Is.EqualTo(new[] { LogValueKind.Int32, LogValueKind.Int32, LogValueKind.String, LogValueKind.String }));
            Assert.That(optionals.Fields.Select(f => f.IsOptional), Is.All.True);
            LogEventSpec signedIn = Spec("tests.people/SignedIn");
            Assert.That((signedIn.Fields[0].Personal, signedIn.Fields[1].Tag.Or("")), Is.EqualTo((true, "path")));
        }

        [Test]
        public void A_record_reads_back_with_its_stamps_and_its_time()
        {
            var clock = new TestClock();
            (LogStore store, string[] lines) = Write(clock: clock, write: store =>
            {
                store.EnterFrame(1);
                using (LogScope saving = store.Log.Saving(1))
                {
                    // In ticks: on Unity's Mono, TimeSpan.FromMilliseconds rounds to a whole millisecond.
                    clock.Advance(TimeSpan.FromTicks(125_000));
                    saving.Log.Saved(1, "A.sav");
                    Stores.OnAnotherThread(() => saving.Log.Wrote(4));
                    clock.Advance(TimeSpan.FromTicks(275_000));
                }

                for (int i = 0; i < 5; i++)
                    store.Log.Hit(i);
                clock.Advance(TimeSpan.FromSeconds(1));
                store.Log.Hit(5);
            });

            LogArchive archive = Read(lines).Archive;

            Assert.That(archive.Program, Is.EqualTo("Tests"));
            Assert.That(archive.Clock.Started, Is.EqualTo(store.Clock.Started));
            Assert.That((archive.First, archive.Next), Is.EqualTo((store.First, store.Next)));
            Assert.That(archive.Capacity, Is.EqualTo(archive.Count), "A file's archive holds what the file does.");
            var written = new LogBatch();
            var read = new LogBatch();
            store.Read(store.First, written);
            archive.Read(archive.First, read);
            Assert.That(read.Count, Is.EqualTo(written.Count));
            for (int i = 0; i < written.Count; i++)
            {
                LogRecord a = written[i];
                LogRecord b = read[i];
                Assert.That((b.Sequence, b.Frame, b.Scope, b.Begin, b.Thread, b.Level, b.Kind, b.Suppressed, b.CallSite.File, b.CallSite.Line),
                    Is.EqualTo((a.Sequence, a.Frame, a.Scope, a.Begin, a.Thread, a.Level, a.Kind, a.Suppressed, a.CallSite.File, a.CallSite.Line)));
                Assert.That((store.Clock.At(a.Timestamp) - archive.Clock.At(b.Timestamp)).Duration(), Is.LessThanOrEqualTo(TimeSpan.FromTicks(1)));
                // The file holds how long a scope was open to the microsecond.
                Assert.That((a.Elapsed - b.Elapsed).Duration(), Is.LessThanOrEqualTo(TimeSpan.FromTicks(5)));
            }

            MemoryRecord[] records = Held.Records(archive);
            Assert.That(records.Single(r => r.Kind == LogRecordKind.ScopeEnd).Elapsed.TotalMilliseconds, Is.EqualTo(40).Within(0.001));
            Assert.That(records.Select(r => r.Suppressed), Has.Member(3));
            Assert.That(records.Select(r => r.Thread).Distinct().Count(), Is.EqualTo(2));
        }

        [Test]
        public void An_exception_reads_back_as_the_type_and_text_it_was_thrown_with()
        {
            string[] lines = Write(store => store.Log.Failed(3, Thrown())).Lines;

            MemoryRecord failed = Held.Records(Read(lines).Archive).Single();

            Assert.That(failed.Field("Error"), Is.InstanceOf<ArchivedException>());
            var exception = (ArchivedException)failed.Field("Error")!;
            Assert.That(exception.TypeName, Is.EqualTo("System.IO.InvalidDataException"));
            Assert.That(exception.Message, Is.EqualTo("No buffer."));
            Assert.That(exception.ToString(), Does.StartWith("System.IO.InvalidDataException: No buffer."));
            Assert.That(exception.ToString(), Does.Contain(nameof(Thrown)));
            Assert.That(failed.Fields[1].Text, Is.EqualTo("InvalidDataException: No buffer."));
            Assert.That(failed.Message, Is.EqualTo("Saving slot 3 failed"));
        }

        [Test]
        public void A_file_with_personal_fields_left_out_reads_them_as_text()
        {
            string[] lines = Write(sink: new LogSinkOptions(LogLevel.Trace, Redact: LogRedaction.Personal), write: store =>
            {
                store.Log.SignedIn("ann@example.com", "C:/Saves");
                store.Log.Lost("ann@example.com", new IOException("No room."));
            }).Lines;

            LogSessionFile file = Read(lines);

            Assert.That(file.Skipped, Is.Empty);
            Assert.That(file.Archive.Redacted, Is.EqualTo(LogRedaction.Personal));
            MemoryRecord[] records = Held.Records(file.Archive);
            Assert.That(records.Select(r => r.Message), Is.EqualTo(new[] { "Signed in as [personal] from C:/Saves", "Lost [personal]" }));
            Assert.That(records[0].Field("Email"), Is.EqualTo("[personal]"));
            Assert.That(records[1].Field("Error"), Is.EqualTo("[personal]"));
            Assert.That(records[1].Event.Fields.Select(f => (f.Kind, f.Personal)), Is.All.EqualTo((LogValueKind.String, true)));
        }

        [Test]
        public void A_redacted_file_written_again_says_what_it_left_out()
        {
            string[] lines = Write(sink: new LogSinkOptions(LogLevel.Trace, Redact: LogRedaction.Personal),
                write: store => store.Log.SignedIn("ann@example.com", "C:/Saves")).Lines;
            var view = new LogView(Read(lines).Archive, LogFilter.All);
            view.Update();

            var stream = new MemoryStream();
            using (var sink = new JsonLinesSink(stream, leaveOpen: true))
                view.Write(sink, 0, view.Count);
            LogSessionFile again = Read(Encoding.UTF8.GetString(stream.ToArray()));

            Assert.That(again.Skipped, Is.Empty);
            Assert.That(again.Archive.Redacted, Is.EqualTo(LogRedaction.Personal), "What the first file left out, the second never had.");
            Assert.That(Held.Records(again.Archive).Single().Message, Is.EqualTo("Signed in as [personal] from C:/Saves"));
        }

        [Test]
        public void A_file_reads_only_what_its_sink_wrote()
        {
            string[] lines = Write(sink: new LogSinkOptions(LogLevel.Warning), write: store =>
            {
                store.Log.Saved(3, "A.sav");
                store.Log.Failed(3, Thrown());
            }).Lines;

            LogArchive archive = Read(lines).Archive;

            Assert.That(Held.Records(archive).Single().Sequence, Is.EqualTo(2));
            Assert.That(archive.Events.Single().Key, Is.EqualTo("tests.save/Failed"));
            Assert.That((archive.First, archive.Next), Is.EqualTo((2L, 3L)));
        }

        [Test]
        public void A_last_line_cut_short_is_skipped_and_the_rest_is_read()
        {
            string[] lines = Write(store =>
            {
                store.Log.Saved(1, "A.sav");
                store.Log.Saved(2, "B.sav");
            }).Lines;
            string text = string.Join("\n", lines);

            LogSessionFile file = Read(text.Substring(0, text.Length - 10));

            Assert.That(Held.Records(file.Archive).Select(r => r.Message), Is.EqualTo(new[] { "Saved slot 1 as A.sav" }));
            LogSkippedLine skipped = file.Skipped.Single();
            Assert.That(skipped.Line, Is.EqualTo(lines.Length));
            Assert.That(skipped.Reason, Is.InstanceOf<LogSkipReason.NotJson>());
            Assert.That(skipped.ToString(), Does.StartWith($"Line {lines.Length} is not JSON"));
        }

        [Test]
        public void A_line_the_reader_cannot_place_is_skipped_with_why()
        {
            string[] lines = Write(store =>
            {
                store.Log.Saved(3, "A.sav");
                store.Log.Wrote(4);
            }).Lines;
            string savedDeclared = lines[1];
            string saved = lines[2];
            string wroteDeclared = lines[3];
            string wrote = lines[4];

            LogSessionFile file = Read(new[]
            {
                lines[0],
                saved,
                savedDeclared,
                savedDeclared.Replace("Saved slot {Slot} as {File}", "Saved slot {Slot} as {Missing}"),
                saved.Replace("\"seq\":1,", "\"seq\":7,").Replace("\"Slot\":3", "\"Slot\":\"many\""),
                saved,
                saved,
                saved.Replace("\"seq\":1,", "\"seq\":8,"),
                wroteDeclared.Replace("Wrote {Bytes} bytes", "Wrote {Missing} bytes"),
                wrote,
                "[1,2]",
                "{\"what\":1}",
            });

            Assert.That(Held.Records(file.Archive).Select(r => r.Sequence), Is.EqualTo(new[] { 1L, 8L }));
            Assert.That(file.Skipped.Select(s => (s.Line, s.Reason.GetType())), Is.EqualTo(new[]
            {
                (2, typeof(LogSkipReason.Undeclared)),
                (4, typeof(LogSkipReason.DeclaredTwice)),
                (5, typeof(LogSkipReason.NotItsDeclaration)),
                (7, typeof(LogSkipReason.SequenceTwice)),
                (9, typeof(LogSkipReason.NotAnEvent)),
                (10, typeof(LogSkipReason.Undeclared)),
                (11, typeof(LogSkipReason.NotALine)),
                (12, typeof(LogSkipReason.NotALine)),
            }));
            Assert.That(file.Skipped[0].ToString(), Is.EqualTo("Line 2 is a record of tests.save/Saved as event 1, which the file does not declare before it"));
            Assert.That(file.Skipped[1].ToString(), Is.EqualTo("Line 4 declares tests.save/Saved as event 1, which a line before it declares, and the first declaration is kept"));
            Assert.That(file.Skipped[2].ToString(), Is.EqualTo("Line 5 is a record of tests.save/Saved that is not as its declaration says: its field Slot is not of type int"));
            Assert.That(file.Skipped[3].ToString(), Is.EqualTo("Line 7 is a second record numbered 1, and the first is kept"));
            Assert.That(file.Skipped[4].ToString(), Is.EqualTo(
                "Line 9 declares tests.save/Wrote, which is not an event: its template has a hole {Missing}, and it has no field of that name"));
            Assert.That(file.Skipped[5].Reason, Is.EqualTo(new LogSkipReason.Undeclared("tests.save/Wrote", 2)));
        }

        [Test]
        public void A_declaration_not_as_the_format_writes_one_is_skipped_with_why()
        {
            string[] lines = Write(store => store.Log.Saved(3, "A.sav")).Lines;
            string declared = lines[1];

            LogSessionFile file = Read(new[]
            {
                lines[0],
                declared.Replace("\"kind\":\"Event\"", "\"kind\":\"Thing\""),
                declared.Replace("\"kind\":\"Int32\"", "\"kind\":\"Integer\""),
                declared.Replace("tests.save/Saved", "Saved"),
                declared.Replace("\"lvl\":\"Information\"", "\"lvl\":\"Loud\""),
            });

            Assert.That(file.Skipped.Select(s => s.Reason), Is.EqualTo(new LogSkipReason[]
            {
                new LogSkipReason.NotADeclaration("tests.save/Saved", "its kind is not Event or Scope"),
                new LogSkipReason.NotADeclaration("tests.save/Saved", "the field Slot is of kind Integer, which is not a kind of value"),
                new LogSkipReason.NotADeclaration("Saved", "its key has no '/' between its category and its name"),
                new LogSkipReason.NotADeclaration("tests.save/Saved", "its level is not a level"),
            }));
            Assert.That(file.Skipped[0].ToString(), Is.EqualTo(
                "Line 2 declares tests.save/Saved as the format does not write a declaration: its kind is not Event or Scope"));
            Assert.That(file.Archive.Events, Is.Empty);
        }

        [Test]
        public void A_field_no_program_could_declare_makes_its_declaration_one_of_what_is_not_an_event()
        {
            string[] lines = Write(store => store.Log.Saved(3, "A.sav")).Lines;

            LogSessionFile file = Read(new[] { lines[0], lines[1].Replace("\"name\":\"Slot\"", "\"name\":\"1Slot\"") });

            Assert.That(file.Skipped.Select(s => s.Reason), Is.EqualTo(new LogSkipReason[]
            {
                new LogSkipReason.NotAnEvent("tests.save/Saved",
                    "the field '1Slot' is not named as a field is, with a letter or '_' followed by letters, digits and '_'"),
            }));
        }

        [Test]
        public void A_files_events_are_its_own_and_neither_the_catalog_nor_a_store_takes_them()
        {
            LogEventSpec read = Read(Write(store => store.Log.Saved(3, "A.sav")).Lines).Archive.Events.Single();
            using var captured = new Captured();

            Assert.That(captured.Log.IsEnabled(read), Is.False, "A store captures only the events this program declares.");
            Assert.That(() => { captured.Log.Write(read, LogLevel.Information, 0, "", 0); },
                Throws.ArgumentException.With.Message.Contains("tests.save/Saved was read back from a file"));
            Assert.That(() => LogCatalog.Register(read), Throws.ArgumentException.With.Message.Contains("tests.save/Saved was read back from a file"));
            Assert.That(read.Index, Is.EqualTo(-1));
            Assert.That(LogCatalog.Find("tests.save/Saved").ValueUnsafe(), Is.SameAs(SaveLog.Events.Saved));
        }

        [Test]
        public void A_record_at_another_level_than_its_event_declares_is_skipped()
        {
            string[] lines = Write(store =>
            {
                store.Log.Saved(3, "A.sav");
                store.Log.Saved(4, "B.sav");
            }).Lines;

            LogSessionFile file = Read(new[] { lines[0], lines[1], lines[2].Replace("\"lvl\":\"Information\"", "\"lvl\":\"Error\""), lines[3] });

            Assert.That(Held.Records(file.Archive).Select(r => r.Sequence), Is.EqualTo(new[] { 2L }));
            Assert.That(file.Skipped.Single().Reason, Is.EqualTo(new LogSkipReason.NotItsDeclaration("tests.save/Saved",
                "it is at Error, and its declaration says every record of it is at Information")));
        }

        [Test]
        public void Two_declarations_of_one_key_are_read_apart_by_their_numbers()
        {
            string[] lines = Write(store => store.Log.Saved(3, "A.sav")).Lines;
            string declared = lines[1];
            string saved = lines[2];
            string other = declared.Replace("\"id\":1", "\"id\":2").Replace("Saved slot {Slot} as {File}", "Kept {File} in slot {Slot}");

            LogSessionFile file = Read(new[]
            {
                lines[0],
                declared,
                other,
                saved,
                saved.Replace("\"seq\":1,", "\"seq\":2,").Replace("\"id\":1", "\"id\":2"),
                saved.Replace("\"seq\":1,", "\"seq\":3,").Replace("\"id\":1", "\"id\":3"),
                saved.Replace("\"seq\":1,", "\"seq\":4,").Replace("tests.save/Saved", "tests.save/Wrote"),
            });

            Assert.That(Held.Records(file.Archive).Select(r => r.Message), Is.EqualTo(new[] { "Saved slot 3 as A.sav", "Kept A.sav in slot 3" }));
            Assert.That(file.Archive.Events.Select(e => e.Key), Is.EqualTo(new[] { "tests.save/Saved", "tests.save/Saved" }));
            Assert.That(file.Skipped, Is.EqualTo(new[]
            {
                new LogSkippedLine(6, new LogSkipReason.Undeclared("tests.save/Saved", 3)),
                new LogSkippedLine(7, new LogSkipReason.NotItsDeclaration("tests.save/Wrote", "the declaration it names, event 1, declares tests.save/Saved")),
            }));
        }

        [Test]
        public void A_declaration_reads_back_as_the_spec_it_declares()
        {
            (LogStore store, string[] lines) = Write(store =>
            {
                store.Log.Numbers(true, 'x', -5, 250, -30_000, 60_000, -2, 4_000_000_000u, long.MinValue, ulong.MaxValue);
                store.Log.Reals(1.25f, -0.1, -7m);
                store.Log.Times(s_id, s_when, s_at, TimeSpan.FromSeconds(1.5));
                store.Log.Things(Doors.Front | Doors.Back, "text", "chars", Vector3.one);
                store.Log.Optionals(3, 4, "note", Vector3.one);
                store.Log.Aligned("left", 1, 0.25, Doors.Side);
                store.Log.SignedIn("ann@example.com", "C:/Saves");
                store.Log.Lost("ann@example.com", Thrown());
                store.Log.Heard(LogLevel.Warning, "the disk is nearly full");
                store.Log.Hit(1);
                using (LogScope saving = store.Log.Saving(1))
                    saving.Log.Step(2);
                store.Log.Failed(1, Thrown());
            });

            LogSessionFile file = Read(lines);

            Assert.That(file.Skipped, Is.Empty);
            LogEventSpec[] written = Held.Records(store).Select(r => r.Event).Distinct().ToArray();
            Assert.That(file.Archive.Events.Count, Is.EqualTo(written.Length));
            for (int i = 0; i < written.Length; i++)
            {
                LogEventSpec was = written[i];
                LogEventSpec read = file.Archive.Events[i];
                Assert.That((read.Category, read.Name, read.Kind, read.Level, read.Template.Text, read.PerSecond, read.Source),
                    Is.EqualTo((was.Category, was.Name, was.Kind, was.Level, was.Template.Text, was.PerSecond, was.Source)));
                Assert.That(read.Fields.Select(f => (f.Name, f.Kind, f.Type, f.Tag, f.Personal, f.IsOptional)),
                    Is.EqualTo(was.Fields.Select(f => (f.Name, f.Kind is LogValueKind.Chars or LogValueKind.Formattable ? LogValueKind.String : f.Kind, f.Type, f.Tag, f.Personal, f.IsOptional))));
            }
        }

        [Test]
        public void A_record_of_a_backlog_reads_back_as_a_backlog()
        {
            string[] lines = Write(sink: new LogSinkOptions(LogLevel.Warning, Backlog: 5), write: store =>
            {
                store.Log.Wrote(1);
                store.Log.Failed(1, Thrown());
            }).Lines;

            LogArchive archive = Read(lines).Archive;

            var batch = new LogBatch();
            archive.Read(archive.First, batch);
            Assert.That(Enumerable.Range(0, batch.Count).Select(i => batch[i].IsBacklog), Is.EqualTo(new[] { true, false }));
            var view = new LogView(archive, LogFilter.All);
            view.Update();
            var memory = new MemorySink();
            view.Write(memory, 0, view.Count);
            Assert.That(memory.Records.Select(r => r.Backlog), Is.EqualTo(new[] { true, false }));
        }

        [TestCase(4, "\"seq\":2", "\"seq\":0", "seq")]
        [TestCase(4, "\"scope\":1", "\"scope\":2", "scope")]
        [TestCase(4, "\"scope\":1", "\"scope\":0", "scope")]
        [TestCase(4, "\"frame\":1", "\"frame\":-1", "frame")]
        [TestCase(4, "\"frame\":1", "\"frame\":1.5", "frame")]
        [TestCase(4, "\"thread\":", "\"thread\":-", "thread")]
        [TestCase(4, "\"thread\":", "\"thread\":9999999999", "thread")]
        [TestCase(4, "\"lvl\"", "\"suppressed\":-1,\"lvl\"", "suppressed")]
        [TestCase(4, "\"file\":", "\"file\":1,\"was\":", "file")]
        [TestCase(4, "\"line\":", "\"line\":-", "line")]
        [TestCase(2, "\"begin\":1", "\"begin\":2", "begin")]
        [TestCase(5, "\"end\":1", "\"end\":3", "end")]
        [TestCase(5, "\"end\":1", "\"end\":0", "end")]
        [TestCase(5, "\"ms\":40", "\"ms\":-40", "ms")]
        [TestCase(5, "\"ms\":40", "\"ms\":1e300", "ms")]
        [TestCase(5, "\"ms\":40", "\"ms\":\"40\"", "ms")]
        public void A_record_with_a_stamp_no_record_can_have_is_skipped(int line, string stamp, string wrong, string named)
        {
            var clock = new TestClock();
            string[] lines = Write(clock: clock, write: store =>
            {
                store.EnterFrame(1);
                using LogScope saving = store.Log.Saving(1);
                saving.Log.Saved(1, "A.sav");
                clock.Advance(TimeSpan.FromMilliseconds(40));
            }).Lines;
            Assert.That(lines[line], Does.Contain(stamp));
            string[] changed = lines.ToArray();
            changed[line] = lines[line].Replace(stamp, wrong);

            LogSessionFile file = Read(changed);

            Assert.That(file.Skipped, Is.EqualTo(new[] { new LogSkippedLine(line + 1, new LogSkipReason.NotAStamp(named)) }));
            Assert.That(file.Skipped[0].ToString(), Is.EqualTo($"Line {line + 1} is a record whose {named} is not one a record can have"));
            Assert.That(Held.Records(file.Archive), Has.Length.EqualTo(2));
        }

        [Test]
        public void A_loss_no_record_follows_is_said_where_its_line_is()
        {
            string[] lines = Write(store => store.Log.Step(1)).Lines;

            LogSessionFile file = Read(new[] { lines[0], lines[1], lines[2], "{\"lost\":3}", "[1]" });

            Assert.That(file.Skipped, Is.EqualTo(new[]
            {
                new LogSkippedLine(4, new LogSkipReason.LostAtEnd(3)),
                new LogSkippedLine(5, new LogSkipReason.NotALine()),
            }));
            Assert.That(file.Skipped[0].ToString(), Is.EqualTo(
                "Line 4 says 3 records were lost after the last record, and no record follows to place the loss before"));

            LogSessionFile followed = Read(new[] { lines[0], lines[1], lines[2], "{\"lost\":3}", "[1]", lines[2].Replace("\"seq\":1,", "\"seq\":5,") });

            Assert.That(followed.Skipped, Is.EqualTo(new[] { new LogSkippedLine(5, new LogSkipReason.NotALine()) }));
            Assert.That(followed.Archive.Read(2, new LogBatch()), Is.EqualTo(new LogRead(5, 1, 6, 3)));
        }

        [Test]
        public void A_loss_is_read_where_the_file_says_it_was()
        {
            string[] lines = Write(store =>
            {
                store.Log.Step(1);
                store.Log.Step(2);
                store.Log.Step(3);
            }).Lines;

            LogSessionFile file = Read(new[] { lines[0], "{\"lost\":4}", lines[1], lines[2], "{\"lost\":2}", "{\"lost\":1}", lines[3], lines[4], "{\"lost\":5}" });

            var batch = new LogBatch();
            Assert.That(file.Archive.Read(0, batch), Is.EqualTo(new LogRead(1, 1, 2, 4)));
            Assert.That(file.Archive.Read(2, batch), Is.EqualTo(new LogRead(2, 2, 4, 3)));
            Assert.That(file.Archive.Read(4, batch), Is.EqualTo(new LogRead(4, 0, 4, 0)));
            Assert.That(file.Archive.Read(3, batch), Is.EqualTo(new LogRead(3, 1, 4, 0)));
            Assert.That(file.Skipped, Is.EqualTo(new[] { new LogSkippedLine(9, new LogSkipReason.LostAtEnd(5)) }));
            Assert.That(file.Archive.Dropped, Is.EqualTo(0));
        }

        [Test]
        public void A_loss_the_sink_suffered_reads_back_before_the_record_after_it()
        {
            string[] lines = Write(options: Stores.Tiny, write: store =>
            {
                for (int i = 0; i < 20; i++)
                    store.Log.Step(i);
            }).Lines;

            LogArchive archive = Read(lines).Archive;

            var batch = new LogBatch();
            LogRead read = archive.Read(1, batch);
            Assert.That((read.First, read.Lost), Is.EqualTo((5L, 4L)));
            Assert.That(Enumerable.Range(0, 16).Select(i => batch[i].Field(0).AsInt64()), Is.EqualTo(Enumerable.Range(4, 16).Select(i => (long)i)));
        }

        [Test]
        public void A_record_is_read_by_sequence()
        {
            string[] lines = Write(store =>
            {
                for (int i = 0; i < 5; i++)
                    store.Log.Step(i);
            }).Lines;
            LogArchive archive = Read(lines).Archive;
            var batch = new LogBatch();

            Assert.That(archive.Read(new long[] { 4, 9, 1, 2 }, batch), Is.EqualTo(3));

            Assert.That(Enumerable.Range(0, batch.Count).Select(i => batch[i].Sequence), Is.EqualTo(new[] { 4L, 1L, 2L }));
        }

        [Test]
        public void A_file_of_another_format_is_refused()
        {
            Error error = Refused("{\"format\":2,\"program\":\"Tests\",\"started\":\"2026-10-29T10:24:09.0000000+01:00\"}\n");

            Assert.That(error.Code, Is.EqualTo((int)LogFileReadError.UnknownFormat));
            Assert.That(error.Message, Is.EqualTo("test.jsonl is a log of format 2, and this reader reads format 1"));
        }

        [TestCase("{\"format\":4}\n", "4")]
        [TestCase("{\"format\":2,\"program\":7}\n", "2")]
        [TestCase("{\"format\":99999999999}\n", "99999999999")]
        public void A_header_is_read_for_its_format_first(string text, string format)
        {
            Error error = Refused(text);

            Assert.That(error.Code, Is.EqualTo((int)LogFileReadError.UnknownFormat));
            Assert.That(error.Message, Does.Contain($" format {format},"));
        }

        [TestCase("")]
        [TestCase("hello\n")]
        [TestCase("[1]\n")]
        [TestCase("{\"format\":1,\"program\":\"Tests\"}\n")]
        [TestCase("{\"format\":\"1\",\"program\":\"Tests\",\"started\":\"2026-10-29T10:24:09.0000000+01:00\"}\n")]
        [TestCase("{\"program\":\"Tests\",\"started\":\"2026-10-29T10:24:09.0000000+01:00\"}\n")]
        [TestCase("{\"format\":1,\"program\":\"Tests\",\"started\":\"yesterday\"}\n")]
        public void A_file_that_is_not_a_log_is_said(string text)
        {
            Error error = Refused(text);

            Assert.That(error.Code, Is.EqualTo((int)LogFileReadError.NotALog));
            Assert.That(error.Message, Does.StartWith("test.jsonl is not a log as JsonLinesSink writes one"));
        }

        [Test]
        public void A_first_line_longer_than_any_header_is_not_a_log()
        {
            string header = "{\"format\":1,\"program\":\"" + new string('a', 64 * 1024) + "\",\"started\":\"2026-10-29T10:24:09.0000000+01:00\"}";

            Error error = Refused(header + "\n");

            Assert.That(error.Code, Is.EqualTo((int)LogFileReadError.NotALog));
        }

        [TestCase("\r\n")]
        [TestCase("\r")]
        [TestCase("")]
        public void A_header_ends_where_a_line_does(string end)
        {
            string[] lines = Write(store => store.Log.Saved(3, "A.sav")).Lines;

            LogSessionFile file = Read(lines[0] + end + (end.Length > 0 ? string.Join("\n", lines, 1, lines.Length - 1) + "\n" : ""));

            Assert.That(file.Skipped, Is.Empty);
            Assert.That(Held.Records(file.Archive).Select(r => r.Message), Is.EqualTo(end.Length > 0 ? new[] { "Saved slot 3 as A.sav" } : Array.Empty<string>()));
        }

        [Test]
        public void A_files_archive_takes_no_record_after_the_files()
        {
            (LogStore written, string[] lines) = Write(store => store.Log.Saved(3, "A.sav"));
            LogArchive archive = Read(lines).Archive;
            LogEventSpec[] declared = archive.Events.ToArray();
            LogBatch records = All(written);

            Assert.That(() => archive.Keep(records[0], backlog: false),
                Throws.InvalidOperationException.With.Message.EqualTo(
                    "The archive is sealed: it holds what its file held, or what its session kept before it ended, and takes no record after."));
            Assert.That(() => Step(archive, 9), Throws.InvalidOperationException);
            Assert.That(() => archive.Lose(1), Throws.InvalidOperationException);
            // The file's own specs, which are not the program's, though they have its keys.
            Assert.That(archive.Events, Is.EqualTo(declared), "Refusing changed nothing.");
            Assert.That(declared.Select(e => e.Key), Is.EqualTo(new[] { SaveLog.Events.Saved.Key }));
            Assert.That(declared, Has.None.SameAs(SaveLog.Events.Saved));
            Assert.That(archive.Count, Is.EqualTo(1));
        }

        [Test]
        public void A_file_that_cannot_be_opened_is_said()
        {
            string path = Path.Combine(_folder, "missing.jsonl");

            Error error = LogFile.Read(path).Match(refused => refused, _ => throw new InvalidOperationException("It read."));

            Assert.That(error.Code, Is.EqualTo((int)LogFileReadError.Unreadable));
            Assert.That(error.Message, Does.StartWith($"The log {path} could not be read: "));
        }

        [Test]
        public void A_sessions_file_is_read_while_the_session_writes_it()
        {
            LogStore store = Stores.Everything();
            JsonLinesSink sink = LogFile.Session(store, _folder).ValueUnsafe();
            using var drain = new LogDrain(store, (sink, new LogSinkOptions(LogLevel.Trace)));
            store.Log.Saved(3, "A.sav");
            drain.Pump();

            LogSessionFile file = LogFile.Read(sink.Name).Match(error => throw new InvalidOperationException(error.Message), read => read);

            Assert.That(file.Path, Is.EqualTo(sink.Name));
            Assert.That(Held.Records(file.Archive).Select(r => r.Message), Is.EqualTo(new[] { "Saved slot 3 as A.sav" }));
        }

        [Test]
        public void A_view_reads_an_archive_as_it_reads_a_store()
        {
            string[] lines = Write(store =>
            {
                using (LogScope saving = store.Log.Saving(1))
                {
                    saving.Log.Heard(LogLevel.Warning, "the disk is nearly full");
                    saving.Log.Heard(LogLevel.Warning, "the disk is nearly full");
                }

                store.Log.Saved(1, "A.sav");
            }).Lines;
            var view = new LogView(Read(lines).Archive, LogFilter.Parse("level>=warning").ValueUnsafe());

            view.Update();

            Assert.That(Enumerable.Range(0, view.Count).Select(i => view[i]).Single(),
                Is.EqualTo(new LogRow(2, 3, 2, 0, LogLevel.Warning, LogRowKind.Event, 0, 0)));
            Assert.That(view.Scopes.IsUnder(1, 1), Is.True);
            Assert.That(view.Scopes.Elapsed(1).IsSome, Is.True);
            Assert.That(view.Counts, Is.EqualTo(new LogLevelCounts(0, 0, 2, 2, 0, 0)));
        }

        [Test]
        public void An_archive_holds_at_least_one_record()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new LogArchive("Tests", default, cap: 0));
        }

        [Test]
        public void Keeping_a_record_learns_its_event_once_and_says_whether_it_was_a_backlog()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(1, "A.sav");
            store.Log.Step(2);
            store.Log.Saved(3, "C.sav");
            LogBatch records = All(store);
            var archive = new LogArchive(store.Program, store.Clock, cap: 100);

            for (int i = 0; i < records.Count; i++)
                Assert.That(archive.Keep(records[i], backlog: i == 1), Is.True);

            Assert.That(archive.Events, Is.EqualTo(new[] { SaveLog.Events.Saved, SaveLog.Events.Step }));
            var batch = new LogBatch();
            Assert.That(archive.Read(1, batch), Is.EqualTo(new LogRead(1, 3, 4, 0)));
            Assert.That(Enumerable.Range(0, batch.Count).Select(i => batch[i].IsBacklog), Is.EqualTo(new[] { false, true, false }));
            Assert.That(Enumerable.Range(0, batch.Count).Select(i => batch[i].ToString()), Is.EqualTo(new[] { "Saved slot 1 as A.sav", "Step 2", "Saved slot 3 as C.sav" }));
        }

        [Test]
        public void An_archive_lets_its_oldest_go_past_its_cap_and_does_not_call_them_lost()
        {
            var archive = new LogArchive("Tests", default, cap: 4);

            for (long sequence = 1; sequence <= 6; sequence++)
                Assert.That(Step(archive, sequence), Is.True);

            Assert.That((archive.Count, archive.Dropped, archive.First, archive.Next), Is.EqualTo((4, 2L, 3L, 7L)));
            Assert.That(archive.Read(1, new LogBatch()), Is.EqualTo(new LogRead(3, 4, 7, 0)));
            Assert.That(Step(archive, 1), Is.False, "A record older than what it let go is let go too.");
            Assert.That(Step(archive, 5), Is.False, "It holds each sequence once.");
            Assert.That((archive.Count, archive.LateMark.Placed), Is.EqualTo((4, 0L)));
        }

        [Test]
        public void A_record_handed_late_is_placed_by_its_sequence()
        {
            // A cap of 100 holds blocks of 25, so the late records split the blocks they fall in.
            var archive = new LogArchive("Tests", default, cap: 100);
            for (long sequence = 1; sequence < 100; sequence += 2)
                Step(archive, sequence);
            Step(archive, 100);
            for (long sequence = 98; sequence >= 2; sequence -= 2)
                Step(archive, sequence);

            var batch = new LogBatch();
            Assert.That(archive.Read(1, batch), Is.EqualTo(new LogRead(1, 100, 101, 0)));
            Assert.That(Enumerable.Range(0, batch.Count).Select(i => batch[i].Sequence), Is.EqualTo(Enumerable.Range(1, 100).Select(i => (long)i)));
            Assert.That(Enumerable.Range(0, batch.Count).Select(i => batch[i].Field(0).AsInt64()), Is.EqualTo(Enumerable.Range(1, 100).Select(i => (long)i)),
                "Each record keeps its own values when it is moved.");
            Assert.That((archive.Count, archive.LateMark.Placed, archive.Dropped), Is.EqualTo((100, 49L, 0L)));
            Assert.That(archive.LateFrom(new LogLateMark(archive, 0)).From, Is.EqualTo(2));
            Assert.That(archive.LateFrom(new LogLateMark(archive, 48)).From, Is.EqualTo(2));
            Assert.That(archive.LateFrom(new LogLateMark(archive, 47)).From, Is.EqualTo(2));
            Assert.That(archive.LateFrom(new LogLateMark(archive, 49)).From, Is.EqualTo(101), "None was placed since.");
            LogLate late = archive.LateFrom(default);
            Assert.That(late, Is.EqualTo(new LogLate(2, archive.LateMark)), "It says the mark to pass next time.");
            Assert.That(archive.LateFrom(late.Mark).From, Is.EqualTo(archive.Next));
            Assert.That(archive.LateFrom(new LogArchive("Tests", default, cap: 100).LateMark), Is.EqualTo(late), "A mark of other records has seen none of these.");
        }

        [Test]
        public void An_archive_of_blocks_of_one_places_a_late_record_in_a_block_of_its_own()
        {
            var archive = new LogArchive("Tests", default, cap: 3);
            Step(archive, 5);
            Step(archive, 3);
            Step(archive, 4);

            var batch = new LogBatch();
            Assert.That(archive.Read(0, batch), Is.EqualTo(new LogRead(3, 3, 6, 0)));
            Assert.That(Enumerable.Range(0, batch.Count).Select(i => batch[i].Sequence), Is.EqualTo(new[] { 3L, 4L, 5L }));
            Assert.That(archive.LateFrom(new LogLateMark(archive, 0)).From, Is.EqualTo(3));

            Step(archive, 6);
            Assert.That((archive.First, archive.Dropped), Is.EqualTo((4L, 1L)));
        }

        [Test]
        public void Late_records_past_what_it_remembers_are_said_from_its_first()
        {
            var archive = new LogArchive("Tests", default, cap: 10_000);
            for (long sequence = 1; sequence <= 10; sequence++)
                Step(archive, sequence);
            Step(archive, 5000);
            for (long sequence = 3000; sequence < 4100; sequence++)
                Step(archive, sequence);

            Assert.That(archive.LateMark.Placed, Is.EqualTo(1100));
            Assert.That(archive.LateFrom(new LogLateMark(archive, 0)).From, Is.EqualTo(1), "More were placed since than it remembers the sequences of.");
            Assert.That(archive.LateFrom(new LogLateMark(archive, 1090)).From, Is.EqualTo(4090));
            Assert.That(archive.LateFrom(new LogLateMark(archive, 1100)).From, Is.EqualTo(5001));
        }

        [Test]
        public void A_loss_is_carried_by_the_next_record_in_order()
        {
            var archive = new LogArchive("Tests", default, cap: 100);
            Step(archive, 1);
            archive.Lose(2);
            Assert.That(archive.PendingLost, Is.EqualTo(2));

            Step(archive, 4);
            Assert.That(archive.PendingLost, Is.EqualTo(0));
            Step(archive, 3);

            var batch = new LogBatch();
            Assert.That(archive.Read(1, batch), Is.EqualTo(new LogRead(1, 2, 4, 0)), "A record placed late carries no loss.");
            Assert.That(archive.Read(4, batch), Is.EqualTo(new LogRead(4, 1, 5, 2)));
            Assert.Throws<ArgumentOutOfRangeException>(() => archive.Lose(-1));
        }

        [Test]
        public void Clearing_an_archive_lets_every_record_go_and_it_takes_none_older()
        {
            var archive = new LogArchive("Tests", default, cap: 4);
            for (long sequence = 1; sequence <= 6; sequence++)
                Step(archive, sequence);
            archive.Lose(3);

            archive.Clear();

            Assert.That((archive.Count, archive.Dropped, archive.PendingLost, archive.First, archive.Next), Is.EqualTo((0, 0L, 0L, 7L, 7L)),
                "It stops where it was; what it dropped, and the loss, were before the clear.");
            Assert.That(archive.CanKeep(6), Is.False);
            Assert.That(Step(archive, 6), Is.False, "A record older than the clear went with it.");
            Assert.That(Step(archive, 8), Is.True);
            Assert.That(Step(archive, 7), Is.True, "A record after the clear, handed late, is placed.");
            var batch = new LogBatch();
            Assert.That(archive.Read(1, batch), Is.EqualTo(new LogRead(7, 2, 9, 0)));
            Assert.That(archive.LateMark.Placed, Is.EqualTo(1));
        }

        [Test]
        public void A_full_archive_does_not_keep_a_record_older_than_all_it_holds()
        {
            var archive = new LogArchive("Tests", default, cap: 4);
            foreach (long sequence in new long[] { 2, 4, 6, 8 })
                Step(archive, sequence);

            Assert.That(archive.CanKeep(1), Is.False, "Its cap would let it go first, with the newer records of its block.");
            Assert.That(Step(archive, 1), Is.False);
            Assert.That(archive.CanKeep(3), Is.True);
            Assert.That(archive.CanKeep(4), Is.False, "It holds it.");
            Assert.That((archive.Count, archive.Dropped), Is.EqualTo((4, 0L)));
        }

        [Test]
        public void A_sealed_archive_keeps_nothing_and_is_not_cleared()
        {
            var archive = new LogArchive("Tests", default, cap: 100);
            Step(archive, 1);

            archive.Seal();

            Assert.That(archive.CanKeep(2), Is.False);
            Assert.That(() => archive.Clear(), Throws.InvalidOperationException);
            Assert.That((archive.Count, archive.Capacity), Is.EqualTo((1, 1)));
        }

        private static InvalidDataException Thrown()
        {
            try
            {
                throw new InvalidDataException("No buffer.");
            }
            catch (InvalidDataException exception)
            {
                return exception;
            }
        }

        /// <summary>The store <paramref name="write"/> logs to, and the lines a JSON lines sink writes of it.</summary>
        private static (LogStore Store, string[] Lines) Write(Action<LogStore> write, LogSinkOptions? sink = null, LogStoreOptions? options = null, TestClock? clock = null)
        {
            var stream = new MemoryStream();
            LogStore store = Stores.Everything(options, clock);
            using (new LogDrain(store, (new JsonLinesSink(stream, leaveOpen: true), sink ?? new LogSinkOptions(LogLevel.Trace))))
                write(store);

            string text = Encoding.UTF8.GetString(stream.ToArray());
            return (store, text.Substring(0, text.Length - 1).Split('\n'));
        }

        private static LogSessionFile Read(IEnumerable<string> lines) => Read(string.Join("\n", lines) + "\n");

        private static LogSessionFile Read(string text) =>
            LogFile.Read(new MemoryStream(Encoding.UTF8.GetBytes(text)), "test.jsonl").Match(error => throw new InvalidOperationException(error.Message), file => file);

        private static Error Refused(string text) =>
            LogFile.Read(new MemoryStream(Encoding.UTF8.GetBytes(text)), "test.jsonl").Match(error => error, _ => throw new InvalidOperationException("It read."));

        /// <summary>Every record the store holds, in one batch.</summary>
        private static LogBatch All(LogStore store)
        {
            var batch = new LogBatch();
            store.Read(store.First, batch);
            return batch;
        }

        /// <summary>Hands <paramref name="archive"/> a step of the value <paramref name="sequence"/> under that sequence, as a file's reader does: whether it kept it.</summary>
        private static bool Step(LogArchive archive, long sequence)
        {
            var entry = new LogEntry
            {
                Sequence = sequence,
                Event = archive.Declare(SaveLog.Events.Step),
                Level = LogLevel.Trace,
                Kind = LogRecordKind.Event,
                Bits = 1,
            };
            return archive.Add(entry, new[] { sequence }, Array.Empty<object?>());
        }
    }
}
