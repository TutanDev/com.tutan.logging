using System;
using System.Globalization;
using System.Linq;
using NUnit.Framework;
using Tutan.Functional;
using UnityEngine;

namespace Tutan.Logging.Tests
{
    public class LogFieldTests
    {
        [Test]
        public void Whole_numbers_bools_and_chars_are_copied_and_read_back_as_themselves()
        {
            using var logged = new MemoryLog();

            logged.Log.Numbers(true, 'x', -5, 200, -300, 60000, -70000, 4_000_000_000u, -5_000_000_000L, ulong.MaxValue);

            MemoryRecord record = logged.Events.Of(FieldLog.Events.Numbers).Single();
            Assert.That(record.Message, Is.EqualTo("true x -5 200 -300 60000 -70000 4000000000 -5000000000 18446744073709551615"));
            Assert.That(record.Fields.Select(f => f.Value), Is.EqualTo(new object[]
            {
                true, 'x', (sbyte)-5, (byte)200, (short)-300, (ushort)60000, -70000, 4_000_000_000u, -5_000_000_000L, ulong.MaxValue,
            }));
            Assert.That(record.Fields.Select(f => f.Name), Is.EqualTo(new[]
            {
                "Flag", "Letter", "Tiny", "Small", "Short", "Word", "Int", "Count", "Long", "Huge",
            }));
        }

        [Test]
        public void Reals_are_copied_and_written_in_the_invariant_culture()
        {
            using var logged = new MemoryLog();
            CultureInfo before = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            try
            {
                logged.Log.Reals(0.1f, 2.5, 1.25m);

                MemoryRecord record = logged.Events.Of(FieldLog.Events.Reals).Single();
                Assert.That(record.Message, Is.EqualTo("0.1 2.5 1.25"));
                Assert.That(record.Fields.Select(f => f.Value), Is.EqualTo(new object[] { 0.1f, 2.5, 1.25m }));
            }
            finally
            {
                CultureInfo.CurrentCulture = before;
            }
        }

        [Test]
        public void Guids_dates_and_spans_are_copied_and_written_as_iso_8601_and_c()
        {
            using var logged = new MemoryLog();
            var id = new Guid("0f8fad5b-d9cb-469f-a165-70867728950e");
            var when = new DateTime(2026, 11, 1, 10, 24, 9, DateTimeKind.Utc);
            var at = new DateTimeOffset(2026, 11, 1, 11, 24, 9, TimeSpan.FromHours(1));
            var took = new TimeSpan(1, 2, 3);

            logged.Log.Times(id, when, at, took);

            MemoryRecord record = logged.Events.Of(FieldLog.Events.Times).Single();
            Assert.That(record.Message, Is.EqualTo(
                "0f8fad5b-d9cb-469f-a165-70867728950e 2026-11-01T10:24:09.0000000Z 2026-11-01T11:24:09.0000000+01:00 01:02:03"));
            Assert.That(record.Fields.Select(f => f.Value), Is.EqualTo(new object[] { id, when, at, took }));
        }

        [Test]
        public void An_enum_is_written_by_its_names_and_read_back_as_its_value()
        {
            using var logged = new MemoryLog();

            logged.Log.Things(Doors.Front | Doors.Back, "a", "b", Vector3.zero);
            logged.Log.Things(Doors.None, "a", "b", Vector3.zero);
            logged.Log.Things((Doors)8, "a", "b", Vector3.zero);
            logged.Log.Things(Doors.Side, "a", "b", Vector3.zero);

            var things = logged.Events.Of(FieldLog.Events.Things);
            Assert.That(things.Select(t => t.Fields[0].Text), Is.EqualTo(new[] { "Front | Back", "None", "8", "Side" }));
            Assert.That(things.Select(t => t.Field("Doors")), Is.EqualTo(new object[] { 3L, 0L, 8L, 4L }));
        }

