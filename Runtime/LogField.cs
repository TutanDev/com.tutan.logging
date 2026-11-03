using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Tutan.Functional;
using Unity.Collections.LowLevel.Unsafe;

namespace Tutan.Logging
{
    /// <summary>
    /// One field of an event: its name, how its value is held, the type it was declared as, and what
    /// its declaration says about it. A spec's fields are in the order of the method's parameters,
    /// which is the order their values are written in.
    /// </summary>
    /// <remarks>
    /// The generator makes these through the factories, one per way a value is held. A hand-written
    /// spec can too, but nothing checks it against a method, so they are the generator's to use.
    /// </remarks>
    public sealed class LogField
    {
        /// <summary>The most bytes a value of an unmanaged formattable type may have, so that a record always fits the smallest rings.</summary>
        public const int MaxValueBytes = 256;

        private LogField(string name, LogValueKind kind, string type, int slots, bool personal, string? tag, bool optional,
            LogEnumNames? names, ValueFormat? format)
        {
            if (name is null || !LogNames.IsEventName(name))
                throw new ArgumentException($"'{name}' is not a field's name, which is a letter or '_' followed by letters, digits and '_'.", nameof(name));

            Name = name;
            Kind = kind;
            Type = type ?? throw new ArgumentNullException(nameof(type));
            Slots = slots;
            Personal = personal;
            Tag = tag is null ? F.None : F.Some(tag);
            IsOptional = optional;
            Names = names;
            Format = format;
        }

        /// <summary>The name, as the template's hole spells it, or the parameter's with its first letter made upper case when no hole names it.</summary>
        public string Name { get; }

        /// <summary>How the value is held.</summary>
        public LogValueKind Kind { get; }

        /// <summary>The type the parameter was declared as: <c>int</c>, <c>System.Guid</c>, <c>UnityEngine.Vector3</c>.</summary>
        public string Type { get; }

        /// <summary>The tag <see cref="LogTagAttribute"/> gave it, which says what a reader can do with it.</summary>
        public Optional<string> Tag { get; }

        /// <summary>Whether it is marked <see cref="PersonalAttribute"/>.</summary>
        public bool Personal { get; }

        /// <summary>Whether it was declared as an <see cref="Optional{T}"/>, so it can be none.</summary>
        public bool IsOptional { get; }

        /// <summary>How many slots of bits the value takes, without the one an optional value's presence takes: 0 for a value kept by reference, and for chars, whose length varies.</summary>
        public int Slots { get; }

        /// <summary>Whether the value is kept by reference rather than copied into bits.</summary>
        public bool IsRef => Kind is LogValueKind.String or LogValueKind.Exception;

        /// <summary>
        /// The slots a record always gives the field: its value's, its presence's, and for chars the one
        /// that holds their length. A value kept by reference takes none, and its presence is its null.
        /// </summary>
        internal int FixedSlots => IsRef ? 0 : Kind == LogValueKind.Chars ? 1 : Slots + (IsOptional ? 1 : 0);

        internal LogEnumNames? Names { get; }

        /// <summary>How a value of an unmanaged formattable type is formatted: with the type's own code.</summary>
        internal ValueFormat? Format { get; }

        /// <summary>A field of one of the built-in value kinds, from <see cref="LogValueKind.Boolean"/> to <see cref="LogValueKind.TimeSpan"/>, held as a <typeparamref name="T"/>.</summary>
        /// <exception cref="ArgumentException"><paramref name="kind"/> is not held as a <typeparamref name="T"/>.</exception>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static LogField Value<T>(string name, LogValueKind kind, string type, bool personal = false, string? tag = null, bool optional = false)
            where T : unmanaged
        {
            if (!HeldAs<T>(kind))
                throw new ArgumentException($"A field of kind {kind} is not held as a {typeof(T).Name}.", nameof(kind));

            return new LogField(name, kind, type, SlotsOf<T>(), personal, tag, optional, null, null);
        }

