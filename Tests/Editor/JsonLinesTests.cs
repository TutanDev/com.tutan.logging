using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Tutan.Functional;
using UnityEngine;

namespace Tutan.Logging.Tests
{
    public class JsonLinesTests
    {
        [Test]
        public void A_line_escapes_what_json_requires_and_nothing_else()
        {
            var json = new JsonLine();

            json.Begin();
            json.Name("text");
            json.String("a\"b\\c\n\r\t\b\f\u0001\u001fé✓");

            Assert.That(json.ToString(), Is.EqualTo("{\"text\":\"a\\\"b\\\\c\\n\\r\\t\\b\\f\\u0001\\u001fé✓\""));
        }

        [Test]
        public void A_line_separates_names_values_arrays_and_objects()
        {
            var json = new JsonLine();

            json.Begin();
            json.Name("a");
            json.StartArray();
            json.Number(1L);
            json.Number(2L);
            json.StartObject();
            json.EndObject();
            json.EndArray();
            json.Name("b");
            json.StartObject();
            json.Name("c");
            json.Null();
            json.Name("d");
            json.Bool(false);
            json.EndObject();
            using var stream = new MemoryStream();
            json.End(stream);

            Assert.That(Encoding.UTF8.GetString(stream.ToArray()), Is.EqualTo("{\"a\":[1,2,{}],\"b\":{\"c\":null,\"d\":false}}\n"));
        }

        [Test]
        public void Numbers_are_written_in_the_invariant_culture_as_the_shortest_text_that_reads_back()
        {
            var json = new JsonLine();

            json.Begin();
            json.Name("n");
            json.StartArray();
            json.Number(long.MinValue);
            json.Number(ulong.MaxValue);
            json.Number(0.1f);
            json.Number(0.1);
            json.Number(1e300);
            json.Number(-1.5m);
            json.EndArray();

            Assert.That(json.ToString(), Is.EqualTo("{\"n\":[-9223372036854775808,18446744073709551615,0.1,0.1,1E+300,-1.5]"));
        }

        [Test]
        public void A_number_that_is_not_finite_is_written_as_a_string()
        {
            var json = new JsonLine();

            json.Begin();
            json.Name("n");
            json.StartArray();
            json.Number(double.NaN);
            json.Number(double.PositiveInfinity);
            json.Number(float.NegativeInfinity);
            json.EndArray();

            Assert.That(json.ToString(), Is.EqualTo("{\"n\":[\"NaN\",\"Infinity\",\"-Infinity\"]"));
        }

        [Test]
        public void A_time_is_written_as_iso_8601_with_its_offset()
        {
            var json = new JsonLine();

            json.Begin();
            json.Name("t");
            json.Time(new DateTimeOffset(2026, 11, 1, 10, 24, 9, TimeSpan.FromHours(-5)));

            Assert.That(json.ToString(), Is.EqualTo("{\"t\":\"2026-11-01T10:24:09.0000000-05:00\""));
        }

        [Test]
        public void A_long_line_grows_its_buffers_and_is_written_as_utf8_without_a_bom()
        {
            var json = new JsonLine();
            string text = string.Concat(Enumerable.Repeat("é✓", 5000));

            json.Begin();
            json.Name("text");
            json.String(text);
            using var stream = new MemoryStream();
            json.End(stream);

            byte[] bytes = stream.ToArray();
            Assert.That(bytes[0], Is.EqualTo((byte)'{'));
            Assert.That(Encoding.UTF8.GetString(bytes), Is.EqualTo("{\"text\":\"" + text + "\"}\n"));
        }