        [Test]
        public void A_string_is_kept_by_reference_and_chars_are_copied_at_the_call()
        {
            using var logged = new MemoryLog();
            string text = "kept " + 1;
            char[] chars = "copied".ToCharArray();

            logged.Log.Things(Doors.None, text, chars, new Vector3(1, 2, 3));
            chars[0] = 'X';

            MemoryRecord record = logged.Events.Of(FieldLog.Events.Things).Single();
            Assert.That(record.Field("Text"), Is.SameAs(text));
            Assert.That(record.Field("Chars"), Is.EqualTo("copied"));
            Assert.That(record.Field("Where"), Is.EqualTo(new Vector3(1, 2, 3).ToString(null, CultureInfo.InvariantCulture)));
            Assert.That(record.Message, Is.EqualTo($"None kept 1 copied {new Vector3(1, 2, 3).ToString(null, CultureInfo.InvariantCulture)}"));
        }

        [Test]
        public void Chars_past_the_most_a_field_holds_are_cut_and_end_in_an_ellipsis()
        {
            using var logged = new MemoryLog();
            string longer = new string('a', LogWriter.MaxChars + 100);

            logged.Log.Things(Doors.None, "", longer, Vector3.zero);

            string kept = (string)logged.Events.Of(FieldLog.Events.Things).Single().Field("Chars")!;
            Assert.That(kept, Has.Length.EqualTo(LogWriter.MaxChars));
            Assert.That(kept, Does.EndWith("aaa..."));
        }

        [Test]
        public void Empty_chars_are_an_empty_field()
        {
            using var logged = new MemoryLog();

            logged.Log.Things(Doors.None, "", ReadOnlySpan<char>.Empty, Vector3.zero);

            Assert.That(logged.Events.Of(FieldLog.Events.Things).Single().Field("Chars"), Is.EqualTo(""));
        }

        [Test]
        public void An_optional_or_nullable_value_that_is_none_is_written_none()
        {
            using var logged = new MemoryLog();

            logged.Log.Optionals(F.None, null, F.None, F.None);
            logged.Log.Optionals(5, 6, "note", new Vector3(1, 2, 3));

            var records = logged.Events.Of(FieldLog.Events.Optionals);
            Assert.That(records[0].Message, Is.EqualTo("none none none none"));
            Assert.That(records[0].Fields.Select(f => f.Value), Is.All.Null);
            Assert.That(records[1].Fields.Select(f => f.Value).Take(3), Is.EqualTo(new object[] { 5, 6, "note" }));
            Assert.That(records[1].Field("Where"), Is.EqualTo(new Vector3(1, 2, 3).ToString(null, CultureInfo.InvariantCulture)));
        }

        [Test]
        public void A_null_reference_is_written_null()
        {
            using var logged = new MemoryLog();

            logged.Log.Said(null);

            MemoryRecord record = logged.Events.Of(FieldLog.Events.Said).Single();
            Assert.That(record.Message, Is.EqualTo("null"));
            Assert.That(record.Field("Text"), Is.Null);
        }

        [Test]
        public void An_exception_is_kept_and_written_as_its_type_and_message()
        {
            using var logged = new MemoryLog();
            var error = new InvalidOperationException("The disk is full.");

            logged.Log.Failed(1, error);

            MemoryRecord record = logged.Events.Of(SaveLog.Events.Failed).Single();
            Assert.That(record.Field("Error"), Is.SameAs(error));
            Assert.That(record.Fields[1].Text, Is.EqualTo("InvalidOperationException: The disk is full."));
            Assert.That(record.Message, Is.EqualTo("Saving slot 1 failed"), "A field the template does not name is kept but not written in the message.");
        }

