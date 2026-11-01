using System;
using System.Linq;
using NUnit.Framework;
using Tutan.Functional;

namespace Tutan.Logging.Tests
{
    public class LogStoreTests
    {
        [Test]
        public void A_default_log_throws_saying_it_was_never_handed_a_store()
        {
            Log log = default;

            Assert.That(log.IsHanded, Is.False);
            Assert.That(() => log.Saved(1, "a.sav"), Throws.InvalidOperationException.With.Message.Contains("never handed a store"));
            Assert.That(() => log.Saving(1), Throws.InvalidOperationException);
        }

        [Test]
        public void Discard_captures_nothing_and_its_scopes_hand_it_back()
        {
            Log.Discard.Saved(1, "a.sav");
            using LogScope scope = Log.Discard.Saving(1);

            Assert.That(Log.Discard.IsEnabled(SaveLog.Events.Saved), Is.False);
            Assert.That(scope.IsOn, Is.False);
            Assert.That(scope.Log, Is.EqualTo(Log.Discard));
        }

        [Test]
        public void A_record_has_its_event_level_call_site_and_fields()
        {
            using var captured = new Captured();

            captured.Log.Saved(3, "slot3.sav");

            MemoryRecord saved = captured.Of(SaveLog.Events.Saved).Single();
            Assert.That(saved.Level, Is.EqualTo(LogLevel.Information));
            Assert.That(saved.Kind, Is.EqualTo(LogRecordKind.Event));
            Assert.That(saved.Scope, Is.EqualTo(0));
            Assert.That(saved.Message, Is.EqualTo("Saved slot 3 as slot3.sav"));
            Assert.That(saved.Field("Slot"), Is.EqualTo(3));
            Assert.That(saved.Field("file"), Is.EqualTo("slot3.sav"));
            Assert.That(saved.CallSite.IsKnown, Is.True);
            Assert.That(saved.CallSite.File, Does.EndWith("LogStoreTests.cs"));
            Assert.That(saved.CallSite.Line, Is.GreaterThan(0));
            Assert.That(saved.Thread, Is.EqualTo(Environment.CurrentManagedThreadId));
        }

        [Test]
        public void Records_are_numbered_in_order_and_stamped_with_the_frame_they_were_written_in()
        {
            using var captured = new Captured();
            long first = captured.Store.Next;

            captured.Log.Step(1);
            captured.Store.EnterFrame(42);
            captured.Log.Step(2);

            var steps = captured.Of(SaveLog.Events.Step);
            Assert.That(steps.Select(s => s.Sequence), Is.EqualTo(new[] { first, first + 1 }));
            Assert.That(steps.Select(s => s.Frame), Is.EqualTo(new long[] { 0, 42 }));
            Assert.That(captured.Store.Frame, Is.EqualTo(42));
            Assert.That(captured.Store.Next, Is.EqualTo(first + 2));
        }

        [Test]
        public void An_event_below_the_least_level_is_not_captured_until_rules_let_it_through()
        {
            using var captured = new Captured("information");

            captured.Log.Wrote(10);
            Assert.That(captured.Log.IsEnabled(SaveLog.Events.Wrote), Is.False);

            captured.Store.Apply(LogRules.Parse("debug").ValueUnsafe());
            captured.Log.Wrote(20);

            Assert.That(captured.Log.IsEnabled(SaveLog.Events.Wrote), Is.True);
            Assert.That(captured.Of(SaveLog.Events.Wrote).Select(r => r.Field("Bytes")), Is.EqualTo(new object[] { 20L }));
        }

        [Test]
        public void A_level_chosen_at_the_call_is_the_records_and_is_gated_as_given()
        {
            using var captured = new Captured("warning");

            captured.Log.Heard(LogLevel.Information, "quiet");
            captured.Log.Heard(LogLevel.Error, "loud");

            Assert.That(captured.Log.IsEnabled(HeardLog.Events.Heard), Is.True, "Some level of it is on.");
            Assert.That(captured.Log.IsEnabled(HeardLog.Events.Heard, LogLevel.Debug), Is.False);
            Assert.That(captured.Log.IsEnabled(HeardLog.Events.Heard, LogLevel.None), Is.False);
            MemoryRecord heard = captured.Of(HeardLog.Events.Heard).Single();
            Assert.That(heard.Level, Is.EqualTo(LogLevel.Error));
            Assert.That(heard.Message, Is.EqualTo("Heard loud"));
        }

        [Test]
        public void A_limit_drops_what_is_past_it_and_counts_the_drops_onto_the_next_record_kept()
        {
            var clock = new TestClock();
            using var captured = new Captured(clock: clock);

            for (int i = 0; i < 5; i++)
                captured.Log.Hit(i);
            clock.Advance(TimeSpan.FromSeconds(1));
            captured.Log.Hit(5);

            var hits = captured.Of(LimitLog.Events.Hit);
            Assert.That(hits.Select(h => h.Field("Count")), Is.EqualTo(new object[] { 0, 1, 5 }));
            Assert.That(hits.Select(h => h.Suppressed), Is.EqualTo(new[] { 0, 0, 3 }));
        }

