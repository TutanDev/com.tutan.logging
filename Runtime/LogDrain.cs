using System;
using System.Collections.Generic;

namespace Tutan.Logging
{
    /// <summary>
    /// Where records leave the program: a sink receives every record a drain reads from the store, in
    /// sequence order and on the thread that pumps the drain, and writes the ones its options let
    /// through. A sink that throws is said, as a record, and is not written to again; the program goes on.
    /// </summary>
    public interface ILogSink
    {
        /// <summary>What the sink is called where the log speaks of it.</summary>
        string Name { get; }

        /// <summary>Called once, before anything else, with what it will be written with.</summary>
        void Open(LogContext context);

        /// <summary>Writes one record.</summary>
        /// <param name="record">The record.</param>
        /// <param name="backlog">
        /// Whether it is written as context for an error: a record below the sink's level, written
        /// because an error after it shares its scope.
        /// </param>
        /// <param name="context">The clock, the open scopes, and what the sink redacts.</param>
        void Write(LogRecord record, bool backlog, LogContext context);

        /// <summary>Says that <paramref name="count"/> records were overwritten before the drain read them, at this point in the sequence.</summary>
        void Lost(long count, LogContext context);

        /// <summary>Makes what was written durable, as far as the sink can.</summary>
        void Flush();
    }

    /// <summary>What one sink of a drain writes.</summary>
    /// <param name="Least">The least level it writes.</param>
    /// <param name="Backlog">
    /// How many records below <paramref name="Least"/> it writes before an error as its context, at
    /// most: the latest ones that share the error's scope, or, for an error under no scope, the latest
    /// under none. 0 for none.
    /// </param>
    /// <param name="Redact">What it leaves out of the fields.</param>
    public sealed record LogSinkOptions(LogLevel Least = LogLevel.Information, int Backlog = 0, LogRedaction Redact = LogRedaction.None);

    /// <summary>What a sink writes with: the records' clock and program, the scopes open, and what it redacts.</summary>
    public sealed class LogContext
    {
        private readonly ScopeTable _scopes;

        internal LogContext(LogRecords records, LogSinkOptions options, ScopeTable scopes)
        {
            Clock = records.Clock;
            Program = records.Program;
            Options = options;
            Redact = options.Redact | records.Redacted;
            _scopes = scopes;
        }

        /// <summary>The clock of the records' store, or of the session they were read back from.</summary>
        public LogClock Clock { get; }

        /// <summary>The program that wrote the records.</summary>
        public string Program { get; }

        /// <summary>The sink's options.</summary>
        public LogSinkOptions Options { get; }

        /// <summary>
        /// What the sink leaves out: what its options say, and what the records it writes already lack,
        /// as a file read back with its personal fields left out does, so what it writes says so too.
        /// </summary>
        public LogRedaction Redact { get; }

        /// <summary>
        /// The record that opened <paramref name="scope"/>, if the drain read it and the scope has not
        /// closed: so a sink can say what a record is under, and what a closing closes.
        /// </summary>
        public bool TryScope(long scope, out LogRecord begin) => _scopes.TryGet(scope, out begin);

        /// <summary>
        /// Writes a record's message with the sink's redaction: for a scope's closing, its opening's
        /// message when the drain has it.
        /// </summary>
        public bool TryFormat(LogRecord record, Span<char> destination, out int written)
        {
            if (record.Kind == LogRecordKind.ScopeEnd && TryScope(record.Begin, out LogRecord begin))
                return begin.TryFormat(destination, out written, Redact);

            return record.TryFormat(destination, out written, Redact);
        }

        /// <summary>The message as a string, for a sink that keeps strings.</summary>
        public string Text(LogRecord record) =>
            record.Kind == LogRecordKind.ScopeEnd && TryScope(record.Begin, out LogRecord begin)
                ? LogText.ToText(begin, Redact)
                : LogText.ToText(record, Redact);
    }

    /// <summary>
    /// Reads a store and hands what it reads to its sinks, when it is pumped: once a frame from the
    /// PlayerLoop, by <see cref="LogPump"/>, or from wherever the host chooses. There is no thread of
    /// its own, since WebGL has none. It owns its sinks, and disposes those that are disposable when
    /// it is disposed.
    /// </summary>
    public sealed class LogDrain : IDisposable
    {
        private readonly LogStore _store;
        private readonly Sink[] _sinks;
        private readonly ScopeTable _scopes = new ScopeTable();
        private readonly LogBatch _batch = new LogBatch();
        private readonly LogBatch _backlog;
        private readonly object _draining = new object();
        private long _next;
        private bool _disposed;