        [Test]
        public void A_value_is_read_as_what_it_was_written_as_and_not_as_another_type()
        {
            using var captured = new Captured();
            captured.Log.Saved(9, "nine.sav");
            var batch = new LogBatch(16);
            captured.Store.Read(captured.Store.Next - 1, batch);

            LogRecord record = batch[0];
            Assert.That(record.TryField("slot", out LogValue slot), Is.True);
            Assert.That(slot.Read<int>(), Is.EqualTo(9));
            Assert.That(slot.AsInt64(), Is.EqualTo(9));
            Assert.That(slot.AsDouble(), Is.EqualTo(9.0));
            Assert.That(record.TryField("Nothing", out _), Is.False);
            Assert.That(Throws(record.Field(0), v => v.Read<Guid>()), Is.True, "A Guid is bigger than the slot an int is in.");
            Assert.That(Throws(record.Field(0), v => v.AsChars()), Is.True);
            Assert.That(Throws(record.Field(0), v => v.AsRef()), Is.True);
            Assert.That(Throws(record.Field(1), v => v.Read<int>()), Is.True, "A string is kept by reference.");
            Assert.That(Throws(record.Field(1), v => v.AsInt64()), Is.True);
            Assert.That(record.Field(1).AsChars().ToString(), Is.EqualTo("nine.sav"));
            Assert.That(record.Field(1).AsRef(), Is.EqualTo("nine.sav"));
        }

        [Test]
        public void A_value_that_is_none_has_nothing_to_read()
        {
            using var captured = new Captured();
            captured.Log.Optionals(F.None, null, F.None, F.None);
            var batch = new LogBatch(16);
            captured.Store.Read(captured.Store.Next - 1, batch);

            LogRecord record = batch[0];
            Assert.That(record.Field(0).IsNone, Is.True);
            Assert.That(record.Field(2).IsNone, Is.True);
            Assert.That(Throws(record.Field(0), v => v.Read<int>()), Is.True);
        }

        [Test]
        public void Holes_are_padded_to_their_alignment_and_formatted_as_they_ask()
        {
            using var logged = new MemoryLog();

            logged.Log.Aligned("ab", 42, 0.5, Doors.Front | Doors.Side);
            logged.Log.Aligned("longer than six", 1234567, 1, Doors.None);

            var records = logged.Events.Of(FieldLog.Events.Aligned);
            Assert.That(records[0].Message, Is.EqualTo("[ab    ] [    42] 0.50 5"));
            Assert.That(records[1].Message, Is.EqualTo("[longer than six] [1234567] 1.00 0"), "What is wider than its alignment is not cut.");
        }

        [Test]
        public void A_template_is_kept_taken_apart()
        {
            LogTemplate template = FieldLog.Events.Aligned.Template;

            Assert.That(template.Text, Is.EqualTo("[{Left,-6}] [{Right,6}] {Ratio:0.00} {Doors:D}"));
            Assert.That(template.Literals, Is.EqualTo(new[] { "[", "] [", "] ", " ", "" }));
            Assert.That(template.Holes, Is.EqualTo(new[]
            {
                new LogHole(0, -6, null), new LogHole(1, 6, null), new LogHole(2, 0, "0.00"), new LogHole(3, 0, "D"),
            }));
        }

        [Test]
        public void A_record_formats_into_a_span_and_says_when_it_does_not_fit()
        {
            using var captured = new Captured();
            captured.Log.Saved(3, "slot3.sav");
            var batch = new LogBatch(16);
            captured.Store.Read(captured.Store.Next - 1, batch);

            Span<char> small = stackalloc char[8];
            Span<char> large = stackalloc char[64];

            Assert.That(batch[0].TryFormat(small, out _, LogRedaction.None), Is.False);
            Assert.That(batch[0].TryFormat(large, out int written, LogRedaction.None), Is.True);
            Assert.That(large.Slice(0, written).ToString(), Is.EqualTo("Saved slot 3 as slot3.sav"));
            Assert.That(batch[0].ToString(), Is.EqualTo("Saved slot 3 as slot3.sav"));
        }

        // A value is a ref struct, which a lambda cannot capture, so it is handed in.
        private delegate void Reading(LogValue value);

        private static bool Throws(LogValue value, Reading read)
        {
            try
            {
                read(value);
                return false;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }
}