        [Test]
        public void A_limit_refills_at_its_rate_and_never_past_it()
        {
            var clock = new TestClock();
            using var captured = new Captured(clock: clock);

            captured.Log.Hit(0);
            captured.Log.Hit(1);
            clock.Advance(TimeSpan.FromSeconds(0.5));
            captured.Log.Hit(2);
            captured.Log.Hit(3);
            clock.Advance(TimeSpan.FromSeconds(60));
            for (int i = 4; i < 8; i++)
                captured.Log.Hit(i);

            Assert.That(captured.Of(LimitLog.Events.Hit).Select(h => h.Field("Count")), Is.EqualTo(new object[] { 0, 1, 2, 4, 5 }));
        }

        [Test]
        public void A_rules_limit_overrides_the_events_own()
        {
            var clock = new TestClock();
            using var captured = new Captured("trace; tests.limits/Hit=0/s; tests.limits/Struck=1/s", clock);

            for (int i = 0; i < 3; i++)
            {
                captured.Log.Hit(i);
                captured.Log.Strike();
            }

            Assert.That(captured.Of(LimitLog.Events.Hit), Has.Count.EqualTo(3), "A limit of 0 is no limit, even for an event that declares one.");
            Assert.That(captured.Of(LimitLog.Events.Struck), Has.Count.EqualTo(1));
        }

        [Test]
        public void Applying_rules_says_each_rule_that_matches_nothing()
        {
            using var captured = new Captured();

            captured.Store.Apply(LogRules.Parse("trace; tests.sav=debug; tests.save=debug; tests.*=trace").ValueUnsafe());

            var said = captured.Of(SelfLog.Events.RuleMatchesNothing);
            Assert.That(said.Select(r => r.Field("Rule")), Is.EqualTo(new object[] { "tests.sav=debug" }));
            Assert.That(captured.Store.Rules.ToString(), Is.EqualTo("trace; tests.sav=debug; tests.save=debug; tests.*=trace"));
        }

        [Test]
        public void A_drain_that_falls_behind_is_told_how_many_records_it_lost_and_the_store_says_so()
        {
            using var captured = new Captured(options: new LogStoreOptions(Records: LogStore.MinRecords, Program: "Tests"));
            long first = captured.Store.Next;

            for (int i = 0; i < 40; i++)
                captured.Log.Step(i);

            var steps = captured.Of(SaveLog.Events.Step);
            // The drain was made reading from the store's first sequence, 1, and the ring kept the latest steps.
            long lost = first - 1 + 40 - LogStore.MinRecords;
            Assert.That(steps, Has.Count.EqualTo(LogStore.MinRecords));
            Assert.That(steps[0].Field("Step"), Is.EqualTo(40 - LogStore.MinRecords));
            Assert.That(captured.Sink.Overwritten, Is.EqualTo(lost));
            Assert.That(captured.Store.First, Is.GreaterThan(first));

            // What the drain says it lost it writes into the store, which the next read brings.
            Assert.That(captured.Of(SelfLog.Events.DrainFellBehind).Single().Field("Count"), Is.EqualTo(lost));
        }

        [Test]
        public void The_opening_of_a_scope_still_open_is_kept_however_old_it_is()
        {
            using var captured = new Captured(options: new LogStoreOptions(Records: LogStore.MinRecords, Program: "Tests"));
            using LogScope saving = captured.Log.Saving(7);

            for (int i = 0; i < 40; i++)
                saving.Log.Step(i);

            var batch = new LogBatch(4);
            int read = captured.Store.Read(new[] { saving.Id }, batch);

            Assert.That(captured.Store.First, Is.GreaterThan(saving.Id), "The ring has moved past the opening.");
            Assert.That(read, Is.EqualTo(1));
            Assert.That(batch[0].Kind, Is.EqualTo(LogRecordKind.ScopeBegin));
            Assert.That(batch[0].Sequence, Is.EqualTo(saving.Id));
            Assert.That(batch[0].Field(0).Read<int>(), Is.EqualTo(7));
        }

        [Test]
        public void A_ring_smaller_than_the_least_a_store_holds_is_refused()
        {
            Assert.That(() => new LogStore(LogRules.Default, new LogStoreOptions(Records: 1)), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => new LogStore(LogRules.Default, new LogStoreOptions(Bits: 1)), Throws.InstanceOf<ArgumentOutOfRangeException>());
            Assert.That(() => new LogStore(LogRules.Default, new LogStoreOptions(Refs: 1)), Throws.InstanceOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public void Rings_are_rounded_up_to_a_power_of_two()
        {
            var store = new LogStore(LogRules.Default, new LogStoreOptions(Records: 1000, Program: "Tests"));

            Assert.That(store.Capacity, Is.EqualTo(1024));
            Assert.That(store.Program, Is.EqualTo("Tests"));
        }

        [Test]
        public void A_log_says_its_store_and_scope_and_is_equal_to_another_handle_to_the_same()
        {
            using var captured = new Captured();
            using LogScope saving = captured.Log.Saving(1);

            Assert.That(captured.Log, Is.EqualTo(captured.Store.Log));
            Assert.That(saving.Log, Is.Not.EqualTo(captured.Log));
            Assert.That(saving.Log.Scope, Is.EqualTo(saving.Id));
            Assert.That(captured.Log.ToString(), Is.EqualTo("Log of Tests"));
            Assert.That(saving.Log.ToString(), Is.EqualTo($"Log of Tests under #{saving.Id}"));
            Assert.That(default(Log).ToString(), Does.Contain("never handed a store"));
            Assert.That(Log.Discard.ToString(), Is.EqualTo("Log.Discard"));
        }
    }
}