        /// <summary>A field of an enum, held as a <see cref="long"/> and named by the table given: its members' names and values, in any order.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static LogField Enum(string name, string type, string[] names, long[] values, bool flags,
            bool personal = false, string? tag = null, bool optional = false)
        {
            if (names is null)
                throw new ArgumentNullException(nameof(names));
            if (values is null)
                throw new ArgumentNullException(nameof(values));
            if (names.Length != values.Length)
                throw new ArgumentException($"The enum {type} has {names.Length} names and {values.Length} values.", nameof(values));

            return new LogField(name, LogValueKind.Enum, type, 1, personal, tag, optional, new LogEnumNames(names, values, flags), null);
        }

        /// <summary>A field kept by reference: a <see cref="LogValueKind.String"/> or an <see cref="LogValueKind.Exception"/>.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static LogField Ref(string name, LogValueKind kind, string type, bool personal = false, string? tag = null, bool optional = false)
        {
            if (kind is not (LogValueKind.String or LogValueKind.Exception))
                throw new ArgumentException($"A field of kind {kind} is not kept by reference.", nameof(kind));

            return new LogField(name, kind, type, 0, personal, tag, optional, null, null);
        }

        /// <summary>A field of chars, copied into bits at the call.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static LogField Chars(string name, string type, bool personal = false, string? tag = null) =>
            new LogField(name, LogValueKind.Chars, type, 0, personal, tag, false, null, null);

        /// <summary>
        /// A field of an unmanaged type that formats itself into a string, as <c>Vector3</c> does: its
        /// bits are copied at the call, and a reader pays for the string when it reads, which the call
        /// never does.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static LogField Formattable<T>(string name, string type, bool personal = false, string? tag = null, bool optional = false)
            where T : unmanaged, IFormattable =>
            new LogField(name, LogValueKind.Formattable, type, SlotsOf<T>(), personal, tag, optional, null, new FormattableFormat<T>());

        /// <summary><c>Name: Type</c>.</summary>
        public override string ToString() => $"{Name}: {Type}";

        private static int SlotsOf<T>() where T : unmanaged
        {
            int size = SizeOf<T>.Bytes;
            if (size > MaxValueBytes)
                throw new ArgumentException($"A {typeof(T).Name} is {size} bytes, and a field's value is at most {MaxValueBytes}.");

            return (size + 7) >> 3;
        }