        /// <summary>A drain of <paramref name="store"/> into <paramref name="sinks"/>, each with what it writes, which reads from the oldest record the store still holds.</summary>
        /// <exception cref="ArgumentException">A sink is given twice.</exception>
        public LogDrain(LogStore store, params (ILogSink Sink, LogSinkOptions Options)[] sinks)
        {
            if (store is null)
                throw new ArgumentNullException(nameof(store));
            if (sinks is null)
                throw new ArgumentNullException(nameof(sinks));

            _store = store;
            _next = store.First;
            _sinks = new Sink[sinks.Length];
            int backlog = 1;
            for (int i = 0; i < sinks.Length; i++)
            {
                (ILogSink target, LogSinkOptions options) = sinks[i];
                if (target is null)
                    throw new ArgumentNullException(nameof(sinks), $"The sink at {i} is null.");
                if (options is null)
                    throw new ArgumentNullException(nameof(sinks), $"The options of {target.Name} are null.");
                if (options.Backlog < 0)
                    throw new ArgumentOutOfRangeException(nameof(sinks), $"The backlog of {target.Name} is {options.Backlog}, which is less than none.");
                for (int j = 0; j < i; j++)
                {
                    if (ReferenceEquals(_sinks[j].Target, target))
                        throw new ArgumentException($"The sink {target.Name} is given twice, and would write every record twice.", nameof(sinks));
                }

                _sinks[i] = new Sink(target, options, new LogContext(store, options, _scopes));
                backlog = Math.Max(backlog, options.Backlog);
            }

            _backlog = new LogBatch(backlog);

            foreach (Sink sink in _sinks)
                Guard(sink, static (s, _) => s.Target.Open(s.Context), 0);
        }

        /// <summary>The store it reads.</summary>
        public LogStore Store => _store;

        /// <summary>Reads what arrived since the last read and hands it to the sinks, then flushes them: once a frame.</summary>
        /// <exception cref="ObjectDisposedException">The drain was disposed.</exception>
        public void Pump()
        {
            lock (_draining)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(LogDrain), "The drain was disposed, and has nothing left to read into.");

