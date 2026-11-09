using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Tutan.Functional;

namespace Tutan.Logging
{
    /// <summary>
    /// A session's records, held after the store that wrote them has moved on: kept by a console sink
    /// as a session goes, or read back from the JSON lines file a <see cref="JsonLinesSink"/> wrote,
    /// by <see cref="LogFile.Read(string)"/>. A console asks it what it asks a live store.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Its records keep the sequences their store gave them, so a scope is the id its records name it
    /// by. It holds what a sink was handed, so the sequences have holes where the sink skipped records
    /// below its level, and a read says how many were lost only where the sink was told so.
    /// </para>
    /// <para>
    /// It holds its records in sequence order, each sequence once. A record handed to it after one
    /// with a later sequence, as an error's context is, is placed among them, and
    /// <see cref="LogRecords.LateFrom"/> says where to read again to see it. Past its
    /// <see cref="Capacity"/>, it lets its oldest records go, a block at a time, and
    /// <see cref="Dropped"/> counts them. A console's clear lets every record go. A record older than
    /// what it let go is let go too, and so is one older than all it holds while it is full, which
    /// would be the first it let go. A file's archive holds what its file held, and an ended
    /// session's what its sink kept, and neither takes a record after.
    /// </para>
    /// <para>
    /// Its events are its own, in the order it was first handed a record of each, so a file's are the
    /// file's declarations and not the catalog's: it reads a log whatever program wrote it. A field
    /// is read back from a file as the kind it was declared as, but for three the file holds only as
    /// text, which are read back as strings: chars, a value of the program's own type, and a field
    /// the file was written with personal fields left out of. An exception is read back as an
    /// <see cref="ArchivedException"/>.
    /// </para>
    /// <para>
    /// It knows which of its records are errors, so a console counts the errors it has not shown,
    /// and pauses for one as it arrives, without reading the records: see <see cref="ErrorsFrom"/>.
    /// </para>
    /// <para>It is safe to keep records on one thread while another reads them.</para>
    /// </remarks>
    public sealed class LogArchive : LogRecords
    {
        /// <summary>The most records a block holds.</summary>
        private const int MostBlock = 4096;

        /// <summary>How many of the latest late records it remembers the sequences of, for <see cref="LateFrom"/>.</summary>
        private const int LateKept = 1024;

        private readonly object _lock = new object();
        private readonly List<Block> _blocks = new List<Block>();
        private readonly Dictionary<LogEventSpec, int> _ids = new Dictionary<LogEventSpec, int>();
        private readonly long[] _lateSequences = new long[LateKept];
        private readonly ErrorIndex _errors = new ErrorIndex();
        private readonly int _blockSize;

        // Its events, which a batch it fills names its records by; and the same, read-only, for a reader.
        private LogEventSpec[] _events = Array.Empty<LogEventSpec>();
        private IReadOnlyList<LogEventSpec> _eventList = Array.AsReadOnly(Array.Empty<LogEventSpec>());
        private int _cap;
        private int _count;
        private long _dropped;
        private long _late;
        private long _pendingLost;

        // Where it stopped when it holds nothing: 1 until it lets records go, then one past the newest it let go.
        private long _floor = 1;

        // The block it let go last, emptied, which the next block it needs is: so past its cap it
        // keeps records without allocating, as a console sink does a frame at a time.
        private Block? _spare;
        private bool _sealed;

        /// <summary>
        /// An empty archive of a live session of <paramref name="program"/>, which holds at most
        /// <paramref name="cap"/> records: kept whole, or lacking <paramref name="redacted"/> when the
        /// records it is handed already lack it.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="cap"/> is less than 1.</exception>
        internal LogArchive(string program, LogClock clock, int cap, LogRedaction redacted = LogRedaction.None)
        {
            if (cap < 1)
                throw new ArgumentOutOfRangeException(nameof(cap), $"An archive holds at least one record, and {cap.ToString(CultureInfo.InvariantCulture)} was given.");

            Program = program ?? throw new ArgumentNullException(nameof(program));
            Clock = clock;
            Redacted = redacted;
            _cap = cap;
            _blockSize = Math.Max(1, Math.Min(MostBlock, cap / 4));
        }

