using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using Tutan.Functional;

namespace Tutan.Logging
{
    /// <summary>
    /// Where a program's records are held: three rings of fixed size, allocated once, of records, of
    /// the bits of their values, and of the strings and exceptions they keep by reference. A writer
    /// never waits for a reader: when the rings are full the oldest records are overwritten, and since
    /// every reader reads by sequence, a reader that fell behind knows how many it lost.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A record is written under a lock, held for the copy of its fields and nothing else: no user
    /// code and no allocation run under it. The timestamp is taken under it too, so sequences are in
    /// the order of time.
    /// </para>
    /// <para>
    /// The composition root makes a store, hands its <see cref="Log"/> to the code it composes, and
    /// owns it: there is no store the package makes on its own.
    /// </para>
    /// </remarks>
    public sealed class LogStore : LogRecords
    {
        /// <summary>The fewest records a store holds.</summary>
        public const int MinRecords = 16;

        /// <summary>The fewest bits a store holds: twice the biggest record an event can make, so any record fits.</summary>
        public const int MinBits = 32_768;

        /// <summary>The fewest refs a store holds.</summary>
        public const int MinRefs = 64;

        /// <summary>The store <see cref="Tutan.Logging.Log.Discard"/> is a handle to: it holds nothing, and every event is off in it.</summary>
        internal static readonly LogStore Discarding = new LogStore();

        private readonly object _lock = new object();
        private readonly object _compiling = new object();
        private readonly LogEntry[] _records;
        private readonly long[] _bits;
        private readonly object?[] _refs;
        private readonly Dictionary<long, OpenScope> _open = new Dictionary<long, OpenScope>();
        private readonly Func<long>? _clock;
        private readonly bool _discards;

        private long _next = 1;
        private long _first = 1;
        private uint _bitsHead;
        private uint _refsHead;
        private long _frame;
        private EventState[] _events = Array.Empty<EventState>();
        private int[] _perSecond = Array.Empty<int>();
        private byte[] _least = Array.Empty<byte>();
        private LogRules _rules;
        private int _conflictsSaid;

        /// <summary>A store capturing what <paramref name="rules"/> say, with rings of the sizes <paramref name="options"/> gives.</summary>
        /// <exception cref="ArgumentOutOfRangeException">A ring is smaller than the least a store holds.</exception>
        public LogStore(LogRules rules, LogStoreOptions? options = null)
            : this(rules, options ?? new LogStoreOptions(), null)
        {
        }

        /// <summary>A store whose timestamps come from <paramref name="clock"/>, in <see cref="Stopwatch"/> ticks, for a test that moves time itself.</summary>
        internal LogStore(LogRules rules, LogStoreOptions options, Func<long>? clock)
        {
            if (rules is null)
                throw new ArgumentNullException(nameof(rules));
            if (options.Records < MinRecords)
                throw new ArgumentOutOfRangeException(nameof(options), $"A store holds at least {MinRecords} records, and {options.Records} were asked for.");
            if (options.Bits < MinBits)
                throw new ArgumentOutOfRangeException(nameof(options), $"A store holds at least {MinBits} bits, and {options.Bits} were asked for.");
            if (options.Refs < MinRefs)
                throw new ArgumentOutOfRangeException(nameof(options), $"A store holds at least {MinRefs} refs, and {options.Refs} were asked for.");

            _clock = clock;
            _records = new LogEntry[RoundUpToPowerOf2(options.Records)];
            _bits = new long[RoundUpToPowerOf2(options.Bits)];
            _refs = new object?[RoundUpToPowerOf2(options.Refs)];
            _rules = rules;

            Program = options.Program ?? DefaultProgram();
            Clock = new LogClock(DateTimeOffset.Now, Now());
            Apply(rules);
        }

        /// <summary>The store <see cref="Discarding"/> is: no rings, and no event on.</summary>
        private LogStore()
        {
            _discards = true;
            _records = Array.Empty<LogEntry>();
            _bits = Array.Empty<long>();
            _refs = Array.Empty<object?>();
            _rules = LogRules.Default;
            Program = "Discard";
            Clock = new LogClock(DateTimeOffset.Now, Stopwatch.GetTimestamp());
        }

