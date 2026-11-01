using System;
using NUnit.Framework;
using Tutan.Functional;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace Tutan.Logging.Tests
{
    /// <summary>
    /// What the decision promises a game's frame: writing a record, opening a scope, and draining
    /// into a sink that keeps nothing allocate nothing once warm.
    /// </summary>
    public class AllocationTests
    {
        private const string File = "slot1.sav";

        [Test]
        public void Writing_an_event_allocates_nothing()
        {
            var store = new LogStore(LogRules.Parse("trace").ValueUnsafe(), new LogStoreOptions(Program: "Tests"));
            Log log = store.Log;
            var error = new InvalidOperationException("warm");
            void Write()
            {
                log.Saved(1, File);
                log.Numbers(true, 'x', 1, 2, 3, 4, 5, 6, 7, 8);
                log.Times(Guid.Empty, default, default, TimeSpan.Zero);
                log.Things(Doors.Front | Doors.Back, File, File.AsSpan(), Vector3.one);
                log.Optionals(1, null, F.None, Vector3.one);
                log.Failed(1, error);
                log.Heard(LogLevel.Warning, File);
            }

            Write();

            Assert.That(Write, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void An_event_that_is_off_allocates_nothing()
        {
            var store = new LogStore(LogRules.Parse("error").ValueUnsafe(), new LogStoreOptions(Program: "Tests"));
            Log log = store.Log;
            void Write()
            {
                log.Saved(1, File);
                log.Step(1);
                log.Heard(LogLevel.Information, File);
                Log.Discard.Saved(1, File);
            }

            Write();

            Assert.That(Write, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void Opening_and_closing_a_scope_allocates_nothing()
        {
            var store = new LogStore(LogRules.Parse("trace").ValueUnsafe(), new LogStoreOptions(Program: "Tests"));
            Log log = store.Log;
            void Scope()
            {
                using LogScope saving = log.Saving(1);
                saving.Log.Step(1);
                using LogScope off = Log.Discard.Saving(2);
            }

            Scope();

            Assert.That(Scope, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void A_record_a_limit_drops_allocates_nothing()
        {
            var store = new LogStore(LogRules.Parse("trace").ValueUnsafe(), new LogStoreOptions(Program: "Tests"));
            Log log = store.Log;
            void Hit()
            {
                for (int i = 0; i < 10; i++)
                    log.Hit(i);
            }

            Hit();

            Assert.That(Hit, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void Draining_into_a_sink_that_keeps_nothing_allocates_nothing()
        {
            var store = new LogStore(LogRules.Parse("trace").ValueUnsafe(), new LogStoreOptions(Program: "Tests"));
            var sink = new FormattingSink();
            using var drain = new LogDrain(store, (sink, new LogSinkOptions(LogLevel.Information, Backlog: 8)));
            Log log = store.Log;
            var error = new InvalidOperationException("warm");
            void Frame()
            {
                using (LogScope saving = log.Saving(1))
                {
                    saving.Log.Step(1);
                    saving.Log.Saved(1, File);
                    saving.Log.Failed(1, error);
                }

                drain.Pump();
            }

            Frame();
            Frame();

            Assert.That(Frame, Is.Not.AllocatingGCMemory());
            Assert.That(sink.Written, Is.GreaterThan(0));
        }

        /// <summary>Formats every record into a buffer it keeps, as a sink that writes text would, and keeps nothing else.</summary>
        private sealed class FormattingSink : ILogSink
        {
            private readonly char[] _chars = new char[1024];

            public int Written { get; private set; }

            public string Name => "formatting";

            public void Open(LogContext context)
            {
            }

            public void Write(LogRecord record, bool backlog, LogContext context)
            {
                if (context.TryFormat(record, _chars, out int written))
                    Written += written;
            }

            public void Lost(long count, LogContext context)
            {
            }

            public void Flush()
            {
            }
        }
    }
}
