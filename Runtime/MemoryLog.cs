using System;
using System.Collections.Generic;
using Tutan.Functional;

namespace Tutan.Logging
{
    /// <summary>
    /// A log for a test to hand the code it tests, and what that code logged, as values: a store that
    /// captures what its rules say, every level unless told otherwise, drained into a
    /// <see cref="MemorySink"/> each time <see cref="Records"/> is read.
    /// <code>
    /// using var logged = new MemoryLog();
    /// var saves = new SaveManager(folder, logged.Log);
    /// // ...
    /// Assert.That(logged.Events, Has.Some.Matches&lt;MemoryRecord&gt;(r => r.Event == SaveLog.Events.Saved));
    /// </code>
    /// </summary>
    public sealed class MemoryLog : IDisposable
    {
        private readonly MemorySink _memory = new MemorySink();
        private readonly LogDrain _drain;

        /// <summary>A log that captures what <paramref name="rules"/> say, or every level when none are given.</summary>
        public MemoryLog(LogRules? rules = null)
        {
            Store = new LogStore(rules ?? new LogRules(LogLevel.Trace), new LogStoreOptions(Program: "Tests"));
            _drain = new LogDrain(Store, (_memory, new LogSinkOptions(LogLevel.Trace)));
        }

        /// <summary>The store the log writes into, for a test that reads it as a console would.</summary>
        public LogStore Store { get; }

        /// <summary>The handle to pass to the code under test.</summary>
        public Log Log => Store.Log;

        /// <summary>Every record logged so far, in the order it was logged, with what each of a scope's openings and closings says.</summary>
        public IReadOnlyList<MemoryRecord> Records
        {
            get
            {
                _drain.Pump();
                return _memory.Records;
            }
        }

        /// <summary>The events logged so far, without the openings and closings of scopes.</summary>
        public IReadOnlyList<MemoryRecord> Events
        {
            get
            {
                var events = new List<MemoryRecord>();
                foreach (MemoryRecord record in Records)
                {
                    if (record.Kind == LogRecordKind.Event)
                        events.Add(record);
                }

                return events;
            }
        }

        /// <inheritdoc />
        public void Dispose() => _drain.Dispose();
    }

    /// <summary>
    /// Keeps every record it is given, as values, for a test that asserts on what was logged as data:
    /// an event by its spec and a field by its name, rather than a substring of a line.
    /// <code>
    /// MemoryRecord saved = memory.Records.Single(r => r.Event == SaveLog.Events.Saved);
    /// Assert.That(saved.Field("Slot"), Is.EqualTo(3));
    /// </code>
    /// It allocates for every record, which is what a test wants and a program does not.
    /// </summary>
    public sealed class MemorySink : ILogSink
    {
        private readonly object _lock = new object();
        private readonly List<MemoryRecord> _records = new List<MemoryRecord>();
        private long _lost;
        private int _flushes;

        /// <summary>A sink that keeps what it is given.</summary>
        /// <param name="name">What the log calls the sink.</param>
        public MemorySink(string name = "memory") => Name = name;

        /// <inheritdoc />
        public string Name { get; }

        /// <summary>Every record written so far, in the order it was written.</summary>
        public IReadOnlyList<MemoryRecord> Records
        {
            get
            {
                lock (_lock)
                    return Array.AsReadOnly(_records.ToArray());
            }
        }

        /// <summary>How many records it was told were overwritten before it read them.</summary>
        public long Overwritten
        {
            get
            {
                lock (_lock)
                    return _lost;
            }
        }

        /// <summary>How many times it was flushed.</summary>
        public int Flushes
        {
            get
            {
                lock (_lock)
                    return _flushes;
            }
        }

        /// <inheritdoc />
        public void Open(LogContext context)
        {
        }

        /// <inheritdoc />
        public void Write(LogRecord record, bool backlog, LogContext context)
        {
            var fields = new MemoryField[record.FieldCount];
            for (int i = 0; i < fields.Length; i++)
            {
                LogValue value = record.Field(i);
                fields[i] = new MemoryField(value.Field.Name, Box(value), value.ToString());
            }

            var kept = new MemoryRecord(
                record.Sequence, record.Frame, record.Scope, record.Begin, record.Thread, record.Level, record.Kind,
                record.Event, record.Suppressed, backlog, record.Elapsed, record.CallSite, context.Text(record), Array.AsReadOnly(fields), record.Stack);

            lock (_lock)
                _records.Add(kept);
        }