        /// <summary>
        /// An empty archive of the file of a session of <paramref name="program"/>, whose sink left
        /// <paramref name="redacted"/> out of it, which its reader fills and then seals.
        /// </summary>
        internal static LogArchive OfFile(string program, LogClock clock, LogRedaction redacted) =>
            new LogArchive(program, clock, int.MaxValue, redacted);

        /// <inheritdoc />
        public override string Program { get; }

        /// <summary>When the session's store started; a record's timestamp counts from it.</summary>
        public override LogClock Clock { get; }

        /// <summary>What the sink that wrote its file left out of the records: nothing for a session kept whole, as a console sink keeps one.</summary>
        public override LogRedaction Redacted { get; }

        /// <summary>Its events, in the order it was first handed a record of each, or a file declares them.</summary>
        public override IReadOnlyList<LogEventSpec> Events => Volatile.Read(ref _eventList);

        /// <summary>The most records it holds: a live session's cap, or every record a file or an ended session holds.</summary>
        public override int Capacity => Volatile.Read(ref _cap);

        /// <summary>How many records it holds, never more than <see cref="Capacity"/>.</summary>
        public int Count
        {
            get
            {
                lock (_lock)
                    return _count;
            }
        }

        /// <summary>How many records it let go to stay under its <see cref="Capacity"/>, the oldest first, since it was last cleared.</summary>
        public long Dropped
        {
            get
            {
                lock (_lock)
                    return _dropped;
            }
        }

        /// <inheritdoc />
        public override long First
        {
            get
            {
                lock (_lock)
                    return FirstHeld;
            }
        }

        /// <inheritdoc />
        public override long Next
        {
            get
            {
                lock (_lock)
                    return NextHeld;
            }
        }

        /// <inheritdoc />
        public override LogLateMark LateMark
        {
            get
            {
                lock (_lock)
                    return new LogLateMark(this, _late);
            }
        }

        /// <summary>A loss the next record in order is to carry, which none has come to yet.</summary>
        internal long PendingLost
        {
            get
            {
                lock (_lock)
                    return _pendingLost;
            }
        }

        // Where it stopped while it holds nothing: 1, which every sequence is at or after, until it lets records go.
        private long FirstHeld => _blocks.Count > 0 ? _blocks[0].First : _floor;

        private long NextHeld => _blocks.Count > 0 ? _blocks[_blocks.Count - 1].Last + 1 : _floor;

        /// <inheritdoc />
        public override LogLate LateFrom(LogLateMark seen)
        {
            lock (_lock)
            {
                // A mark of other records, as default is, has seen none of these.
                long since = ReferenceEquals(seen.Of, this) ? seen.Placed : 0;
                return new LogLate(LeastLateSince(since), new LogLateMark(this, _late));
            }
        }

        // With the lock held.
        private long LeastLateSince(long since)
        {
            if (since >= _late)
                return NextHeld;
            if (_late - since > LateKept)
                return FirstHeld;

            long least = long.MaxValue;
            for (long i = since; i < _late; i++)
                least = Math.Min(least, _lateSequences[i % LateKept]);

            return Math.Max(least, FirstHeld);
        }

        /// <summary>
        /// Copies its records from <paramref name="from"/> on into <paramref name="into"/>, as many as
        /// fit. A read stops before a record that records were lost before, and the read that starts
        /// at that record says how many, so a reader knows where the loss was. A read from before
        /// <see cref="LogRecords.First"/> starts at it, and says nothing of what was let go before it:
        /// <see cref="Dropped"/> does.
        /// </summary>
        public override LogRead Read(long from, LogBatch into)
        {
            if (into is null)
                throw new ArgumentNullException(nameof(into));

            lock (_lock)
            {
                into.Clear();
                Locate(from, out int block, out int at);
                long first = SequenceAt(block, at);
                long lost = block < _blocks.Count ? _blocks[block].LostBefore(at) : 0;
                int count = 0;
                while (block < _blocks.Count)
                {
                    Block held = _blocks[block];
                    if (count > 0 && held.LostBefore(at) > 0)
                        break;
                    if (!into.TryAdd(held.Entries[at], held.BitsOf(at), held.RefsOf(at)))
                        break;

                    count++;
                    if (++at == held.Count)
                    {
                        block++;
                        at = 0;
                    }
                }

                into.Seal(_events);
                return new LogRead(first, count, SequenceAt(block, at), lost);
            }
        }