        /// <summary>The handle the program passes to what logs: records written through it are under no scope.</summary>
        public Log Log => new Log(this, 0);

        /// <summary>The program's name, which a file sink writes first and names its file by.</summary>
        public override string Program { get; }

        /// <summary>When the store started, which turns a record's timestamp into a time.</summary>
        public override LogClock Clock { get; }

        /// <summary>The catalog's events: every event the program declares, which is every event a record can be of.</summary>
        public override IReadOnlyList<LogEventSpec> Events => LogCatalog.Events;

        /// <summary>The rules applied last.</summary>
        public LogRules Rules => Volatile.Read(ref _rules);

        /// <summary>How many records the rings hold at most, which is the size asked for rounded up to a power of two.</summary>
        public override int Capacity => _records.Length;

        /// <summary>The frame every record written now is stamped with, 0 until the host first calls <see cref="EnterFrame"/>.</summary>
        public long Frame => Volatile.Read(ref _frame);

        /// <summary>The oldest sequence the store still holds.</summary>
        public override long First
        {
            get
            {
                lock (_lock)
                    return _first;
            }
        }

        /// <summary>The sequence the next record gets; one past the newest.</summary>
        public override long Next
        {
            get
            {
                lock (_lock)
                    return _next;
            }
        }

        /// <summary>Nothing: a store holds every record as it was written.</summary>
        public override LogRedaction Redacted => LogRedaction.None;

        /// <summary>None placed late: a store numbers its records as it writes them, so they are always in order.</summary>
        public override LogLateMark LateMark => default;

        /// <summary><see cref="Next"/>, with the <c>default</c> mark: there is nothing to read again.</summary>
        public override LogLate LateFrom(LogLateMark seen) => new LogLate(Next, default);

        /// <summary>What <see cref="Tutan.Logging.Log.IsEnabled(LogEventSpec)"/> reads: one byte per spec, the least level it is captured at.</summary>
        internal byte[] Least => _least;

        /// <summary>
        /// Captures what <paramref name="rules"/> say from now on, from any thread, and says as a
        /// record each rule whose pattern matches nothing the program declares.
        /// </summary>
        public void Apply(LogRules rules)
        {
            if (rules is null)
                throw new ArgumentNullException(nameof(rules));
            if (_discards)
                return;

            LogEventSpec[] catalog = Compile(rules);
            foreach (LogRule rule in rules.MatchingNothing(catalog))
                Log.RuleMatchesNothing(rule.ToString());
        }

        /// <summary>
        /// Stamps every record written from now on with <paramref name="frame"/>, on any thread. The
        /// pump calls it at the start of every frame with <c>Time.frameCount</c>.
        /// </summary>
        public void EnterFrame(long frame) => Volatile.Write(ref _frame, frame);

        /// <summary>
        /// Copies the records from <paramref name="from"/> on into <paramref name="into"/>, as many as
        /// fit, which it empties first. The read says the first sequence it copied, which is past
        /// <paramref name="from"/> by the number overwritten before they were read.
        /// </summary>
        public override LogRead Read(long from, LogBatch into)
        {
            if (into is null)
                throw new ArgumentNullException(nameof(into));

            into.Clear();
            long start;
            long sequence;
            lock (_lock)
            {
                start = Math.Max(from, _first);
                for (sequence = start; sequence < _next; sequence++)
                {
                    ref LogEntry entry = ref _records[sequence & (_records.Length - 1)];
                    if (!into.TryAdd(entry, BitsOf(entry), RefsOf(entry)))
                        break;
                }
            }

            into.Seal(LogCatalog.EventArray);
            return new LogRead(start, (int)(sequence - start), sequence, Math.Max(0, start - from));
        }