        /// <inheritdoc />
        public void Lost(long count, LogContext context)
        {
            lock (_lock)
                _lost += count;
        }

        /// <inheritdoc />
        public void Flush()
        {
            lock (_lock)
                _flushes++;
        }

        /// <summary>The value as an object: a number, bool, char, guid, date or span as itself, an enum as its <see cref="long"/>, text as a string, an exception as itself, and the program's own type as its text.</summary>
        private static object? Box(LogValue value)
        {
            if (value.IsNone)
                return null;

            return value.Kind switch
            {
                LogValueKind.Boolean => value.Read<bool>(),
                LogValueKind.Char => value.Read<char>(),
                LogValueKind.SByte => value.Read<sbyte>(),
                LogValueKind.Byte => value.Read<byte>(),
                LogValueKind.Int16 => value.Read<short>(),
                LogValueKind.UInt16 => value.Read<ushort>(),
                LogValueKind.Int32 => value.Read<int>(),
                LogValueKind.UInt32 => value.Read<uint>(),
                LogValueKind.Int64 => value.Read<long>(),
                LogValueKind.UInt64 => value.Read<ulong>(),
                LogValueKind.Single => value.Read<float>(),
                LogValueKind.Double => value.Read<double>(),
                LogValueKind.Decimal => value.Read<decimal>(),
                LogValueKind.Guid => value.Read<Guid>(),
                LogValueKind.DateTime => value.Read<DateTime>(),
                LogValueKind.DateTimeOffset => value.Read<DateTimeOffset>(),
                LogValueKind.TimeSpan => value.Read<TimeSpan>(),
                LogValueKind.Enum => value.Read<long>(),
                LogValueKind.String or LogValueKind.Exception => value.AsRef(),
                LogValueKind.Chars => new string(value.AsChars()),
                _ => value.ToString(),
            };
        }
    }

    /// <summary>One record a <see cref="MemorySink"/> kept.</summary>
    /// <param name="Sequence">Its sequence.</param>
    /// <param name="Frame">Its frame.</param>
    /// <param name="Scope">The scope it was under.</param>
    /// <param name="Begin">The scope it opens or closes, or 0.</param>
    /// <param name="Thread">The thread it was written on.</param>
    /// <param name="Level">Its level.</param>
    /// <param name="Kind">An event, or a scope's opening or closing.</param>
    /// <param name="Event">Its spec.</param>
    /// <param name="Suppressed">How many a limit dropped before it.</param>
    /// <param name="Backlog">Whether it was written as an error's context.</param>
    /// <param name="Elapsed">For a scope's closing, how long it was open.</param>
    /// <param name="CallSite">The call that wrote it.</param>
    /// <param name="Message">Its message, with the sink's redaction; for a scope's closing, its opening's.</param>
    /// <param name="Fields">Its fields.</param>
    /// <param name="Stack">The stack it was written from, when it was at its event's stack level.</param>
    public sealed record MemoryRecord(
        long Sequence, long Frame, long Scope, long Begin, int Thread, LogLevel Level, LogRecordKind Kind, LogEventSpec Event,
        int Suppressed, bool Backlog, TimeSpan Elapsed, LogCallSite CallSite, string Message, IReadOnlyList<MemoryField> Fields,
        Optional<LogStack> Stack = default)
    {
        /// <summary>The value of the field named <paramref name="name"/>, ignoring case.</summary>
        /// <exception cref="KeyNotFoundException">It has no such field.</exception>
        public object? Field(string name)
        {
            foreach (MemoryField field in Fields)
            {
                if (string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))
                    return field.Value;
            }

            throw new KeyNotFoundException($"{Event.Key} has no field {name}.");
        }

        /// <summary>The message.</summary>
        public override string ToString() => Message;
    }

    /// <summary>One field of a <see cref="MemoryRecord"/>.</summary>
    /// <param name="Name">The field's name.</param>
    /// <param name="Value">The value, as <see cref="MemorySink"/> boxes it.</param>
    /// <param name="Text">The value formatted.</param>
    public sealed record MemoryField(string Name, object? Value, string Text);
}