        /// <inheritdoc />
        public override int Read(ReadOnlySpan<long> sequences, LogBatch into)
        {
            if (into is null)
                throw new ArgumentNullException(nameof(into));

            lock (_lock)
            {
                into.Clear();
                foreach (long sequence in sequences)
                {
                    if (!Find(sequence, out int block, out int at))
                        continue;

                    Block held = _blocks[block];
                    if (!into.TryAdd(held.Entries[at], held.BitsOf(at), held.RefsOf(at)))
                        break;
                }

                into.Seal(_events);
                return into.Count;
            }
        }

        /// <summary>
        /// The errors it holds from <paramref name="sequence"/> on, and where it is now, read at once,
        /// without allocating. An error is an event's record at <see cref="LogLevel.Error"/> or
        /// <see cref="LogLevel.Critical"/> that was not written as an error's backlog. An error it let
        /// go, past its cap or on a clear, is not held, so it is not counted.
        /// </summary>
        public LogErrorsHeld ErrorsFrom(long sequence)
        {
            lock (_lock)
            {
                int at = _errors.IndexFrom(sequence);
                int end = _errors.End;
                return at < end
                    ? new LogErrorsHeld(end - at, _errors[at], _errors[end - 1], NextHeld)
                    : new LogErrorsHeld(NextHeld);
            }
        }

        /// <summary>The index its records of <paramref name="spec"/> are stamped with, which it gives a spec the first time it is handed one.</summary>
        /// <exception cref="InvalidOperationException">It is sealed: a file's archive, or an ended session's, which takes no record after.</exception>
        internal int Declare(LogEventSpec spec)
        {
            if (spec is null)
                throw new ArgumentNullException(nameof(spec));

            lock (_lock)
            {
                CheckOpen();
                return DeclareHeld(spec);
            }
        }

        /// <summary>
        /// Keeps a copy of <paramref name="record"/>, as a sink was handed it: as an error's backlog
        /// when <paramref name="backlog"/> says so. False when it holds the record's sequence already,
        /// or it lets the record go as it is handed: older than what it let go, or than all it holds while it is full.
        /// </summary>
        /// <exception cref="InvalidOperationException">It is sealed: a file's archive, or an ended session's, which takes no record after.</exception>
        internal bool Keep(LogRecord record, bool backlog)
        {
            LogEntry entry = record.Entry;
            entry.Backlog = backlog;
            lock (_lock)
            {
                CheckOpen();
                entry.Event = DeclareHeld(record.Event);
                return AddHeld(entry, record.Bits, record.Refs);
            }
        }

        /// <summary>
        /// Keeps a record its reader made, whose event is the index <see cref="Declare"/> gave its
        /// spec, and whose <see cref="LogEntry.BitsAt"/> and <see cref="LogEntry.RefsAt"/> it sets.
        /// False when it holds the sequence already, or it lets the record go as it is handed, as <see cref="Keep"/> does.
        /// </summary>
        /// <exception cref="InvalidOperationException">It is sealed: a file's archive, or an ended session's, which takes no record after.</exception>
        internal bool Add(in LogEntry entry, ReadOnlySpan<long> bits, ReadOnlySpan<object?> refs)
        {
            lock (_lock)
            {
                if ((uint)entry.Event >= (uint)_events.Length)
                    throw new ArgumentOutOfRangeException(nameof(entry), $"The record names event {entry.Event.ToString(CultureInfo.InvariantCulture)}, and {_events.Length.ToString(CultureInfo.InvariantCulture)} are declared.");

                return AddHeld(entry, bits, refs);
            }
        }

