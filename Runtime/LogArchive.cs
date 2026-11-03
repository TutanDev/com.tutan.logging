using System;
using System.Collections.Generic;
using System.Threading;

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
    /// <see cref="Dropped"/> counts them; a record older than what it let go is let go too. A file's
    /// archive holds what its file held, and takes no record after.
    /// </para>
    /// <para>
    /// Its events are its own, in the order it was first handed a record of each, so a file's are the
    /// file's declarations and not the catalog's: it reads a log whatever program wrote it. A field
    /// is read back from a file as the kind it was declared as, but for three the file holds only as
    /// text, which are read back as strings: chars, a value of the program's own type, and a field
    /// the file was written with personal fields left out of. An exception is read back as an
    /// <see cref="ArchivedException"/>.
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
        private readonly int _blockSize;

        // Its events, which a batch it fills names its records by; and the same, read-only, for a reader.
        private LogEventSpec[] _events = Array.Empty<LogEventSpec>();
        private IReadOnlyList<LogEventSpec> _eventList = Array.AsReadOnly(Array.Empty<LogEventSpec>());
        private int _cap;
        private int _count;
        private long _dropped;
        private long _late;
        private long _pendingLost;
        private bool _sealed;

        /// <summary>An empty archive of a session of <paramref name="program"/> kept whole, which holds at most <paramref name="cap"/> records.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="cap"/> is less than 1.</exception>
        internal LogArchive(string program, LogClock clock, int cap)
            : this(program, clock, LogRedaction.None, cap)
        {
        }

        /// <summary>
        /// An empty archive of the file of a session of <paramref name="program"/>, whose sink left
        /// <paramref name="redacted"/> out of it, which its reader fills and then seals.
        /// </summary>
        internal static LogArchive OfFile(string program, LogClock clock, LogRedaction redacted) =>
            new LogArchive(program, clock, redacted, int.MaxValue);

        private LogArchive(string program, LogClock clock, LogRedaction redacted, int cap)
        {
            if (cap < 1)
                throw new ArgumentOutOfRangeException(nameof(cap), $"An archive holds at least one record, and {cap} was given.");

            Program = program ?? throw new ArgumentNullException(nameof(program));
            Clock = clock;
            Redacted = redacted;
            _cap = cap;
            _blockSize = Math.Max(1, Math.Min(MostBlock, cap / 4));
        }

        /// <inheritdoc />
        public override string Program { get; }

        /// <summary>When the session's store started; a record's timestamp counts from it.</summary>
        public override LogClock Clock { get; }

        /// <summary>What the sink that wrote its file left out of the records: nothing for a session kept whole.</summary>
        public override LogRedaction Redacted { get; }

        /// <summary>Its events, in the order it was first handed a record of each, or a file declares them.</summary>
        public override IReadOnlyList<LogEventSpec> Events => Volatile.Read(ref _eventList);

        /// <summary>The most records it holds: a session's cap, or every record a file holds.</summary>
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

        /// <summary>How many records it let go to stay under its <see cref="Capacity"/>, the oldest first.</summary>
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

        // 1 while it holds nothing, which every sequence is at or after.
        private long FirstHeld => _blocks.Count > 0 ? _blocks[0].First : 1;

        private long NextHeld => _blocks.Count > 0 ? _blocks[_blocks.Count - 1].Last + 1 : 1;

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

        /// <summary>The index its records of <paramref name="spec"/> are stamped with, which it gives a spec the first time it is handed one.</summary>
        /// <exception cref="InvalidOperationException">It is a file's archive, which takes no record after the file's.</exception>
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
        /// or the record is older than what it let go.
        /// </summary>
        /// <exception cref="InvalidOperationException">It is a file's archive, which takes no record after the file's.</exception>
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
        /// False when it holds the sequence already, or the record is older than what it let go.
        /// </summary>
        /// <exception cref="InvalidOperationException">It is a file's archive, which takes no record after the file's.</exception>
        internal bool Add(in LogEntry entry, ReadOnlySpan<long> bits, ReadOnlySpan<object?> refs)
        {
            lock (_lock)
            {
                if ((uint)entry.Event >= (uint)_events.Length)
                    throw new ArgumentOutOfRangeException(nameof(entry), $"The record names event {entry.Event}, and {_events.Length} are declared.");

                return AddHeld(entry, bits, refs);
            }
        }

        /// <summary>Whether it holds the record of <paramref name="sequence"/>.</summary>
        internal bool Holds(long sequence)
        {
            lock (_lock)
                return Find(sequence, out _, out _);
        }

        /// <summary>Counts <paramref name="count"/> records lost before the next record kept in order, which says so when it is read.</summary>
        /// <exception cref="InvalidOperationException">It is a file's archive, which takes no record after the file's.</exception>
        internal void Lose(long count)
        {
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), $"A loss is of 0 records or more, and {count} was given.");

            lock (_lock)
            {
                CheckOpen();
                _pendingLost += count;
            }
        }

        /// <summary>Holds every record it holds now, and takes no more: a file's archive, which holds all the file does.</summary>
        internal void Seal()
        {
            lock (_lock)
            {
                _sealed = true;
                Volatile.Write(ref _cap, Math.Max(_count, 1));
            }
        }

        private void CheckOpen()
        {
            if (_sealed)
                throw new InvalidOperationException("A file's archive holds what its file held, and takes no record after.");
        }

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

            _count++;
            while (_count > _cap)
                DropFront();

            return true;
        }

        /// <summary>Places a record before the newest held, where its sequence goes; false when it is there already or is older than what was let go.</summary>
        private bool Place(in LogEntry entry, ReadOnlySpan<long> bits, ReadOnlySpan<object?> refs)
        {
            long sequence = entry.Sequence;
            if (_dropped > 0 && sequence < FirstHeld)
                return false;

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
                var own = new Block(_blockSize);
                _blocks.Insert(at == 0 ? index : index + 1, own);
                block = own;
                at = 0;
            }
            else if (block.Count == _blockSize)
            {
                (Block left, Block right) = block.Split(_blockSize);
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

        private Block AddBlock()
        {
            var block = new Block(_blockSize);
            _blocks.Add(block);
            return block;
        }

        private void DropFront()
        {
            Block front = _blocks[0];
            _blocks.RemoveAt(0);
            _count -= front.Count;
            _dropped += front.Count;
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

            /// <summary>Two blocks of its records, the older half and the newer, each with only its own bits and refs.</summary>
            public (Block Left, Block Right) Split(int size)
            {
                var left = new Block(size);
                var right = new Block(size);
                int half = Count / 2;
                for (int at = 0; at < Count; at++)
                {
                    Block to = at < half ? left : right;
                    to.Insert(to.Count, Entries[at], BitsOf(at), RefsOf(at), LostBefore(at));
                }

                return (left, right);
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