        /// <summary>
        /// Copies the records of <paramref name="sequences"/> the store still holds into
        /// <paramref name="into"/>, in the order given, as many as fit, and says how many. The record
        /// that opened a scope still open is held however old it is.
        /// </summary>
        public override int Read(ReadOnlySpan<long> sequences, LogBatch into)
        {
            if (into is null)
                throw new ArgumentNullException(nameof(into));

            into.Clear();
            lock (_lock)
            {
                foreach (long sequence in sequences)
                {
                    bool added;
                    if (sequence >= _first && sequence < _next)
                    {
                        ref LogEntry entry = ref _records[sequence & (_records.Length - 1)];
                        added = into.TryAdd(entry, BitsOf(entry), RefsOf(entry));
                    }
                    else if (_open.TryGetValue(sequence, out OpenScope open) && open.Kept is { } kept)
                    {
                        added = into.TryAdd(kept.Entry, kept.Bits, kept.Refs);
                    }
                    else
                    {
                        continue;
                    }

                    if (!added)
                        break;
                }
            }

            into.Seal(LogCatalog.EventArray);
            return into.Count;
        }

        /// <summary>
        /// The way out of <see cref="Tutan.Logging.Log.IsEnabled(LogEventSpec)"/> when a spec is past the
        /// table: one registered after the table was compiled, or one never registered, which this
        /// registers. For <see cref="Discarding"/>, every spec, and every one is off.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal bool Refresh(LogEventSpec spec, byte level)
        {
            // A file's spec is its archive's alone, and a store captures only what this program declares.
            if (_discards || spec.Origin == LogSpecOrigin.File)
                return false;
            if (spec.Index < 0)
                LogCatalog.Register(spec);

            Compile(Rules);
            byte[] least = _least;
            return (uint)spec.Index < (uint)least.Length && level >= least[spec.Index];
        }

        /// <summary>Reserves a record and the slots of its fields, and returns the writer that fills them with the lock held.</summary>
        /// <remarks>
        /// Not inlined: every event's generated method calls it, and one copy of the write path is
        /// smaller and faster than a copy in every event.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal LogWriter Open(Log from, LogEventSpec spec, LogLevel level, int chars, bool scope, string callerFile, int callerLine)
        {
            if (_discards)
                return LogWriter.Dropped(from);
            if (spec.Kind != (scope ? LogEventKind.Scope : LogEventKind.Event))
                ThrowWrongKind(spec, scope);
            if (level > LogLevel.Critical)
                ThrowNotALevel(spec, level);
            if (spec.Level.HasValue(out LogLevel declared) && level != declared)
                ThrowWrongLevel(spec, level, declared);
            if (spec.Index < 0)
            {
                if (spec.Origin == LogSpecOrigin.File)
                    ThrowOfAFile(spec);
                LogCatalog.Register(spec);
            }

            int thread = Environment.CurrentManagedThreadId;
            long under = from.Scope;
            int bits = spec.FixedSlots + chars;
            int refs = spec.Refs;
            int index = spec.Index;
            if (chars < 0 || bits > Math.Min(_bits.Length / 2, ushort.MaxValue))
                ThrowTooBig(spec, chars);

            Monitor.Enter(_lock);
            long timestamp = Now();

            int suppressed = 0;
            if ((uint)index < (uint)_events.Length)
            {
                ref EventState state = ref _events[index];
                int limit = _perSecond[index];
                if (limit > 0 && !state.Take(limit, timestamp))
                {
                    state.Suppressed++;
                    Monitor.Exit(_lock);
                    return LogWriter.Dropped(from);
                }

                suppressed = state.Suppressed;
                state.Suppressed = 0;
            }

            long sequence = _next++;
            Reserve(bits, refs, sequence, out uint bitsAt, out uint refsAt);
            _records[sequence & (_records.Length - 1)] = new LogEntry
            {
                Sequence = sequence,
                Timestamp = timestamp,
                Frame = Volatile.Read(ref _frame),
                Scope = under,
                CallerFile = callerFile,
                Event = index,
                Thread = thread,
                BitsAt = bitsAt,
                RefsAt = refsAt,
                Suppressed = suppressed,
                CallerLine = callerLine,
                Bits = (ushort)bits,
                Refs = (byte)refs,
                Level = level,
                Kind = scope ? LogRecordKind.ScopeBegin : LogRecordKind.Event,
            };

            if (scope)
                _open[sequence] = new OpenScope(spec, level, under, timestamp, callerFile, callerLine);

            return new LogWriter(
                this,
                _bits.AsSpan((int)(bitsAt & (uint)(_bits.Length - 1)), bits),
                _refs.AsSpan((int)(refsAt & (uint)(_refs.Length - 1)), refs),
                from,
                scope ? sequence : 0);
        }