        [Test]
        public void A_file_starts_with_its_format_and_program_and_declares_each_event_before_its_first_record()
        {
            string[] lines = Lines(log =>
            {
                log.Saved(3, "slot3.sav");
                log.Saved(4, "slot4.sav");
            });

            Assert.That(lines[0], Is.EqualTo("{\"format\":1,\"program\":\"Tests\",\"started\":\"*\"}"));
            Assert.That(Ours(lines), Is.EqualTo(new[]
            {
                "{\"event\":\"tests.save/Saved\",\"id\":1,\"kind\":\"Event\",\"lvl\":\"Information\",\"template\":\"Saved slot {Slot} as {File}\"," +
                "\"fields\":[{\"name\":\"Slot\",\"kind\":\"Int32\",\"type\":\"int\"},{\"name\":\"File\",\"kind\":\"String\",\"type\":\"string\"}]," +
                "\"source\":{\"assembly\":\"Tutan.Logging.Editor.Tests\",\"type\":\"Tutan.Logging.Tests.SaveLog\",\"file\":\"*\",\"line\":*}}",
                "{\"seq\":*,\"t\":\"*\",\"thread\":*,\"lvl\":\"Information\",\"key\":\"tests.save/Saved\",\"id\":1,\"msg\":\"Saved slot 3 as slot3.sav\"," +
                "\"file\":\"*\",\"line\":*,\"fields\":{\"Slot\":3,\"File\":\"slot3.sav\"}}",
                "{\"seq\":*,\"t\":\"*\",\"thread\":*,\"lvl\":\"Information\",\"key\":\"tests.save/Saved\",\"id\":1,\"msg\":\"Saved slot 4 as slot4.sav\"," +
                "\"file\":\"*\",\"line\":*,\"fields\":{\"Slot\":4,\"File\":\"slot4.sav\"}}",
            }));
        }

        [Test]
        public void A_scopes_opening_closing_and_what_is_under_it_say_so()
        {
            var clock = new TestClock();
            string[] lines = Ours(Lines(log =>
            {
                using LogScope saving = log.Saving(1);
                saving.Log.Step(1);
                clock.Advance(TimeSpan.FromMilliseconds(250));
            }, clock: clock, raw: true));

            // The declaration of Saving, its opening, the declaration of Step, the step, the closing.
            long begin = Sequence(lines[1]);
            Assert.That(lines[1], Does.StartWith($"{{\"seq\":{begin},\"begin\":{begin},\"t\":"));
            Assert.That(lines[1], Does.Contain("\"msg\":\"Saving slot 1\""));
            Assert.That(lines[3], Does.StartWith($"{{\"seq\":{begin + 1},\"scope\":{begin},\"t\":"));
            Assert.That(lines[4], Does.StartWith($"{{\"seq\":{begin + 2},\"end\":{begin},\"t\":"));
            Assert.That(lines[4], Does.Contain("\"key\":\"tests.save/Saving\",\"id\":1,\"ms\":250,"));
            Assert.That(lines[4], Does.Not.Contain("\"msg\""));
            Assert.That(lines[4], Does.Not.Contain("\"fields\""));
        }

        [Test]
        public void Every_kind_of_field_is_written_as_a_reader_reads_it_back()
        {
            string[] lines = Ours(Lines(log =>
            {
                log.Numbers(true, 'x', -5, 200, -300, 60000, -70000, 4_000_000_000u, -5_000_000_000L, ulong.MaxValue);
                log.Reals(0.1f, double.NaN, 1.25m);
                log.Times(Guid.Empty, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), default, TimeSpan.FromSeconds(90));
                log.Things(Doors.Front | Doors.Back, "a \"quote\"", "chars", new Vector3(1, 2, 3));
                log.Optionals(F.None, null, F.None, F.None);
                log.Aligned("left", 1, 0.25, Doors.Side);
                log.Failed(1, new InvalidOperationException("The disk is full."));
            }));

            // The fields end a line, but for an error's stack after them.
            string Fields(string key) => Regex.Match(lines.Single(l => l.Contains($"\"key\":\"{key}\"")), "\"fields\":(\\{.*\\})(,\"stack\":\\[.*\\])?\\}$").Groups[1].Value;