        /// <summary>Whether it holds the record of <paramref name="sequence"/>.</summary>
        internal bool Holds(long sequence)
        {
            lock (_lock)
                return Find(sequence, out _, out _);
        }

        /// <summary>
        /// Whether it would keep the record of <paramref name="sequence"/>: it takes records still, the
        /// record is not older than what it let go, and it does not hold it.
        /// </summary>
        internal bool CanKeep(long sequence)
        {
            lock (_lock)
                return !_sealed && !WouldLetGo(sequence) && !Find(sequence, out _, out _);
        }

        /// <summary>Counts <paramref name="count"/> records lost before the next record kept in order, which says so when it is read.</summary>
        /// <exception cref="InvalidOperationException">It is sealed: a file's archive, or an ended session's, which takes no record after.</exception>
        internal void Lose(long count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), $"A loss is of 0 records or more, and {count.ToString(CultureInfo.InvariantCulture)} was given.");

            lock (_lock)
            {
                CheckOpen();
                _pendingLost += count;
            }
        }

        /// <summary>
        /// Lets go of every record it holds, as a console's clear does, and keeps what it is handed
        /// after: <see cref="LogRecords.First"/> and <see cref="LogRecords.Next"/> are where it stopped,
        /// and it takes no record older. What it dropped, and a loss no record carried yet, were before
        /// the clear, and go with it.
        /// </summary>
        /// <exception cref="InvalidOperationException">It is sealed: a file's, or an ended session's.</exception>
        internal void Clear()
        {
            lock (_lock)
            {
                CheckOpen();
                _floor = NextHeld;
                _blocks.Clear();
                _errors.Clear();
                _count = 0;
                _dropped = 0;
                _pendingLost = 0;
            }
        }

        /// <summary>
        /// Holds every record it holds now, and takes no more: a file's archive, which holds all the
        /// file does, and an ended session's, which its sink writes to no more.
        /// </summary>
        internal void Seal()
        {
            lock (_lock)
            {
                _sealed = true;
                _spare = null;
                Volatile.Write(ref _cap, Math.Max(_count, 1));
            }
        }

        private void CheckOpen()
        {
            if (_sealed)
                throw new InvalidOperationException("The archive is sealed: it holds what its file held, or what its session kept before it ended, and takes no record after.");
        }

        // With the lock held. Whether it lets the record go as it is handed: it is older than what was
        // let go, by the cap or a clear, or it is full and the record is older than all it holds, so
        // the cap would let it go first, and the newer records of its block with it.
        private bool WouldLetGo(long sequence) => sequence < _floor || (_count >= _cap && sequence < FirstHeld);

        private int DeclareHeld(LogEventSpec spec)
        {
            if (_ids.TryGetValue(spec, out int index))
                return index;

            // A new array, so a batch sealed with the one before still names every record it holds.
            index = _events.Length;
            var events = new LogEventSpec[index + 1];
            Array.Copy(_events, events, index);
            events[index] = spec;
            Volatile.Write(ref _events, events);
            Volatile.Write(ref _eventList, Array.AsReadOnly(events));
            _ids.Add(spec, index);
            return index;
        }

        private bool AddHeld(in LogEntry entry, ReadOnlySpan<long> bits, ReadOnlySpan<object?> refs)
        {
            CheckOpen();
            long sequence = entry.Sequence;
            if (WouldLetGo(sequence))
                return false;

            if (_blocks.Count == 0 || sequence > _blocks[_blocks.Count - 1].Last)
            {
                Block last = _blocks.Count > 0 ? _blocks[_blocks.Count - 1] : AddBlock();
                if (last.Count == _blockSize)
                    last = AddBlock();

                last.Insert(last.Count, entry, bits, refs, _pendingLost);
                _pendingLost = 0;
            }
            else if (!Place(entry, bits, refs))
            {
                return false;
            }

            if (IsError(entry))
                _errors.Add(sequence);

            _count++;
            while (_count > _cap)
                DropFront();

            return true;
        }

        /// <summary>Places a record before the newest held, where its sequence goes; false when it is there already.</summary>
        private bool Place(in LogEntry entry, ReadOnlySpan<long> bits, ReadOnlySpan<object?> refs)
        {
            long sequence = entry.Sequence;

            // The last block that starts at or before it, or the first when it is older than all.
            int index = 0;
            int low = 1;
            int high = _blocks.Count - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                if (_blocks[middle].First <= sequence)
                {
                    index = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            Block block = _blocks[index];
            int at = block.Find(sequence);
            if (at < block.Count && block.Entries[at].Sequence == sequence)
                return false;

            if (block.Count == _blockSize && _blockSize == 1)
            {
                // A block of one does not halve: the record is a block of its own, beside it.
                Block own = NewBlock();
                _blocks.Insert(at == 0 ? index : index + 1, own);
                block = own;
                at = 0;
            }
            else if (block.Count == _blockSize)
            {
                // The halves take the spare, when it has one, and a new block, and the block they halve is the spare after.
                Block left = NewBlock();
                Block right = NewBlock();
                block.SplitInto(left, right);
                block.Empty();
                _spare = block;
                _blocks[index] = left;
                _blocks.Insert(index + 1, right);
                bool newer = at >= left.Count;
                block = newer ? right : left;
                at = newer ? at - left.Count : at;
            }

            block.Insert(at, entry, bits, refs, lost: 0);
            _lateSequences[_late % LateKept] = sequence;
            _late++;
            return true;
        }

        /// <summary>Whether <paramref name="entry"/> is an error, as <see cref="ErrorsFrom"/> counts one.</summary>
        private static bool IsError(in LogEntry entry) =>
            entry.Kind == LogRecordKind.Event && (entry.Level is LogLevel.Error or LogLevel.Critical) && !entry.Backlog;

        private Block AddBlock()
        {
            Block block = NewBlock();
            _blocks.Add(block);
            return block;
        }

        // The spare, when it has one, so a block it let go is the next it fills.
        private Block NewBlock()
        {
            Block block = _spare ?? new Block(_blockSize);
            _spare = null;
            return block;
        }

        private void DropFront()
        {
            Block front = _blocks[0];
            _blocks.RemoveAt(0);
            _count -= front.Count;
            _dropped += front.Count;
            _floor = front.Last + 1;
            _errors.DropThrough(front.Last);

            // Readers copy what they read under the lock, so none holds the block now. Every block is
            // of one size, and its buffers grow as any block's do.
            front.Empty();
            _spare = front;
        }

        private long SequenceAt(int block, int at) => block < _blocks.Count ? _blocks[block].Entries[at].Sequence : NextHeld;

        /// <summary>Where the first record at or after <paramref name="sequence"/> is, or one past the last block when none is.</summary>
        private void Locate(long sequence, out int block, out int at)
        {
            int low = 0;
            int high = _blocks.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (_blocks[middle].Last < sequence)
                    low = middle + 1;
                else
                    high = middle;
            }

            block = low;
            at = block < _blocks.Count ? _blocks[block].Find(sequence) : 0;
        }

        private bool Find(long sequence, out int block, out int at)
        {
            Locate(sequence, out block, out at);
            return block < _blocks.Count && _blocks[block].Entries[at].Sequence == sequence;
        }

        /// <summary>
        /// The sequences of the errors it holds, in order and each once, used under its lock: a run of
        /// an array, which lets go from its start and grows by doubling, so keeping in order allocates
        /// only when it grows.
        /// </summary>
        private sealed class ErrorIndex
        {
            private long[] _sequences = new long[16];
            private int _start;

            /// <summary>One past the last error, where the run ends.</summary>
            public int End { get; private set; }

            /// <summary>The sequence of the error at <paramref name="index"/>, from the run's start up to <see cref="End"/>.</summary>
            public long this[int index] => _sequences[index];

            /// <summary>Where the first error at or after <paramref name="sequence"/> is, or <see cref="End"/> when none is.</summary>
            public int IndexFrom(long sequence)
            {
                int low = _start;
                int high = End;
                while (low < high)
                {
                    int middle = low + (high - low) / 2;
                    if (_sequences[middle] < sequence)
                        low = middle + 1;
                    else
                        high = middle;
                }

                return low;
            }

            /// <summary>Indexes the error of <paramref name="sequence"/>, which it does not hold: at the end when it is the newest, as it most often is.</summary>
            public void Add(long sequence)
            {
                int at = End > _start && sequence < _sequences[End - 1] ? IndexFrom(sequence) : End;
                if (End == _sequences.Length)
                    at = MakeRoom(at);

                if (at < End)
                    Array.Copy(_sequences, at, _sequences, at + 1, End - at);

                _sequences[at] = sequence;
                End++;
            }

            /// <summary>Lets go of the errors at or before <paramref name="last"/>, as their block is let go.</summary>
            public void DropThrough(long last)
            {
                _start = IndexFrom(last + 1);
                if (_start == End)
                    Clear();
            }

            /// <summary>Lets go of every error, as a clear lets go of every record.</summary>
            public void Clear()
            {
                _start = 0;
                End = 0;
            }

            /// <summary>
            /// Moves the run to the array's start, into an array twice as long when the run is more than
            /// half of it, and says where <paramref name="at"/> is after.
            /// </summary>
            private int MakeRoom(int at)
            {
                int count = End - _start;
                long[] into = count * 2 > _sequences.Length ? new long[_sequences.Length * 2] : _sequences;
                Array.Copy(_sequences, _start, into, 0, count);
                _sequences = into;
                at -= _start;
                _start = 0;
                End = count;
                return at;
            }
        }

        /// <summary>
        /// Records in sequence order, each with its bits and refs in the block's own buffers, at
        /// positions relative to them, and the loss before it, if any.
        /// </summary>
        private sealed class Block
        {
            private long[] _bits = new long[64];
            private object?[] _refs = new object?[16];
            private long[]? _lost;
            private int _bitsUsed;
            private int _refsUsed;

            public Block(int size) => Entries = new LogEntry[size];

            public LogEntry[] Entries { get; }

            public int Count { get; private set; }

            public long First => Entries[0].Sequence;

            public long Last => Entries[Count - 1].Sequence;

            public long LostBefore(int at) => _lost is null ? 0 : _lost[at];

            public ReadOnlySpan<long> BitsOf(int at) => new ReadOnlySpan<long>(_bits, (int)Entries[at].BitsAt, Entries[at].Bits);

            public ReadOnlySpan<object?> RefsOf(int at) => new ReadOnlySpan<object?>(_refs, (int)Entries[at].RefsAt, Entries[at].Refs);

            /// <summary>Where the first record at or after <paramref name="sequence"/> is, or <see cref="Count"/>.</summary>
            public int Find(long sequence)
            {
                int low = 0;
                int high = Count;
                while (low < high)
                {
                    int middle = low + (high - low) / 2;
                    if (Entries[middle].Sequence < sequence)
                        low = middle + 1;
                    else
                        high = middle;
                }

                return low;
            }

            /// <summary>Puts a record at <paramref name="at"/>, moving those after it on by one; it has room.</summary>
            public void Insert(int at, in LogEntry entry, ReadOnlySpan<long> bits, ReadOnlySpan<object?> refs, long lost)
            {
                Array.Copy(Entries, at, Entries, at + 1, Count - at);
                if (_lost is not null)
                    Array.Copy(_lost, at, _lost, at + 1, Count - at);

                LogEntry copy = entry;
                copy.BitsAt = (uint)Append(ref _bits, ref _bitsUsed, bits);
                copy.RefsAt = (uint)Append(ref _refs, ref _refsUsed, refs);
                Entries[at] = copy;

                if (lost > 0)
                    _lost ??= new long[Entries.Length];
                if (_lost is not null)
                    _lost[at] = lost;

                Count++;
            }

            /// <summary>Holds nothing, keeping its buffers, and lets go of the values it held, so it can be filled again.</summary>
            public void Empty()
            {
                Array.Clear(Entries, 0, Count);
                Array.Clear(_refs, 0, _refsUsed);
                if (_lost is not null)
                    Array.Clear(_lost, 0, Count);
                _bitsUsed = 0;
                _refsUsed = 0;
                Count = 0;
            }

            /// <summary>Copies the older half of its records into <paramref name="left"/> and the newer into <paramref name="right"/>, both empty, each with only its own bits and refs.</summary>
            public void SplitInto(Block left, Block right)
            {
                int half = Count / 2;
                for (int at = 0; at < Count; at++)
                {
                    Block to = at < half ? left : right;
                    to.Insert(to.Count, Entries[at], BitsOf(at), RefsOf(at), LostBefore(at));
                }
            }

            private static int Append<T>(ref T[] buffer, ref int used, ReadOnlySpan<T> items)
            {
                int at = used;
                if (at + items.Length > buffer.Length)
                {
                    int length = buffer.Length;
                    while (at + items.Length > length)
                        length *= 2;
                    Array.Resize(ref buffer, length);
                }

                items.CopyTo(buffer.AsSpan(at));
                used += items.Length;
                return at;
            }
        }
    }

    /// <summary>
    /// The errors an archive holds from a sequence on, as <see cref="LogArchive.ErrorsFrom"/> read them:
    /// none, or how many, from the first to the newest, so the count and its ends always agree.
    /// </summary>
    public readonly record struct LogErrorsHeld
    {
        private readonly long _first;
        private readonly long _last;

        /// <summary>None held, the archive at <paramref name="next"/>.</summary>
        internal LogErrorsHeld(long next)
        {
            Count = 0;
            _first = 0;
            _last = 0;
            Next = next;
        }

        /// <summary><paramref name="count"/> errors held, from <paramref name="first"/> to <paramref name="last"/>, all before <paramref name="next"/>.</summary>
        internal LogErrorsHeld(long count, long first, long last, long next)
        {
            if (count < 1 || first > last || last >= next || last - first + 1 < count)
                throw new ArgumentOutOfRangeException(nameof(count),
                    $"No {count.ToString(CultureInfo.InvariantCulture)} errors are held from {first.ToString(CultureInfo.InvariantCulture)} to {last.ToString(CultureInfo.InvariantCulture)} before {next.ToString(CultureInfo.InvariantCulture)}.");

            Count = count;
            _first = first;
            _last = last;
            Next = next;
        }

        /// <summary>How many errors it holds from the sequence on.</summary>
        public long Count { get; }

        /// <summary>The sequence of the first of them: none when it holds none.</summary>
        public Optional<long> First => Count > 0 ? _first : F.None;

        /// <summary>The sequence of the newest of them: none when it holds none.</summary>
        public Optional<long> Last => Count > 0 ? _last : F.None;

        /// <summary>The archive's <see cref="LogRecords.Next"/> as it was read: every error it held then is before it.</summary>
        public long Next { get; }
    }

    /// <summary>
    /// An exception read back from a log's file: the name of the type it was thrown as, its message,
    /// and its text, as the program that threw it wrote them, since the type need not be in this
    /// process. Its <see cref="ToString"/> is that text.
    /// </summary>
    public sealed class ArchivedException : Exception
    {
        private readonly string _text;

        /// <summary>An exception as a file holds it.</summary>
        /// <param name="typeName">The full name of the type it was thrown as.</param>
        /// <param name="message">Its message.</param>
        /// <param name="text">Its text: the type, the message, the stack and what it wraps.</param>
        public ArchivedException(string typeName, string message, string text)
            : base(message)
        {
            TypeName = typeName ?? throw new ArgumentNullException(nameof(typeName));
            _text = text ?? throw new ArgumentNullException(nameof(text));
        }

        /// <summary>The full name of the type it was thrown as: <c>System.IO.InvalidDataException</c>.</summary>
        public string TypeName { get; }

        /// <summary>Its text, as the program that threw it wrote it.</summary>
        public override string ToString() => _text;
    }
}