        /// <summary>Lets the lock go once a record is written.</summary>
        internal void Committed() => Monitor.Exit(_lock);

        /// <summary>
        /// Writes the record that closes scope <paramref name="id"/>: the opening's sequence and
        /// timestamp, and no fields. A scope already closed is left alone, so it closes once.
        /// </summary>
        internal void Close(long id)
        {
            if (_discards)
                return;

            int thread = Environment.CurrentManagedThreadId;
            lock (_lock)
            {
                if (!_open.Remove(id, out OpenScope open))
                    return;

                long sequence = _next++;
                Reserve(2, 0, sequence, out uint bitsAt, out uint refsAt);
                _records[sequence & (_records.Length - 1)] = new LogEntry
                {
                    Sequence = sequence,
                    Timestamp = Now(),
                    Frame = Volatile.Read(ref _frame),
                    Scope = open.Parent,
                    CallerFile = open.CallerFile,
                    Event = open.Spec.Index,
                    Thread = thread,
                    BitsAt = bitsAt,
                    RefsAt = refsAt,
                    CallerLine = open.CallerLine,
                    Bits = 2,
                    Level = open.Level,
                    Kind = LogRecordKind.ScopeEnd,
                };

                int at = (int)(bitsAt & (uint)(_bits.Length - 1));
                _bits[at] = id;
                _bits[at + 1] = open.Began;
            }
        }

        private static void ThrowWrongKind(LogEventSpec spec, bool scope) =>
            throw new ArgumentException($"{spec.Key} is a{(scope ? "n event" : " scope")}, and it was written as a{(scope ? " scope" : "n event")}.", nameof(spec));

        // A record at None no view shows, and a reader skips its line.
        private static void ThrowNotALevel(LogEventSpec spec, LogLevel level) =>
            throw new ArgumentOutOfRangeException(nameof(level), $"A record of {spec.Key} was written at {level}, and a record is at a level from Trace to Critical.");

        private static void ThrowOfAFile(LogEventSpec spec) =>
            throw new ArgumentException($"{spec.Key} was read back from a file, and a store writes only the events this program declares.", nameof(spec));

        // A file says a record's level beside its event's, and a reader skips a record whose two disagree.
        private static void ThrowWrongLevel(LogEventSpec spec, LogLevel level, LogLevel declared) =>
            throw new ArgumentException($"{spec.Key} is declared at {declared}, and it was written at {level}.", nameof(level));

        private static void ThrowTooBig(LogEventSpec spec, int chars) =>
            throw new ArgumentOutOfRangeException(nameof(chars), $"A record of {spec.Key} with {chars} slots of chars does not fit the store's rings.");

        private long Now() => _clock is null ? Stopwatch.GetTimestamp() : _clock();

        /// <summary>Compiles the rules with the catalog as it is now into the table the calls read, and says the conflicts not said yet.</summary>
        private LogEventSpec[] Compile(LogRules rules)
        {
            LogEventSpec[] catalog;
            lock (_compiling)
            {
                catalog = LogCatalog.EventArray;
                CompiledRules compiled = rules.Compile(catalog);
                var events = new EventState[catalog.Length];

                lock (_lock)
                {
                    Array.Copy(_events, events, Math.Min(_events.Length, events.Length));
                    _events = events;
                    _perSecond = compiled.PerSecond;
                    Volatile.Write(ref _rules, rules);
                }

                Volatile.Write(ref _least, compiled.Least);
            }

            SayConflicts();
            return catalog;
        }

        /// <summary>Says, as records, the catalog's conflicts this store has not said yet.</summary>
        private void SayConflicts()
        {
            IReadOnlyList<LogCatalogConflict> conflicts = LogCatalog.Conflicts;
            int from = Interlocked.Exchange(ref _conflictsSaid, conflicts.Count);
            for (int i = from; i < conflicts.Count; i++)
            {
                switch (conflicts[i])
                {
                    case LogCatalogConflict.KeyTwice twice:
                        Log.KeyTaken(twice.Key, $"{twice.First.Type} in {twice.First.Assembly}", $"{twice.Second.Type} in {twice.Second.Assembly}");
                        break;
                    case LogCatalogConflict.CategoryTwice twice:
                        Log.CategoryTaken(twice.Category, $"{twice.First.Type} in {twice.First.Assembly}", $"{twice.Second.Type} in {twice.Second.Assembly}");
                        break;
                }
            }
        }

