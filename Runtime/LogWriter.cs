using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Tutan.Logging
{
    /// <summary>
    /// A record being written: the space the store reserved for its fields, which the generated code
    /// fills in the order of the spec's fields and then commits. The store's lock is held from
    /// <see cref="Log.Write"/> to <see cref="Commit"/>, and nothing but these copies runs in between,
    /// since the arguments were evaluated before the call.
    /// </summary>
    /// <remarks>
    /// It is committed by a call in a <c>finally</c>, not disposed by a <c>using</c>: a <c>using</c>
    /// variable is read-only, and each write would then advance a copy. A writer the store did not
    /// open, because a limit dropped the record, writes nothing and commits nothing.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ref struct LogWriter
    {
        /// <summary>The most chars a chars field keeps. Longer text is cut, and ends with <c>...</c>.</summary>
        public const int MaxChars = 1024;

        private readonly LogStore? _store;
        private readonly Span<long> _bits;
        private readonly Span<object?> _refs;
        private readonly Log _from;
        private readonly long _scope;
        private int _bitsAt;
        private int _refsAt;
        private bool _committed;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal LogWriter(LogStore? store, Span<long> bits, Span<object?> refs, Log from, long scope)
        {
            _store = store;
            _bits = bits;
            _refs = refs;
            _from = from;
            _scope = scope;
            _bitsAt = 0;
            _refsAt = 0;
            _committed = false;
        }

        /// <summary>A writer that writes nothing, for a record a limit dropped: a scope it would have opened is off, and hands back <paramref name="from"/>.</summary>
        internal static LogWriter Dropped(Log from) => new LogWriter(null, default, default, from, 0);

        /// <summary>The slots <paramref name="value"/> takes beyond its length's, which the call adds up for <see cref="Log.Write"/> before it starts.</summary>
        public static int CharSlots(ReadOnlySpan<char> value) => (Math.Min(value.Length, MaxChars) + 3) >> 2;

        /// <summary>The scope a <see cref="Log.BeginScope"/> opened, once committed; for one a limit dropped, a scope that is off.</summary>
        public readonly LogScope Scope => _scope != 0 ? new LogScope(_from, _scope) : LogScope.Off(_from);

        /// <summary>Copies a value into its slots, zeroing what of the last it does not fill.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Value<T>(T value) where T : unmanaged
        {
            if (_store is null)
                return;

            int size = SizeOf<T>.Bytes;
            int slots = (size + 7) >> 3;
            Span<long> target = _bits.Slice(_bitsAt, slots);
            if ((size & 7) != 0)
                target[slots - 1] = 0;

            MemoryMarshal.Write(MemoryMarshal.AsBytes(target), ref value);
            _bitsAt += slots;
        }

        /// <summary>Whether an optional value is there, in the slot before the value's.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Presence(bool some)
        {
            if (_store is null)
                return;

            _bits[_bitsAt++] = some ? 1 : 0;
        }

        /// <summary>Keeps a string or an exception by reference.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Ref(object? value)
        {
            if (_store is null)
                return;

            _refs[_refsAt++] = value;
        }

        /// <summary>Copies chars: their length in one slot, then four to a slot, cut at <see cref="MaxChars"/>.</summary>
        public void Chars(ReadOnlySpan<char> value)
        {
            if (_store is null)
                return;

            int length = Math.Min(value.Length, MaxChars);
            int slots = (length + 3) >> 2;
            _bits[_bitsAt++] = length;

            Span<char> target = MemoryMarshal.Cast<long, char>(_bits.Slice(_bitsAt, slots));
            if (value.Length > MaxChars)
            {
                value.Slice(0, MaxChars - 3).CopyTo(target);
                "...".AsSpan().CopyTo(target.Slice(MaxChars - 3));
            }
            else
            {
                value.CopyTo(target);
            }

            target.Slice(length).Clear();
            _bitsAt += slots;
        }

        /// <summary>Ends the record and lets the store go.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Commit()
        {
            if (_store is null || _committed)
                return;

            _committed = true;
            _store.Committed();
        }
    }
}
