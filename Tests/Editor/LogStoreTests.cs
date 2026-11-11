using System;
using System.Linq;
using System.Runtime.CompilerServices;
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
        public void A_store_refuses_a_record_of_another_kind_or_level_than_its_event_declares()
        {
            using var captured = new Captured();

            Assert.That(() => { captured.Log.Write(SaveLog.Events.Saved, LogLevel.Error, 0, "", 0); },
                Throws.ArgumentException.With.Message.Contains("tests.save/Saved is declared at Information, and it was written at Error"));
            Assert.That(() => { captured.Log.BeginScope(SaveLog.Events.Saved, LogLevel.Information, 0, "", 0); },
                Throws.ArgumentException.With.Message.Contains("tests.save/Saved is an event, and it was written as a scope"));
            Assert.That(() => { captured.Log.Write(SaveLog.Events.Saving, LogLevel.Information, 0, "", 0); },
                Throws.ArgumentException.With.Message.Contains("tests.save/Saving is a scope, and it was written as an event"));
        }

        [Test]
        public void A_store_refuses_a_record_at_no_level()
        {
            using var captured = new Captured();

            Assert.That(() => { captured.Log.Write(HeardLog.Events.Heard, LogLevel.None, 0, "", 0); },
                Throws.TypeOf<ArgumentOutOfRangeException>().With.Message.Contains("A record of tests.heard/Heard was written at None"));
            Assert.That(() => { captured.Log.Write(SaveLog.Events.Saved, LogLevel.None, 0, "", 0); },
                Throws.TypeOf<ArgumentOutOfRangeException>().With.Message.Contains("A record of tests.save/Saved was written at None"),
                "No level, before another level than its own.");
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
        public void An_opening_kept_past_the_ring_keeps_its_stack()
        {
            using var captured = new Captured("trace; stack>=information", options: new LogStoreOptions(Records: LogStore.MinRecords, Program: "Tests"));
            using LogScope saving = captured.Log.Saving(7);

            for (int i = 0; i < 40; i++)
                saving.Log.Step(i);

            var batch = new LogBatch(4);
            captured.Store.Read(new[] { saving.Id }, batch);

            Assert.That(captured.Store.First, Is.GreaterThan(saving.Id), "The ring has moved past the opening.");
            Assert.That(batch[0].Stack.Map(stack => stack.Frames[0].Method).Or(""), Is.EqualTo(Called(nameof(An_opening_kept_past_the_ring_keeps_its_stack))));
        }

        [Test]
        public void A_record_at_its_stack_level_carries_the_stack_it_was_written_from()
        {
            LogStore store = Stores.Everything();
            store.Log.Failed(1, new InvalidOperationException("The disk is full."));
            store.Log.Saved(1, "A.sav");

            var batch = new LogBatch();
            store.Read(store.First, batch);
            LogRecord failed = batch[0];
            LogStack stack = failed.Stack.ValueUnsafe();

            Assert.That(stack.Frames[0].Method, Is.EqualTo(Called(nameof(A_record_at_its_stack_level_carries_the_stack_it_was_written_from))),
                "From the method that made the call: the generated method and the package's own are left out.");
            Assert.That(stack.Frames[0].At, Is.EqualTo(failed.CallSite), "Its first frame is where the record was written.");
            Assert.That(stack.Frames.Count, Is.GreaterThan(1), "And outward: the frames of the runner that called the test.");
            Assert.That(failed.FieldCount, Is.EqualTo(2), "The stack is not a field.");
            Assert.That(batch[1].Stack.IsNone, Is.True, "Information is below the default stack level, error.");
        }

        [Test]
        public void A_wrapper_that_forwards_its_callers_call_site_is_left_out_of_the_stack()
        {
            LogStore store = Stores.Everything();
            FailedFor(store.Log, 1);

            var batch = new LogBatch();
            store.Read(store.First, batch);
            LogRecord failed = batch[0];
            LogStack stack = failed.Stack.ValueUnsafe();

            Assert.That(stack.Frames[0].Method, Is.EqualTo(Called(nameof(A_wrapper_that_forwards_its_callers_call_site_is_left_out_of_the_stack))));
            Assert.That(stack.Frames[0].At, Is.EqualTo(failed.CallSite));
        }

        [Test]
        public void Rules_say_from_which_level_a_record_carries_its_stack()
        {
            var store = new LogStore(LogRules.Parse("trace; stack>=off; tests.save=stack>=debug").ValueUnsafe(), new LogStoreOptions(Program: "Tests"));
            store.Log.Step(1);
            store.Log.Wrote(2);
            store.Log.Heard(LogLevel.Critical, "loud");
            using (store.Log.Saving(3))
            {
            }

            store.Apply(LogRules.Parse("trace; stack>=critical").ValueUnsafe());
            store.Log.Heard(LogLevel.Critical, "loud");
            store.Log.Heard(LogLevel.Error, "less loud");

            var batch = new LogBatch();
            store.Read(store.First, batch);
            bool[] stacked = Enumerable.Range(0, batch.Count).Select(i => batch[i].Stack.IsSome).ToArray();

            Assert.That(stacked, Is.EqualTo(new[] { false, true, false, true, false, true, false }),
                "Trace is below tests.save's stack level, the critical below none is off, a scope's opening carries one and its closing never, and new rules apply.");
        }

        [Test]
        public void A_record_with_no_call_site_carries_no_stack()
        {
            LogStore store = Stores.Everything();

            // As the bridge writes Unity's messages, which hold their own stack.
            store.Log.Failed(1, new InvalidOperationException("The disk is full."), "", 0);

            var batch = new LogBatch();
            store.Read(store.First, batch);

            Assert.That((batch[0].CallSite.IsKnown, batch[0].Stack.IsNone), Is.EqualTo((false, true)));
        }

        [Test]
        public void A_stack_is_equal_to_another_of_the_same_frames_and_names_each_frame_on_a_line()
        {
            var save = new LogStackFrame("Game.SaveManager.Save(int)", new LogCallSite("Assets/Game/SaveManager.cs", 42));
            var native = new LogStackFrame("Game.Native.Call()", default);
            var stack = new LogStack(new[] { save, native });

            Assert.That(stack, Is.EqualTo(new LogStack(new[] { save, native })));
            Assert.That(stack.GetHashCode(), Is.EqualTo(new LogStack(new[] { save, native }).GetHashCode()));
            Assert.That(stack, Is.Not.EqualTo(new LogStack(new[] { native, save })));
            Assert.That(stack, Is.Not.EqualTo(new LogStack(new[] { save })));
            Assert.That(stack.ToString(), Is.EqualTo("Game.SaveManager.Save(int) (at Assets/Game/SaveManager.cs:42)\nGame.Native.Call()"));
            Assert.That(() => new LogStackFrame("", default), Throws.ArgumentException);
            Assert.That(() => new LogStack(new[] { save, default }), Throws.ArgumentException, "A default frame names no method.");
            Assert.That(() => new LogStack(Array.Empty<LogStackFrame>()), Throws.ArgumentException, "A record without a frame has no stack.");
            Assert.That(default(LogStackFrame).Method, Is.Empty);
        }

        [Test]
        public void A_frame_names_its_method_as_csharp_writes_it()
        {
            LogStore store = Stores.Everything();
            long count = 0;
            Nested.Fail(store.Log, new[] { "A.sav" }, ref count, out int _);

            var batch = new LogBatch();
            store.Read(store.First, batch);

            Assert.That(batch[0].Stack.ValueUnsafe().Frames[0].Method,
                Is.EqualTo($"{typeof(LogStoreTests).FullName}.{nameof(Nested)}.{nameof(Nested.Fail)}(Log, string[], ref long, out int)"),
                "A nested type after its outer one, a keyword for a built-in type, and how a parameter is passed.");
        }

        /// <summary>The method a test is, as a frame names it.</summary>
        private static string Called(string test) => $"{typeof(LogStoreTests).FullName}.{test}()";

        /// <summary>A wrapper of the program's own, which writes for its caller and forwards its caller's call site.</summary>
        private static void FailedFor(Log log, int slot, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
            log.Failed(slot, new InvalidOperationException("The disk is full."), file, line);

        private static class Nested
        {
            /// <summary>Writes an error from a frame of its own, whose name is what a test reads.</summary>
            [MethodImpl(MethodImplOptions.NoInlining)]
            public static void Fail(Log log, string[] files, ref long count, out int written)
            {
                count++;
                written = files.Length;
                log.Failed(written, new InvalidOperationException("The disk is full."));
            }
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