        private ReadOnlySpan<long> BitsOf(in LogEntry entry) =>
            _bits.AsSpan((int)(entry.BitsAt & (uint)(_bits.Length - 1)), entry.Bits);

        private ReadOnlySpan<object?> RefsOf(in LogEntry entry) =>
            _refs.AsSpan((int)(entry.RefsAt & (uint)(_refs.Length - 1)), entry.Refs);

        /// <summary>
        /// Finds room for a record's slots, each kind contiguous, starting a kind again at the front of
        /// its ring rather than wrapping it, and forgets every record whose slots that room overwrites.
        /// A record that opened a scope still open is copied aside first, so the scope keeps its fields.
        /// </summary>
        private void Reserve(int bits, int refs, long sequence, out uint bitsAt, out uint refsAt)
        {
            bitsAt = Contiguous(_bitsHead, bits, _bits.Length);
            refsAt = Contiguous(_refsHead, refs, _refs.Length);
            uint bitsEnd = bitsAt + (uint)bits;
            uint refsEnd = refsAt + (uint)refs;

            while (_first < sequence)
            {
                ref LogEntry oldest = ref _records[_first & (_records.Length - 1)];
                bool overwritten = sequence - _first >= _records.Length
                    || bitsEnd - oldest.BitsAt > (uint)_bits.Length
                    || refsEnd - oldest.RefsAt > (uint)_refs.Length;
                if (!overwritten)
                    break;

                if (oldest.Kind == LogRecordKind.ScopeBegin && _open.TryGetValue(oldest.Sequence, out OpenScope open) && open.Kept is null)
                    _open[oldest.Sequence] = open.Keeping(new KeptRecord(oldest, BitsOf(oldest).ToArray(), RefsOf(oldest).ToArray()));

                // What the ring forgets it lets go of, so a string or an exception it kept is not held past its record.
                _refs.AsSpan((int)(oldest.RefsAt & (uint)(_refs.Length - 1)), oldest.Refs).Clear();
                _first++;
            }

            _bitsHead = bitsEnd;
            _refsHead = refsEnd;
        }

        private static uint Contiguous(uint head, int count, int length)
        {
            int offset = (int)(head & (uint)(length - 1));
            return offset + count > length ? head + (uint)(length - offset) : head;
        }

        private static int RoundUpToPowerOf2(int value)
        {
            int power = 1;
            while (power < value)
                power <<= 1;
            return power;
        }

        /// <summary>The game's product name, read where Unity allows it, or <c>Program</c> where it does not.</summary>
        private static string DefaultProgram()
        {
            try
            {
                string name = UnityEngine.Application.productName;
                return string.IsNullOrEmpty(name) ? "Program" : name;
            }
            catch (UnityEngine.UnityException)
            {
                return "Program";
            }
        }

        /// <summary>A limit's bucket for one event, and how many records of it the limit dropped since the last one kept.</summary>
        private struct EventState
        {
            private double _tokens;
            private long _refilled;
            private bool _started;

            public int Suppressed;

            /// <summary>Takes a token if there is one, after adding what the time since the last refill earned. A bucket starts full.</summary>
            public bool Take(int limit, long now)
            {
                if (!_started)
                {
                    _started = true;
                    _tokens = limit;
                }
                else
                {
                    double earned = (now - _refilled) * (double)limit / Stopwatch.Frequency;
                    _tokens = Math.Min(limit, _tokens + earned);
                }

                _refilled = now;
                if (_tokens < 1)
                    return false;

                _tokens -= 1;
                return true;
            }
        }
    }

    /// <summary>The sizes of a store's rings, each rounded up to a power of two, and the program's name.</summary>
    /// <param name="Records">How many records it holds: 72 bytes each.</param>
    /// <param name="Bits">How many 8-byte slots of values it holds.</param>
    /// <param name="Refs">How many strings and exceptions it holds.</param>
    /// <param name="Program">The program's name, or null for <c>Application.productName</c>.</param>
    /// <remarks>The defaults hold about 2 MB: some sixteen thousand records, a few frames' worth of a busy game or minutes of a quiet one.</remarks>
    public sealed record LogStoreOptions(int Records = 16_384, int Bits = 65_536, int Refs = 16_384, string? Program = null);

