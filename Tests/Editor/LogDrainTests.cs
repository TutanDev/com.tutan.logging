using System;
using System.Linq;
using NUnit.Framework;
using Tutan.Functional;

namespace Tutan.Logging.Tests
{
    public class LogDrainTests
    {
        [Test]
        public void A_sink_is_handed_only_what_is_at_its_least_level_or_above()
        {
            using var captured = new Captured(sink: new LogSinkOptions(LogLevel.Information));

            captured.Log.Step(1);
            captured.Log.Wrote(2);
            captured.Log.Saved(3, "c.sav");

            Assert.That(captured.Records.Where(r => r.Event.Category == "tests.save").Select(r => r.Event), Is.EqualTo(new[] { SaveLog.Events.Saved }));
        }

        [Test]
        public void Before_an_error_a_sink_with_a_backlog_is_handed_the_latest_records_it_skipped_in_the_errors_scope()
        {
            using var captured = new Captured(sink: new LogSinkOptions(LogLevel.Warning, Backlog: 3));

            captured.Log.Step(0);
            using (LogScope saving = captured.Log.Saving(1))
            {
                for (int i = 1; i <= 5; i++)
                    saving.Log.Step(i);
                saving.Log.Failed(1, new InvalidOperationException("The disk is full."));
            }

            var records = captured.Records.Where(r => r.Event.Category == "tests.save").ToList();
            Assert.That(records.Select(r => r.Message), Is.EqualTo(new[] { "Step 3", "Step 4", "Step 5", "Saving slot 1 failed" }));
            Assert.That(records.Select(r => r.Backlog), Is.EqualTo(new[] { true, true, true, false }));
        }

        [Test]
        public void The_opening_of_the_errors_scope_is_context_too()
        {
            using var captured = new Captured(sink: new LogSinkOptions(LogLevel.Warning, Backlog: 10));

            captured.Log.Step(0);
            using (LogScope saving = captured.Log.Saving(1))
            {
                saving.Log.Step(1);
                using (LogScope inner = saving.Log.Saving(2))
                    inner.Log.Failed(2, new InvalidOperationException("The disk is full."));
            }

            var records = captured.Records.Where(r => r.Event.Category == "tests.save").ToList();
            Assert.That(records.Select(r => r.Message), Is.EqualTo(new[]
            {
                "Saving slot 1", "Step 1", "Saving slot 2", "Saving slot 2 failed",
            }), "What is under the outermost scope is the error's context, and what is outside it is not.");
        }

        [Test]
        public void A_record_is_context_for_one_error_only()
        {
            using var captured = new Captured(sink: new LogSinkOptions(LogLevel.Error, Backlog: 5));

            captured.Log.Step(1);
            captured.Log.Failed(1, new InvalidOperationException("first"));
            captured.Log.Failed(2, new InvalidOperationException("second"));

            var records = captured.Records.Where(r => r.Event.Category == "tests.save").ToList();
            Assert.That(records.Select(r => r.Message), Is.EqualTo(new[] { "Step 1", "Saving slot 1 failed", "Saving slot 2 failed" }));
        }

        [Test]
        public void A_sink_with_no_backlog_is_handed_no_context()
        {
            using var captured = new Captured(sink: new LogSinkOptions(LogLevel.Error));

            captured.Log.Step(1);
            captured.Log.Failed(1, new InvalidOperationException("first"));

            Assert.That(captured.Records.Where(r => r.Event.Category == "tests.save").Select(r => r.Message), Is.EqualTo(new[] { "Saving slot 1 failed" }));
        }

