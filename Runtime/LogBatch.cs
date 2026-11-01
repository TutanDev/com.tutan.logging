using System;

namespace Tutan.Logging
{
    /// <summary>
    /// A reader's own copy of some records: buffers sized once, which a read empties and fills under
    /// the store's lock. A reader holds references to its batch and never to the rings, so the store
    /// can overwrite a record the moment the read is done. A batch grows only when one record is bigger
    /// than all of it.
    /// </summary>
    public sealed class LogBatch
    {
        private readonly LogEntry[] _entries;
        private long[] _bits;
        private object?[] _refs;
        private int _count;
        private int _bitsUsed;
        private int _refsUsed;
        private LogEventSpec[] _catalog = Array.Empty<LogEventSpec>();

        /// <summary>A batch that holds <paramref name="records"/> records, with room for eight slots of values and two refs each.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="records"/> is less than 1.</exception>
        public LogBatch(int records = 1024)
        {
            if (records < 1)
                throw new ArgumentOutOfRangeException(nameof(records), $"A batch holds at least 1 record, and {records} were asked for.");

            _entries = new LogEntry[records];
            _bits = new long[records * 8];
            _refs = new object?[records * 2];
        }

        /// <summary>How many records the last read copied.</summary>
        public int Count => _count;

        /// <summary>The record at <paramref name="index"/>, from 0 to <see cref="Count"/>, which is valid until the next read into this batch.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not a record of the batch.</exception>
        public LogRecord this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_count)
                    throw new ArgumentOutOfRangeException(nameof(index), $"The batch holds {_count} records, and {index} was asked for.");

                return new LogRecord(this, index);
            }
        }

        internal ref readonly LogEntry Entry(int index) => ref _entries[index];

        internal ReadOnlySpan<long> Bits(int index) => _bits.AsSpan((int)_entries[index].BitsAt, _entries[index].Bits);

        internal ReadOnlySpan<object?> Refs(int index) => _refs.AsSpan((int)_entries[index].RefsAt, _entries[index].Refs);

        internal LogEventSpec Spec(int index) => _catalog[_entries[index].Event];

        /// <summary>Empties the batch, letting go of the references it held.</summary>
        internal void Clear()
        {
            Array.Clear(_refs, 0, _refsUsed);
            Array.Clear(_entries, 0, _count);
            _count = 0;
            _bitsUsed = 0;
            _refsUsed = 0;
        }

        /// <summary>Copies a record in, if it fits, with its slots moved to the batch's own.</summary>
        internal bool TryAdd(in LogEntry entry, ReadOnlySpan<long> bits, ReadOnlySpan<object?> refs)
        {
            bool fits = _count < _entries.Length && _bitsUsed + bits.Length <= _bits.Length && _refsUsed + refs.Length <= _refs.Length;
            if (!fits)
            {
                if (_count > 0)
                    return false;

                // One record bigger than the whole batch: the batch grows to it, or a reader could never pass it.
                Array.Resize(ref _bits, Math.Max(_bits.Length, bits.Length));
                Array.Resize(ref _refs, Math.Max(_refs.Length, refs.Length));
            }

            LogEntry copy = entry;
            copy.BitsAt = (uint)_bitsUsed;
            copy.RefsAt = (uint)_refsUsed;
            _entries[_count++] = copy;
            bits.CopyTo(_bits.AsSpan(_bitsUsed));
            refs.CopyTo(_refs.AsSpan(_refsUsed));
            _bitsUsed += bits.Length;
            _refsUsed += refs.Length;
            return true;
        }

        /// <summary>
        /// Takes the specs its records' events index, once the copy is done: the catalog as it is now,
        /// which names every record copied, since each was written by a spec registered before it.
        /// </summary>
        internal void Seal(LogEventSpec[] events) => _catalog = events;

        /// <summary>Copies one record of <paramref name="source"/> into this batch, emptied first.</summary>
        internal void CopyFrom(LogBatch source, int index)
        {
            Clear();
            TryAdd(source.Entry(index), source.Bits(index), source.Refs(index));
            _catalog = source._catalog;
        }
    }
}