    /// <summary>What a read copied.</summary>
    /// <param name="First">The first sequence copied, or where the read started when it copied none.</param>
    /// <param name="Count">How many were copied.</param>
    /// <param name="Next">The sequence to read from next: one past the last copied, since a store's sequences have no holes.</param>
    /// <param name="Lost">How many records were overwritten, after where the read was asked to start, before they were read.</param>
    public readonly record struct LogRead(long First, int Count, long Next, long Lost);

    /// <summary>Turns a record's timestamp into a time, from when the store started.</summary>
    /// <param name="Started">The wall clock when the store started.</param>
    /// <param name="StartedAt">The <see cref="Stopwatch"/> timestamp when the store started.</param>
    public readonly record struct LogClock(DateTimeOffset Started, long StartedAt)
    {
        private static readonly double s_ticksPerTimestamp = (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;

        /// <summary>When <paramref name="timestamp"/> was, on the wall clock.</summary>
        public DateTimeOffset At(long timestamp) => Started + Between(StartedAt, timestamp);

        /// <summary>How long it was from <paramref name="from"/> to <paramref name="to"/>.</summary>
        public static TimeSpan Between(long from, long to) => new TimeSpan((long)((to - from) * s_ticksPerTimestamp));
    }

    /// <summary>
    /// One record as the rings hold it, and as a batch and an archive copy it. In the rings, a
    /// record's bits and refs are where <see cref="BitsAt"/> and <see cref="RefsAt"/> say, positions
    /// that only grow and wrap at 2^32, so a ring position is one mask away and an overwrite is one
    /// subtraction away; a copy holds them where its own buffers do.
    /// </summary>
    internal struct LogEntry
    {
        public long Sequence;
        public long Timestamp;
        public long Frame;
        public long Scope;

        // The call site's file is a constant the compiler wrote into the calling assembly, so holding
        // it costs a reference and allocates nothing.
        public string? CallerFile;
        public int Event;
        public int Thread;
        public uint BitsAt;
        public uint RefsAt;
        public int Suppressed;
        public int CallerLine;
        public ushort Bits;
        public byte Refs;
        public LogLevel Level;
        public LogRecordKind Kind;

        // Written as an error's backlog, which a store's record never is: a session's record, as its sink was handed it.
        public bool Backlog;
    }

    /// <summary>A scope that is open: what its closing record needs, and a copy of its opening if the rings overwrote it.</summary>
    internal readonly struct OpenScope
    {
        public OpenScope(LogEventSpec spec, LogLevel level, long parent, long began, string callerFile, int callerLine)
            : this(spec, level, parent, began, callerFile, callerLine, null)
        {
        }

        private OpenScope(LogEventSpec spec, LogLevel level, long parent, long began, string callerFile, int callerLine, KeptRecord? kept)
        {
            Spec = spec;
            Level = level;
            Parent = parent;
            Began = began;
            CallerFile = callerFile;
            CallerLine = callerLine;
            Kept = kept;
        }

        public LogEventSpec Spec { get; }

        public LogLevel Level { get; }

        /// <summary>The scope it was opened under, or 0.</summary>
        public long Parent { get; }

        /// <summary>When it opened.</summary>
        public long Began { get; }

        public string CallerFile { get; }

        public int CallerLine { get; }

        /// <summary>A copy of the record that opened it, made when the rings overwrote the record while the scope was still open.</summary>
        public KeptRecord? Kept { get; }

        public OpenScope Keeping(KeptRecord kept) => new OpenScope(Spec, Level, Parent, Began, CallerFile, CallerLine, kept);
    }

    /// <summary>A record copied out of the rings, with its own bits and refs.</summary>
    internal sealed class KeptRecord
    {
        public KeptRecord(LogEntry entry, long[] bits, object?[] refs)
        {
            Entry = entry;
            Bits = bits;
            Refs = refs;
        }

        public LogEntry Entry { get; }

        public long[] Bits { get; }

        public object?[] Refs { get; }
    }
}