            Assert.That(Fields("tests.fields/Numbers"), Is.EqualTo(
                "{\"Flag\":true,\"Letter\":\"x\",\"Tiny\":-5,\"Small\":200,\"Short\":-300,\"Word\":60000,\"Int\":-70000," +
                "\"Count\":4000000000,\"Long\":-5000000000,\"Huge\":18446744073709551615}"));
            Assert.That(Fields("tests.fields/Reals"), Is.EqualTo("{\"Single\":0.1,\"Double\":\"NaN\",\"Money\":1.25}"));
            Assert.That(Fields("tests.fields/Times"), Is.EqualTo(
                "{\"Id\":\"00000000-0000-0000-0000-000000000000\",\"When\":\"2026-11-01T00:00:00.0000000Z\"," +
                "\"At\":\"0001-01-01T00:00:00.0000000+00:00\",\"Took\":\"00:01:30\"}"));
            Assert.That(Fields("tests.fields/Things"), Is.EqualTo(
                $"{{\"Doors\":\"Front | Back\",\"Text\":\"a \\\"quote\\\"\",\"Chars\":\"chars\",\"Where\":\"{new Vector3(1, 2, 3).ToString(null, System.Globalization.CultureInfo.InvariantCulture)}\"}}"));
            Assert.That(Fields("tests.fields/Optionals"), Is.EqualTo("{\"Maybe\":null,\"Count\":null,\"Note\":null,\"Where\":null}"));
            Assert.That(Fields("tests.fields/Aligned"), Is.EqualTo("{\"Left\":\"left\",\"Right\":1,\"Ratio\":0.25,\"Doors\":\"Side\"}"),
                "A value is written as itself, not padded or formatted as its hole asks.");
            Assert.That(Fields("tests.save/Failed"), Is.EqualTo(
                "{\"Slot\":1,\"Error\":{\"type\":\"System.InvalidOperationException\",\"message\":\"The disk is full.\"," +
                "\"text\":\"System.InvalidOperationException: The disk is full.\"}}"));
        }

        [Test]
        public void A_declaration_says_what_is_personal_tagged_optional_limited_and_an_enums_names()
        {
            string[] lines = Ours(Lines(log =>
            {
                log.SignedIn("ann@example.com", "C:/Saves");
                log.Optionals(1, 2, "x", Vector3.zero);
                log.Things(Doors.None, "", "", Vector3.zero);
                log.Hit(1);
                log.Heard(LogLevel.Warning, "x");
            }));

            Assert.That(lines.Single(l => l.Contains("\"event\":\"tests.people/SignedIn\"")), Does.Contain(
                "\"fields\":[{\"name\":\"Email\",\"kind\":\"String\",\"type\":\"string\",\"personal\":true}," +
                "{\"name\":\"Folder\",\"kind\":\"String\",\"type\":\"string\",\"tag\":\"path\"}]"));
            Assert.That(lines.Single(l => l.Contains("\"event\":\"tests.fields/Optionals\"")), Does.Contain(
                "{\"name\":\"Maybe\",\"kind\":\"Int32\",\"type\":\"Tutan.Functional.Optional<int>\",\"optional\":true}"));
            Assert.That(lines.Single(l => l.Contains("\"event\":\"tests.fields/Things\"")), Does.Contain(
                "{\"name\":\"Doors\",\"kind\":\"Enum\",\"type\":\"Tutan.Logging.Tests.Doors\",\"names\":{\"None\":0,\"Front\":1,\"Back\":2,\"Side\":4},\"flags\":true}"));
            Assert.That(lines.Single(l => l.Contains("\"event\":\"tests.limits/Hit\"")), Does.Contain("\"perSecond\":2,"));
            Assert.That(lines.Single(l => l.Contains("\"event\":\"tests.heard/Heard\"")), Does.Not.Contain("\"lvl\""),
                "An event whose level is chosen at the call declares none.");
            Assert.That(lines.Single(l => l.Contains("\"key\":\"tests.heard/Heard\"")), Does.Contain("\"lvl\":\"Warning\""));
        }

        [Test]
        public void A_sink_that_redacts_says_so_and_writes_personal_fields_as_personal()
        {
            string[] lines = Lines(log => log.SignedIn("ann@example.com", "C:/Saves"), new LogSinkOptions(LogLevel.Trace, Redact: LogRedaction.Personal));

            Assert.That(lines[0], Does.Contain("\"redact\":[\"Personal\"]"));
            string record = lines.Single(l => l.Contains("\"key\":\"tests.people/SignedIn\""));
            Assert.That(record, Does.Contain("\"msg\":\"Signed in as [personal] from C:/Saves\""));
            Assert.That(record, Does.Contain("\"fields\":{\"Email\":\"[personal]\",\"Folder\":\"C:/Saves\"}"));
            Assert.That(string.Join("\n", lines), Does.Not.Contain("ann@example.com"));
        }