        [Test]
        public void A_sink_that_throws_is_not_written_to_again_and_the_store_says_why()
        {
            var store = new LogStore(LogRules.Parse("trace").ValueUnsafe(), new LogStoreOptions(Program: "Tests"));
            var failing = new FailingSink();
            var memory = new MemorySink();
            using var drain = new LogDrain(store, (failing, new LogSinkOptions(LogLevel.Trace)), (memory, new LogSinkOptions(LogLevel.Trace)));

            store.Log.Saved(1, "a.sav");
            drain.Pump();
            store.Log.Saved(2, "b.sav");
            drain.Pump();

            Assert.That(failing.Writes, Is.EqualTo(1));
            Assert.That(memory.Records.Of(SaveLog.Events.Saved), Has.Count.EqualTo(2), "The other sinks go on.");
            MemoryRecord failed = memory.Records.Of(SelfLog.Events.SinkFailed).Single();
            Assert.That(failed.Field("Sink"), Is.EqualTo("failing"));
            Assert.That(failed.Field("Exception"), Is.InstanceOf<InvalidOperationException>());
        }

        [Test]
        public void Every_pump_flushes_the_sinks()
        {
            using var captured = new Captured();

            _ = captured.Records;
            _ = captured.Records;

            Assert.That(captured.Sink.Flushes, Is.EqualTo(2));
        }

        [Test]
        public void Disposing_drains_what_is_left_and_disposes_the_sinks_that_are_disposable()
        {
            var store = new LogStore(LogRules.Parse("trace").ValueUnsafe(), new LogStoreOptions(Program: "Tests"));
            var sink = new DisposableSink();
            var drain = new LogDrain(store, (sink, new LogSinkOptions(LogLevel.Trace)));

            store.Log.Saved(1, "a.sav");
            drain.Dispose();
            drain.Dispose();
            drain.Flush();

            Assert.That(sink.Writes, Is.EqualTo(1));
            Assert.That(sink.Disposals, Is.EqualTo(1));
            Assert.That(() => drain.Pump(), Throws.InstanceOf<ObjectDisposedException>());
        }

        [Test]
        public void A_sink_given_twice_or_with_a_negative_backlog_is_refused()
        {
            var store = new LogStore(LogRules.Default, new LogStoreOptions(Program: "Tests"));
            var sink = new MemorySink();

            Assert.That(() => new LogDrain(store, (sink, new LogSinkOptions()), (sink, new LogSinkOptions())), Throws.ArgumentException);
            Assert.That(() => new LogDrain(store, (sink, new LogSinkOptions(Backlog: -1))), Throws.InstanceOf<ArgumentOutOfRangeException>());
        }

        [Test]
        public void A_sink_is_opened_with_the_stores_program_and_clock()
        {
            var store = new LogStore(LogRules.Default, new LogStoreOptions(Program: "Opened"));
            var sink = new DisposableSink();

            using var drain = new LogDrain(store, (sink, new LogSinkOptions(Redact: LogRedaction.Personal)));

            LogContext opened = sink.Opened!;
            Assert.That(opened.Program, Is.EqualTo("Opened"));
            Assert.That(opened.Clock, Is.EqualTo(store.Clock));
            Assert.That(opened.Redact, Is.EqualTo(LogRedaction.Personal));
        }

        private sealed class FailingSink : ILogSink
        {
            public int Writes { get; private set; }

            public string Name => "failing";

            public void Open(LogContext context)
            {
            }

            public void Write(LogRecord record, bool backlog, LogContext context)
            {
                Writes++;
                throw new InvalidOperationException("The sink broke.");
            }

            public void Lost(long count, LogContext context)
            {
            }

            public void Flush()
            {
            }
        }

        private sealed class DisposableSink : ILogSink, IDisposable
        {
            public int Writes { get; private set; }

            public int Disposals { get; private set; }

            public LogContext? Opened { get; private set; }

            public string Name => "disposable";

            public void Open(LogContext context) => Opened = context;

            public void Write(LogRecord record, bool backlog, LogContext context)
            {
                if (record.Event == SaveLog.Events.Saved)
                    Writes++;
            }

            public void Lost(long count, LogContext context)
            {
            }

            public void Flush()
            {
            }

            public void Dispose() => Disposals++;
        }
    }
}
