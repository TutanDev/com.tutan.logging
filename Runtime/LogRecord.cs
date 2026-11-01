using System;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Tutan.Logging
{
    /// <summary>
    /// One record, as a reader sees it in its <see cref="LogBatch"/>: the stamps it was written with,
    /// its spec, its call site, and its fields as typed values. It is valid until the next read into
    /// the batch, and being a ref struct it cannot be kept past it. Nothing about it is text until a
    /// reader formats it, into a span of its own.
    /// </summary>
    public readonly ref struct LogRecord
    {
        private readonly LogBatch _batch;
        private readonly int _index;

        internal LogRecord(LogBatch batch, int index)
        {
            _batch = batch;
            _index = index;
        }

        /// <summary>Its sequence, counting from 1 in each store, which is how every reader addresses it.</summary>
        public long Sequence => Entry.Sequence;

        /// <summary>When it was written, as a <see cref="Stopwatch"/> timestamp, which the store's <see cref="LogClock"/> turns into a time.</summary>
        public long Timestamp => Entry.Timestamp;

        /// <summary>The frame the host was on when it was written, 0 before the pump first stamped one.</summary>
        public long Frame => Entry.Frame;

        /// <summary>The scope it was written under, or 0. A scope's own records, which open and close it, are under the scope it was opened in.</summary>
        public long Scope => Entry.Scope;

        /// <summary>
        /// The scope this record opens or closes, which is the sequence of the record that opened it:
        /// its own sequence for a <see cref="LogRecordKind.ScopeBegin"/>, the opening's for a
        /// <see cref="LogRecordKind.ScopeEnd"/>, and 0 for an event.
        /// </summary>
        public long Begin => Kind switch
        {
            LogRecordKind.ScopeBegin => Entry.Sequence,
            LogRecordKind.ScopeEnd => _batch.Bits(_index)[0],
            _ => 0,
        };

        /// <summary>For a <see cref="LogRecordKind.ScopeEnd"/>, how long its scope was open; otherwise zero.</summary>
        public TimeSpan Elapsed => Kind == LogRecordKind.ScopeEnd
            ? LogClock.Between(_batch.Bits(_index)[1], Entry.Timestamp)
            : TimeSpan.Zero;

        /// <summary>The managed id of the thread it was written on.</summary>
        public int Thread => Entry.Thread;

        /// <summary>Its level.</summary>
        public LogLevel Level => Entry.Level;

        /// <summary>An event, or the opening or closing of a scope.</summary>
        public LogRecordKind Kind => Entry.Kind;

        /// <summary>How many records of its event a limit dropped between the one before it and it.</summary>
        public int Suppressed => Entry.Suppressed;

        /// <summary>
        /// The call that wrote it, as the compiler filled in the generated method's caller-info
        /// parameters: what a console links to. A scope's closing has its opening's. A record written
        /// by the bridge from Unity's log has none.
        /// </summary>
        public LogCallSite CallSite => new LogCallSite(Entry.CallerFile, Entry.CallerLine);

        /// <summary>Its event or scope.</summary>
        public LogEventSpec Event => _batch.Spec(_index);

        /// <summary>How many fields it has: its spec's, or none for a scope's closing.</summary>
        public int FieldCount => Kind == LogRecordKind.ScopeEnd ? 0 : Event.FieldArray.Length;

        private ref readonly LogEntry Entry => ref _batch.Entry(_index);

        /// <summary>The field at <paramref name="index"/>, in the order of the spec's.</summary>
        /// <exception cref="ArgumentOutOfRangeException">The record has no field at <paramref name="index"/>.</exception>
        public LogValue Field(int index)
        {
            if ((uint)index >= (uint)FieldCount)
                throw new ArgumentOutOfRangeException(nameof(index), $"{Event.Key} has {FieldCount} fields here, and {index} was asked for.");

            LogField[] fields = Event.FieldArray;
            ReadOnlySpan<long> bits = _batch.Bits(_index);
            int bitsAt = 0;
            int refsAt = 0;
            for (int i = 0; i < index; i++)
            {
                LogField field = fields[i];
                if (field.IsRef)
                    refsAt++;
                else if (field.Kind == LogValueKind.Chars)
                    bitsAt += 1 + (((int)bits[bitsAt] + 3) >> 2);
                else
                    bitsAt += field.FixedSlots;
            }

            LogField at = fields[index];
            if (at.IsRef)
                return new LogValue(at, default, _batch.Refs(_index)[refsAt]);

            int length = at.Kind == LogValueKind.Chars ? 1 + (((int)bits[bitsAt] + 3) >> 2) : at.FixedSlots;
            return new LogValue(at, bits.Slice(bitsAt, length), null);
        }

        /// <summary>The field named <paramref name="name"/>, ignoring case, if it has one.</summary>
        public bool TryField(string name, out LogValue value)
        {
            LogField[] fields = Event.FieldArray;
            for (int i = 0; i < FieldCount; i++)
            {
                if (string.Equals(fields[i].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = Field(i);
                    return true;
                }
            }

            value = default;
            return false;
        }

        /// <summary>
        /// Writes its message: the template with each hole's field formatted into it. A scope's
        /// closing has no fields, so it writes the template as it was declared, holes and all; a reader
        /// with the opening record writes that instead, as <see cref="LogContext.TryFormat"/> does.
        /// </summary>
        /// <param name="destination">Where to write it.</param>
        /// <param name="written">How many chars were written, or 0 when they did not fit.</param>
        /// <param name="redact">What to leave out of the fields.</param>
        /// <returns>Whether it fit.</returns>
        public bool TryFormat(Span<char> destination, out int written, LogRedaction redact = LogRedaction.None)
        {
            written = 0;
            LogTemplate template = Event.Template;
            if (Kind == LogRecordKind.ScopeEnd)
                return LogText.Append(template.Text, destination, ref written);

            string[] literals = template.LiteralArray;
            LogHole[] holes = template.HoleArray;
            for (int i = 0; i < holes.Length; i++)
            {
                if (!LogText.Append(literals[i], destination, ref written))
                {
                    written = 0;
                    return false;
                }

                LogHole hole = holes[i];
                if (!LogText.TryFormatAligned(Field(hole.Field), destination.Slice(written), out int value, hole, redact))
                {
                    written = 0;
                    return false;
                }

                written += value;
            }

            if (LogText.Append(literals[literals.Length - 1], destination, ref written))
                return true;

            written = 0;
            return false;
        }

        /// <summary>The message, as a string: for a test or a debugger, since it allocates.</summary>
        public override string ToString() => LogText.ToText(this, LogRedaction.None);
    }

    /// <summary>
    /// Where a record was written from: the file the compiler gave the calling method's
    /// <c>[CallerFilePath]</c>, which is the path it compiled, as any <c>-pathmap</c> mapped it, and the
    /// line. Unity maps the project's folder to <c>.</c> for the assemblies of packages, so a package's
    /// file is relative to the project, and a file under <c>Assets/</c> is absolute.
    /// </summary>
    public readonly struct LogCallSite : IEquatable<LogCallSite>
    {
        /// <summary>A call site of <paramref name="file"/> at <paramref name="line"/>.</summary>
        public LogCallSite(string? file, int line)
        {
            File = string.IsNullOrEmpty(file) ? null : file;
            Line = File is null ? 0 : line;
        }

        /// <summary>The file, as the compiler wrote it, or null when the record has none.</summary>
        public string? File { get; }

        /// <summary>The line, from 1, or 0 when the record has no call site.</summary>
        public int Line { get; }

        /// <summary>Whether the record has a call site.</summary>
        public bool IsKnown => File is not null;

        /// <inheritdoc />
        public bool Equals(LogCallSite other) => string.Equals(File, other.File, StringComparison.Ordinal) && Line == other.Line;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is LogCallSite other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(File, Line);

        /// <summary><c>File:Line</c>, or empty when there is none.</summary>
        public override string ToString() => File is null ? "" : $"{File}:{Line.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// One field's value in a record: what it was declared as, and the value, readable as itself,
    /// as a number, as chars or as the reference it was kept by, and formattable into a span.
    /// </summary>
    public readonly ref struct LogValue
    {
        private readonly ReadOnlySpan<long> _slots;
        private readonly object? _ref;

        internal LogValue(LogField field, ReadOnlySpan<long> slots, object? reference)
        {
            Field = field;
            _slots = slots;
            _ref = reference;
        }

        /// <summary>The field: its name, kind, declared type, tag, and whether it is personal.</summary>
        public LogField Field { get; }

        /// <summary>How the value is held.</summary>
        public LogValueKind Kind => Field.Kind;

        /// <summary>Whether there is no value: an optional value that was none, or a reference that was null.</summary>
        public bool IsNone => Field.IsRef ? _ref is null : Field.IsOptional && _slots[0] == 0;

        private ReadOnlySpan<long> ValueSlots => Field.IsOptional ? _slots.Slice(1) : _slots;

        /// <summary>The value as the <typeparamref name="T"/> it was written as: <c>int</c> for an Int32, <c>long</c> for an enum, the program's own type for a Formattable.</summary>
        /// <exception cref="InvalidOperationException">The value is kept by reference, or is chars, or is smaller than a <typeparamref name="T"/>, or is none.</exception>
        public T Read<T>() where T : unmanaged
        {
            if (Field.IsRef || Kind == LogValueKind.Chars || SizeOf<T>.Bytes > Field.Slots * 8)
                throw new InvalidOperationException($"{Field.Name} is a {Field.Type}, which is not read as a {typeof(T).Name}.");
            if (IsNone)
                throw new InvalidOperationException($"{Field.Name} is none here, so it has no value to read.");

            return MemoryMarshal.Read<T>(MemoryMarshal.AsBytes(ValueSlots));
        }

        /// <summary>A whole number, a bool as 0 or 1, a char's code, or an enum's value, as a <see cref="long"/>; a ulong past its range wraps.</summary>
        /// <exception cref="InvalidOperationException">The value is not one of these.</exception>
        public long AsInt64() => Kind switch
        {
            LogValueKind.Boolean => Read<bool>() ? 1 : 0,
            LogValueKind.Char => Read<char>(),
            LogValueKind.SByte => Read<sbyte>(),
            LogValueKind.Byte => Read<byte>(),
            LogValueKind.Int16 => Read<short>(),
            LogValueKind.UInt16 => Read<ushort>(),
            LogValueKind.Int32 => Read<int>(),
            LogValueKind.UInt32 => Read<uint>(),
            LogValueKind.Int64 or LogValueKind.Enum => Read<long>(),
            LogValueKind.UInt64 => unchecked((long)Read<ulong>()),
            _ => throw new InvalidOperationException($"{Field.Name} is a {Field.Type}, which is not a whole number."),
        };

        /// <summary>Any number, as a <see cref="double"/>.</summary>
        /// <exception cref="InvalidOperationException">The value is not a number.</exception>
        public double AsDouble() => Kind switch
        {
            LogValueKind.Single => Read<float>(),
            LogValueKind.Double => Read<double>(),
            LogValueKind.Decimal => (double)Read<decimal>(),
            LogValueKind.UInt64 => Read<ulong>(),
            LogValueKind.SByte or LogValueKind.Byte or LogValueKind.Int16 or LogValueKind.UInt16
                or LogValueKind.Int32 or LogValueKind.UInt32 or LogValueKind.Int64 => AsInt64(),
            _ => throw new InvalidOperationException($"{Field.Name} is a {Field.Type}, which is not a number."),
        };

        /// <summary>A string's chars, or chars copied at the call; empty for a null string.</summary>
        /// <exception cref="InvalidOperationException">The value is neither.</exception>
        public ReadOnlySpan<char> AsChars() => Kind switch
        {
            LogValueKind.Chars => MemoryMarshal.Cast<long, char>(_slots.Slice(1)).Slice(0, (int)_slots[0]),
            LogValueKind.String => ((string?)_ref).AsSpan(),
            _ => throw new InvalidOperationException($"{Field.Name} is a {Field.Type}, which is not text."),
        };

        /// <summary>The string or exception the value was kept by.</summary>
        /// <exception cref="InvalidOperationException">The value was copied, not kept by reference.</exception>
        public object? AsRef() => Field.IsRef
            ? _ref
            : throw new InvalidOperationException($"{Field.Name} is a {Field.Type}, which is copied rather than kept by reference.");

        /// <summary>
        /// Writes the value, with <paramref name="format"/> handed to its own formatting, in the
        /// invariant culture: a bool as <c>true</c> or <c>false</c>, a date and time in ISO 8601, a
        /// time span as <c>c</c> writes it, an enum by its names, an exception as its type's name and
        /// its message, <c>none</c> for an optional value that is none and <c>null</c> for a null.
        /// </summary>
        public bool TryFormat(Span<char> destination, out int written, string? format = null)
        {
            if (IsNone)
                return LogText.Copy(Field.IsOptional ? "none" : "null", destination, out written);

            IFormatProvider invariant = CultureInfo.InvariantCulture;
            switch (Kind)
            {
                case LogValueKind.Boolean:
                    return LogText.Copy(Read<bool>() ? "true" : "false", destination, out written);
                case LogValueKind.Char:
                    written = destination.Length > 0 ? 1 : 0;
                    if (written == 1)
                        destination[0] = Read<char>();
                    return written == 1;
                case LogValueKind.SByte:
                    return Read<sbyte>().TryFormat(destination, out written, format, invariant);
                case LogValueKind.Byte:
                    return Read<byte>().TryFormat(destination, out written, format, invariant);
                case LogValueKind.Int16:
                    return Read<short>().TryFormat(destination, out written, format, invariant);
                case LogValueKind.UInt16:
                    return Read<ushort>().TryFormat(destination, out written, format, invariant);
                case LogValueKind.Int32:
                    return Read<int>().TryFormat(destination, out written, format, invariant);
                case LogValueKind.UInt32:
                    return Read<uint>().TryFormat(destination, out written, format, invariant);
                case LogValueKind.Int64:
                    return Read<long>().TryFormat(destination, out written, format, invariant);
                case LogValueKind.UInt64:
                    return Read<ulong>().TryFormat(destination, out written, format, invariant);
                case LogValueKind.Single:
                    return Read<float>().TryFormat(destination, out written, format, invariant);
                case LogValueKind.Double:
                    return Read<double>().TryFormat(destination, out written, format, invariant);
                case LogValueKind.Decimal:
                    return Read<decimal>().TryFormat(destination, out written, format, invariant);
                case LogValueKind.Guid:
                    return Read<Guid>().TryFormat(destination, out written, format);
                case LogValueKind.DateTime:
                    return Read<DateTime>().TryFormat(destination, out written, format ?? "O", invariant);
                case LogValueKind.DateTimeOffset:
                    return Read<DateTimeOffset>().TryFormat(destination, out written, format ?? "O", invariant);
                case LogValueKind.TimeSpan:
                    return Read<TimeSpan>().TryFormat(destination, out written, format ?? "c", invariant);
                case LogValueKind.Enum:
                    return Field.Names!.TryFormat(Read<long>(), destination, out written, format);
                case LogValueKind.String:
                    return LogText.Copy((string)_ref!, destination, out written);
                case LogValueKind.Exception:
                    var exception = (Exception)_ref!;
                    written = 0;
                    if (LogText.Append(exception.GetType().Name, destination, ref written)
                        && LogText.Append(": ", destination, ref written)
                        && LogText.Append(exception.Message, destination, ref written))
                        return true;
                    written = 0;
                    return false;
                case LogValueKind.Chars:
                    return LogText.Copy(AsChars(), destination, out written);
                case LogValueKind.Formattable:
                    return Field.Format!.TryFormat(ValueSlots, destination, out written, format);
                default:
                    throw new InvalidOperationException($"{Kind} is not a kind of value.");
            }
        }

        /// <summary>The value formatted, as a string: for a test or a debugger, since it allocates.</summary>
        public override string ToString()
        {
            char[] buffer = ArrayPool<char>.Shared.Rent(256);
            try
            {
                while (true)
                {
                    if (TryFormat(buffer, out int written))
                        return new string(buffer, 0, written);

                    int length = buffer.Length * 2;
                    ArrayPool<char>.Shared.Return(buffer);
                    buffer = ArrayPool<char>.Shared.Rent(length);
                }
            }
            finally
            {
                ArrayPool<char>.Shared.Return(buffer);
            }
        }
    }
}