        [Test]
        public void A_records_stack_follows_its_fields_a_frame_an_object()
        {
            string[] lines = Ours(Lines(FailFromHere));

            string failed = lines.Single(l => l.Contains("\"key\":\"tests.save/Failed\""));
            Assert.That(failed, Does.Match(
                "\"fields\":\\{.*\\},\"stack\":\\[\\{\"method\":\"Tutan\\.Logging\\.Tests\\.JsonLinesTests\\.FailFromHere\\(Log\\)\",\"file\":\"\\*\",\"line\":\\*\\}" +
                "(,\\{\"method\":\"[^\"]+\"(,\"file\":\"\\*\",\"line\":\\*)?\\})*\\]\\}$"),
                "The method that made the call first, then outward; a frame without symbols has no file or line.");
            Assert.That(lines.Single(l => l.Contains("\"key\":\"tests.save/Saved\"")), Does.Not.Contain("\"stack\""), "Information is below the default stack level, error.");
        }

        [Test]
        public void A_record_written_as_an_errors_context_says_so()
        {
            string[] lines = Ours(Lines(log =>
            {
                log.Step(1);
                log.Failed(1, new InvalidOperationException("The disk is full."));
            }, new LogSinkOptions(LogLevel.Error, Backlog: 4)));

            Assert.That(lines.Single(l => l.Contains("\"key\":\"tests.save/Step\"")), Does.Contain(",\"context\":true,"));
            Assert.That(lines.Single(l => l.Contains("\"key\":\"tests.save/Failed\"")), Does.Not.Contain("\"context\""));
        }

        [Test]
        public void Records_the_sink_was_told_were_lost_are_a_line_of_their_own()
        {
            var store = new LogStore(LogRules.Parse("trace").ValueUnsafe(), new LogStoreOptions(Records: LogStore.MinRecords, Program: "Tests"));
            using var stream = new MemoryStream();
            using (var drain = new LogDrain(store, (new JsonLinesSink(stream, leaveOpen: true), new LogSinkOptions(LogLevel.Trace))))
            {
                long first = store.Next;
                for (int i = 0; i < 20; i++)
                    store.Log.Step(i);
                drain.Pump();

                // The drain was made reading from the store's first sequence, 1, and the ring kept the latest steps.
                long lost = first - 1 + 20 - LogStore.MinRecords;
                Assert.That(Encoding.UTF8.GetString(stream.ToArray()).Split('\n'), Has.Member($"{{\"lost\":{lost}}}"));
            }
        }

        [Test]
        public void Disposing_the_sink_closes_its_stream_unless_it_was_to_be_left_open()
        {
            var closed = new MemoryStream();
            var open = new MemoryStream();

            new JsonLinesSink(closed).Dispose();
            new JsonLinesSink(open, leaveOpen: true).Dispose();

            Assert.That(closed.CanWrite, Is.False);
            Assert.That(open.CanWrite, Is.True);
        }

        /// <summary>The lines a JSON sink writes of what <paramref name="write"/> logs, with what changes from run to run as <c>*</c>.</summary>
        private static string[] Lines(Action<Log> write, LogSinkOptions? options = null, TestClock? clock = null, bool raw = false)
        {
            var store = new LogStore(LogRules.Parse("trace").ValueUnsafe(), new LogStoreOptions(Program: "Tests"), clock is null ? (Func<long>?)null : clock.Read);
            using var stream = new MemoryStream();
            using (var drain = new LogDrain(store, (new JsonLinesSink(stream, leaveOpen: true), options ?? new LogSinkOptions(LogLevel.Trace))))
            {
                write(store.Log);
            }

            string[] lines = Encoding.UTF8.GetString(stream.ToArray()).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Select(line =>
            {
                line = Regex.Replace(line, "\"(started|t|file)\":\"[^\"]*\"", "\"$1\":\"*\"");
                line = Regex.Replace(line, "\"(thread|line)\":\\d+", "\"$1\":*");
                return raw ? line : Regex.Replace(line, "^\\{\"seq\":\\d+", "{\"seq\":*");
            }).ToArray();
        }

        /// <summary>The lines of the tests' own events, without the header or what the store said of itself.</summary>
        private static string[] Ours(string[] lines) =>
            lines.Where(line => line.Contains("\"key\":\"tests.") || line.Contains("\"event\":\"tests.")).ToArray();

        /// <summary>Writes an error from a frame whose name a test reads, and a record below the stack level.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void FailFromHere(Log log)
        {
            log.Failed(1, new InvalidOperationException("The disk is full."));
            log.Saved(1, "A.sav");
        }

        private static long Sequence(string line) => long.Parse(Regex.Match(line, "^\\{\"seq\":(\\d+)").Groups[1].Value);
    }
}
