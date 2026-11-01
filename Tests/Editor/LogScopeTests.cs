using System;
using System.Linq;
using NUnit.Framework;

namespace Tutan.Logging.Tests
{
    public class LogScopeTests
    {
        [Test]
        public void A_scope_that_is_off_hands_back_the_log_it_was_opened_from()
        {
            using var captured = new Captured("warning");

            using LogScope saving = captured.Log.Saving(1);

            Assert.That(saving.IsOn, Is.False);
            Assert.That(saving.Id, Is.EqualTo(0));
            Assert.That(saving.Log, Is.EqualTo(captured.Log));
            Assert.That(saving.ToString(), Is.EqualTo("LogScope (off)"));
        }

        [Test]
        public void What_is_written_through_a_scopes_log_is_under_the_scope()
        {
            using var captured = new Captured();

            using (LogScope saving = captured.Log.Saving(2))
                saving.Log.Saved(2, "slot2.sav");
            captured.Log.Saved(3, "slot3.sav");

            var records = captured.Records.Where(r => r.Event.Category == "tests.save").ToList();
            Assert.That(records.Select(r => r.Kind), Is.EqualTo(new[]
            {
                LogRecordKind.ScopeBegin, LogRecordKind.Event, LogRecordKind.ScopeEnd, LogRecordKind.Event,
            }));

            long id = records[0].Sequence;
            Assert.That(records[0].Begin, Is.EqualTo(id), "An opening's Begin is itself.");
            Assert.That(records[0].Scope, Is.EqualTo(0));
            Assert.That(records[0].Field("Slot"), Is.EqualTo(2));
            Assert.That(records[1].Scope, Is.EqualTo(id));
            Assert.That(records[2].Begin, Is.EqualTo(id));
            Assert.That(records[2].Scope, Is.EqualTo(0), "A closing is under what its opening was under.");
            Assert.That(records[2].Fields, Is.Empty);
            Assert.That(records[3].Scope, Is.EqualTo(0));
        }

        [Test]
        public void A_closing_says_what_its_opening_said_and_how_long_the_scope_was_open()
        {
            var clock = new TestClock();
            using var captured = new Captured(clock: clock);

            using (captured.Log.Saving(4))
                clock.Advance(TimeSpan.FromMilliseconds(250));

            MemoryRecord end = captured.Of(SaveLog.Events.Saving).Single(r => r.Kind == LogRecordKind.ScopeEnd);
            Assert.That(end.Message, Is.EqualTo("Saving slot 4"));
            Assert.That(end.Elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(250)).Within(TimeSpan.FromMilliseconds(1)));
            Assert.That(end.Level, Is.EqualTo(LogLevel.Information));
            Assert.That(end.CallSite, Is.EqualTo(captured.Of(SaveLog.Events.Saving)[0].CallSite), "A closing is said where its scope was opened.");
        }

        [Test]
        public void A_scope_closes_once_however_many_of_its_copies_are_disposed()
        {
            using var captured = new Captured();

            LogScope saving = captured.Log.Saving(5);
            LogScope copy = saving;
            saving.Dispose();
            copy.Dispose();
            saving.Dispose();

            Assert.That(captured.Of(SaveLog.Events.Saving).Count(r => r.Kind == LogRecordKind.ScopeEnd), Is.EqualTo(1));
        }

        [Test]
        public void A_record_written_through_a_closed_scopes_log_is_still_under_it()
        {
            using var captured = new Captured();

            LogScope saving = captured.Log.Saving(6);
            Log under = saving.Log;
            saving.Dispose();
            under.Saved(6, "late.sav");

            Assert.That(captured.Of(SaveLog.Events.Saved).Single().Scope, Is.EqualTo(saving.Id));
        }

        [Test]
        public void Scopes_nest_and_each_closing_returns_to_the_scope_around_it()
        {
            using var captured = new Captured();

            using (LogScope outer = captured.Log.Saving(1))
            {
                using (LogScope inner = outer.Log.Saving(2))
                {
                    inner.Log.Step(1);
                    Assert.That(inner.Log.Scope, Is.EqualTo(inner.Id));
                }

                outer.Log.Step(2);
            }

            var records = captured.Of(SaveLog.Events.Saving).Concat(captured.Of(SaveLog.Events.Step)).OrderBy(r => r.Sequence).ToList();
            long outerId = records[0].Sequence;
            long innerId = records[1].Sequence;
            Assert.That(records[1].Scope, Is.EqualTo(outerId));
            Assert.That(records[2].Scope, Is.EqualTo(innerId), "The first step is under the inner scope.");
            Assert.That(records[3].Kind, Is.EqualTo(LogRecordKind.ScopeEnd));
            Assert.That(records[3].Scope, Is.EqualTo(outerId), "The inner closing is under the outer scope.");
            Assert.That(records[4].Scope, Is.EqualTo(outerId), "The second step is under the outer scope.");
            Assert.That(records[5].Scope, Is.EqualTo(0));
        }

        [Test]
        public void A_scope_says_its_id()
        {
            using var captured = new Captured();

            using LogScope saving = captured.Log.Saving(1);

            Assert.That(saving.IsOn, Is.True);
            Assert.That(saving.ToString(), Is.EqualTo($"LogScope #{saving.Id}"));
        }
    }
}
