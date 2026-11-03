using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Tutan.Functional;

namespace Tutan.Logging.Tests
{
    /// <summary>
    /// A store drained into a <see cref="MemorySink"/>, as <see cref="MemoryLog"/> is, but with the
    /// store's options and clock in the test's hands.
    /// </summary>
    internal sealed class Captured : IDisposable
    {
        private readonly LogDrain _drain;

        public Captured(string rules = "trace", TestClock? clock = null, LogStoreOptions? options = null, LogSinkOptions? sink = null)
        {
            Store = new LogStore(LogRules.Parse(rules).ValueUnsafe(), options ?? new LogStoreOptions(Program: "Tests"), clock is null ? (Func<long>?)null : clock.Read);
            Sink = new MemorySink();
            _drain = new LogDrain(Store, (Sink, sink ?? new LogSinkOptions(LogLevel.Trace)));
        }

        public LogStore Store { get; }

        public Log Log => Store.Log;

        public MemorySink Sink { get; }

        public IReadOnlyList<MemoryRecord> Records
        {
            get
            {
                _drain.Pump();
                return Sink.Records;
            }
        }

        public IReadOnlyList<MemoryRecord> Of(LogEventSpec spec) => Records.Of(spec);

        public void Dispose() => _drain.Dispose();
    }

    /// <summary>Time a test moves by hand, in <see cref="Stopwatch"/> ticks.</summary>
    internal sealed class TestClock
    {
        public long Now { get; private set; } = 1_000_000;

        public long Read() => Now;

        public void Advance(TimeSpan by) => Now += (long)(by.TotalSeconds * Stopwatch.Frequency);
    }

    /// <summary>Stores the reading tests write to and read back, which keep every record.</summary>
    internal static class Stores
    {
        /// <summary>The smallest store there is, which overwrites its records after sixteen.</summary>
        public static readonly LogStoreOptions Tiny = new LogStoreOptions(LogStore.MinRecords, LogStore.MinBits, LogStore.MinRefs, "Tests");

        /// <summary>A store that keeps every level, on <paramref name="clock"/> when one is given.</summary>
        public static LogStore Everything(LogStoreOptions? options = null, TestClock? clock = null) =>
            new LogStore(LogRules.Parse("trace").ValueUnsafe(), options ?? new LogStoreOptions(Program: "Tests"), clock is null ? (Func<long>?)null : clock.Read);

        /// <summary>Runs <paramref name="write"/> on a thread of its own, and waits for it.</summary>
        public static void OnAnotherThread(Action write)
        {
            var thread = new System.Threading.Thread(() => write());
            thread.Start();
            thread.Join();
        }
    }

    internal static class MemoryRecords
    {
        /// <summary>The records of <paramref name="spec"/>: what a test asserts on, whatever else a store said.</summary>
        public static IReadOnlyList<MemoryRecord> Of(this IEnumerable<MemoryRecord> records, LogEventSpec spec) =>
            records.Where(r => r.Event == spec).ToList();
    }
}