                Drain();
            }
        }

        /// <summary>
        /// Everything the store holds, to the sinks, now, on the caller's thread, and flushed: for a
        /// crash or an exit. Once the drain is disposed it has nothing left to do, so a handler that
        /// flushes after another disposed it does no harm.
        /// </summary>
        public void Flush()
        {
            lock (_draining)
            {
                if (!_disposed)
                    Drain();
            }
        }

        /// <summary>
        /// Drains and flushes what is left, and disposes the sinks: under the lock a pump or a flush
        /// takes, so one on another thread never writes to a sink as it is disposed.
        /// </summary>
        public void Dispose()
        {
            lock (_draining)
            {
                if (_disposed)
                    return;

                Drain();
                _disposed = true;
                foreach (Sink sink in _sinks)
                {
                    if (sink.Target is IDisposable)
                        Guard(sink, static (s, _) => ((IDisposable)s.Target).Dispose(), 0);
                }
            }
        }

        private void Drain()
        {
            lock (_draining)
            {
                long until = _store.Next;
                while (_next < until)
                {
                    LogRead read = _store.Read(_next, _batch);
                    if (read.Lost > 0)
                    {
                        foreach (Sink sink in _sinks)
                            Guard(sink, static (s, lost) => s.Target.Lost(lost, s.Context), read.Lost);
                        _store.Log.DrainFellBehind(read.Lost);
                    }

                    for (int i = 0; i < _batch.Count; i++)
                        Dispatch(i);

                    _next = read.Next;
                    if (read.Count == 0)
                        break;
                }

                foreach (Sink sink in _sinks)
                    Guard(sink, static (s, _) => s.Target.Flush(), 0);
            }
        }

        private void Dispatch(int index)
        {
            LogRecord record = _batch[index];
            if (record.Kind == LogRecordKind.ScopeBegin)
                _scopes.Open(_batch, index);

            // A scope's opening is context for what happens in it, so it is under its own root, not its parent's.
            long root = _scopes.Root(record.Kind == LogRecordKind.ScopeBegin ? record.Sequence : record.Scope);
            foreach (Sink sink in _sinks)
            {
                if (sink.Failed)
                    continue;

                if (record.Level >= sink.Options.Least)
                {
                    if (sink.Skipped is not null && record.Level >= LogLevel.Error)
                        WriteBacklog(sink, root);

                    Write(sink, record, backlog: false);
                }
                else if (record.Kind != LogRecordKind.ScopeEnd)
                {
                    // A scope's closing is not what led to an error: by the time the error's context
                    // is written, the scope is gone, and its closing would say only that it ended.
                    sink.Skipped?.Add(record.Sequence, root);
                }
            }

            if (record.Kind == LogRecordKind.ScopeEnd)
                _scopes.Close(record.Begin);
        }

        /// <summary>Writes, before an error, the latest records the sink skipped that share the error's scope.</summary>
        private void WriteBacklog(Sink sink, long root)
        {
            int count = sink.Skipped!.Take(root, sink.Options.Backlog, sink.Sequences);
            if (count == 0)
                return;

            _store.Read(new ReadOnlySpan<long>(sink.Sequences, 0, count), _backlog);
            for (int i = 0; i < _backlog.Count && !sink.Failed; i++)
                Write(sink, _backlog[i], backlog: true);
        }

        private void Write(Sink sink, LogRecord record, bool backlog)
        {
            try
            {
                sink.Target.Write(record, backlog, sink.Context);
            }
            catch (Exception exception)
            {
                Fail(sink, exception);
            }
        }

        private void Guard<T>(Sink sink, Action<Sink, T> act, T argument)
        {
            if (sink.Failed)
                return;

            try
            {
                act(sink, argument);
            }
            catch (Exception exception)
            {
                Fail(sink, exception);
            }
        }

        private void Fail(Sink sink, Exception exception)
        {
            sink.Failed = true;
            _store.Log.SinkFailed(sink.Target.Name, exception);
        }

        private sealed class Sink
        {
            public Sink(ILogSink target, LogSinkOptions options, LogContext context)
            {
                Target = target;
                Options = options;
                Context = context;
                Skipped = options.Backlog > 0 ? new SkippedRing(options.Backlog * 4) : null;
                Sequences = new long[options.Backlog];
            }

            public ILogSink Target { get; }

            public LogSinkOptions Options { get; }

            public LogContext Context { get; }

            public SkippedRing? Skipped { get; }

            public long[] Sequences { get; }

            public bool Failed { get; set; }
        }
    }

    /// <summary>
    /// The records a sink skipped lately, by sequence and by the outermost scope they were under,
    /// which is what says whether one is an error's context: two records share a scope exactly when
    /// they share the outermost one.
    /// </summary>
    internal sealed class SkippedRing
    {
        private readonly long[] _sequences;
        private readonly long[] _roots;
        private int _count;
        private int _head;

        public SkippedRing(int capacity)
        {
            _sequences = new long[capacity];
            _roots = new long[capacity];
        }

        public void Add(long sequence, long root)
        {
            _sequences[_head] = sequence;
            _roots[_head] = root;
            _head = (_head + 1) % _sequences.Length;
            _count = Math.Min(_count + 1, _sequences.Length);
        }

        /// <summary>
        /// Puts into <paramref name="into"/>, oldest first, the latest <paramref name="most"/> records
        /// under <paramref name="root"/>, says how many, and forgets every record held, so none is
        /// written as context twice.
        /// </summary>
        public int Take(long root, int most, long[] into)
        {
            int found = 0;
            for (int i = 0; i < _count && found < most; i++)
            {
                int at = (_head - 1 - i + _sequences.Length) % _sequences.Length;
                if (_roots[at] == root)
                    into[found++] = _sequences[at];
            }

            Array.Reverse(into, 0, found);
            _count = 0;
            return found;
        }
    }

    /// <summary>The scopes a drain has seen open and not yet close, each with a copy of the record that opened it.</summary>
    internal sealed class ScopeTable
    {
        private readonly Dictionary<long, LogBatch> _open = new Dictionary<long, LogBatch>();
        private readonly Stack<LogBatch> _free = new Stack<LogBatch>();

        public void Open(LogBatch batch, int index)
        {
            LogBatch kept = _free.Count > 0 ? _free.Pop() : new LogBatch(1);
            kept.CopyFrom(batch, index);
            _open[batch[index].Sequence] = kept;
        }

        public void Close(long scope)
        {
            if (_open.Remove(scope, out LogBatch? kept))
            {
                kept.Clear();
                _free.Push(kept);
            }
        }

        public bool TryGet(long scope, out LogRecord begin)
        {
            if (scope != 0 && _open.TryGetValue(scope, out LogBatch? kept))
            {
                begin = kept[0];
                return true;
            }

            begin = default;
            return false;
        }

        /// <summary>The outermost scope known in the chain from <paramref name="scope"/>, or <paramref name="scope"/> itself when its opening was not seen.</summary>
        public long Root(long scope)
        {
            long root = scope;
            while (scope != 0 && _open.TryGetValue(scope, out LogBatch? kept))
            {
                root = scope;
                scope = kept[0].Scope;
            }

            return root;
        }
    }
}