        private static bool HeldAs<T>(LogValueKind kind) => kind switch
        {
            LogValueKind.Boolean => typeof(T) == typeof(bool),
            LogValueKind.Char => typeof(T) == typeof(char),
            LogValueKind.SByte => typeof(T) == typeof(sbyte),
            LogValueKind.Byte => typeof(T) == typeof(byte),
            LogValueKind.Int16 => typeof(T) == typeof(short),
            LogValueKind.UInt16 => typeof(T) == typeof(ushort),
            LogValueKind.Int32 => typeof(T) == typeof(int),
            LogValueKind.UInt32 => typeof(T) == typeof(uint),
            LogValueKind.Int64 => typeof(T) == typeof(long),
            LogValueKind.UInt64 => typeof(T) == typeof(ulong),
            LogValueKind.Single => typeof(T) == typeof(float),
            LogValueKind.Double => typeof(T) == typeof(double),
            LogValueKind.Decimal => typeof(T) == typeof(decimal),
            LogValueKind.Guid => typeof(T) == typeof(Guid),
            LogValueKind.DateTime => typeof(T) == typeof(DateTime),
            LogValueKind.DateTimeOffset => typeof(T) == typeof(DateTimeOffset),
            LogValueKind.TimeSpan => typeof(T) == typeof(TimeSpan),
            _ => false,
        };
    }

    /// <summary>The size of an unmanaged <typeparamref name="T"/>, asked of Unity once per type and then read as a field.</summary>
    internal static class SizeOf<T> where T : unmanaged
    {
        public static readonly int Bytes = UnsafeUtility.SizeOf<T>();
    }

    /// <summary>
    /// An enum's members, which name its values without reflection: a value that is a member is its
    /// name, and a <c>[Flags]</c> value that is members combined is their names joined by <c> | </c>.
    /// </summary>
    internal sealed class LogEnumNames
    {
        private readonly string[] _names;
        private readonly long[] _values;

        public LogEnumNames(string[] names, long[] values, bool flags)
        {
            // Ascending by value, so a flags value's names come out lowest first, as they read.
            _names = (string[])names.Clone();
            _values = (long[])values.Clone();
            Array.Sort(_values, _names);
            Flags = flags;
        }

        public bool Flags { get; }

        /// <summary>The members' names, ascending by value.</summary>
        public ReadOnlySpan<string> Names => _names;

        /// <summary>The members' values, ascending.</summary>
        public ReadOnlySpan<long> Values => _values;

        /// <summary>
        /// Writes <paramref name="value"/>'s name, or its names, or the number when no member or
        /// combination of members is it. <c>D</c> and <c>X</c> write the number, in decimal or hex.
        /// </summary>
        public bool TryFormat(long value, Span<char> destination, out int written, string? format)
        {
            if (format is "D" or "d")
                return value.TryFormat(destination, out written, default, CultureInfo.InvariantCulture);
            if (format is "X" or "x")
                return value.TryFormat(destination, out written, format, CultureInfo.InvariantCulture);

            int exact = Array.BinarySearch(_values, value);
            if (exact >= 0)
            {
                written = 0;
                return LogText.Append(_names[exact], destination, ref written);
            }

            if (!Flags || value == 0)
                return value.TryFormat(destination, out written, default, CultureInfo.InvariantCulture);

            Span<bool> chosen = _values.Length <= 256 ? stackalloc bool[_values.Length] : new bool[_values.Length];
            long remaining = value;
            for (int i = _values.Length - 1; i >= 0 && remaining != 0; i--)
            {
                long member = _values[i];
                if (member != 0 && (value & member) == member && (remaining & member) != 0)
                {
                    chosen[i] = true;
                    remaining &= ~member;
                }
            }

            if (remaining != 0)
                return value.TryFormat(destination, out written, default, CultureInfo.InvariantCulture);

            written = 0;
            bool first = true;
            for (int i = 0; i < _values.Length; i++)
            {
                if (!chosen[i])
                    continue;

                if (!first && !LogText.Append(" | ", destination, ref written))
                    return false;
                if (!LogText.Append(_names[i], destination, ref written))
                    return false;
                first = false;
            }

            return true;
        }

        /// <summary>
        /// Reads back what <see cref="TryFormat"/> writes with no format: a member's name, a
        /// <c>[Flags]</c> value's names joined by <c> | </c>, or the number. Names are read as written,
        /// with their case.
        /// </summary>
        public bool TryParse(string text, out long value)
        {
            if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
                return true;

            value = 0;
            for (int start = 0; ;)
            {
                int bar = text.IndexOf(" | ", start, StringComparison.Ordinal);
                if (bar >= 0 && !Flags)
                    return Fail(out value);

                string name = bar < 0 ? text.Substring(start) : text.Substring(start, bar - start);
                int member = Array.IndexOf(_names, name);
                if (member < 0)
                    return Fail(out value);

                value |= _values[member];
                if (bar < 0)
                    return true;

                start = bar + 3;
            }
        }

        private static bool Fail(out long value)
        {
            value = 0;
            return false;
        }
    }

    /// <summary>Formats a value of an unmanaged formattable type from the slots it was copied into.</summary>
    internal abstract class ValueFormat
    {
        public abstract bool TryFormat(ReadOnlySpan<long> slots, Span<char> destination, out int written, string? format);
    }

    /// <summary>
    /// Formats a value through its own <see cref="IFormattable.ToString(string, IFormatProvider)"/>,
    /// in the invariant culture: a string a reader allocates when it reads, which the call never does.
    /// </summary>
    internal sealed class FormattableFormat<T> : ValueFormat where T : unmanaged, IFormattable
    {
        public override bool TryFormat(ReadOnlySpan<long> slots, Span<char> destination, out int written, string? format)
        {
            string text = MemoryMarshal.Read<T>(MemoryMarshal.AsBytes(slots)).ToString(format, CultureInfo.InvariantCulture);
            return LogText.Copy(text, destination, out written);
        }
    }
}
