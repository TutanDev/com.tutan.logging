using System;

namespace Tutan.Logging
{
    /// <summary>What an event is: a moment, or an operation with a beginning and an end.</summary>
    public enum LogEventKind : byte
    {
        /// <summary>Something that happened, declared with <see cref="LogEventAttribute"/>.</summary>
        Event,

        /// <summary>An operation the records written through its <see cref="LogScope.Log"/> are under, declared with <see cref="LogScopeAttribute"/>.</summary>
        Scope,
    }

    /// <summary>What a record is.</summary>
    public enum LogRecordKind : byte
    {
        /// <summary>An event's record, with its fields.</summary>
        Event,

        /// <summary>A scope's opening, with its fields. Its sequence is the scope's id.</summary>
        ScopeBegin,

        /// <summary>A scope's closing, which holds when it opened and no fields.</summary>
        ScopeEnd,
    }

    /// <summary>
    /// How a field's value is held, and so how a reader reads it. A value of every kind but
    /// <see cref="String"/> and <see cref="Exception"/> is copied into the store's bits at the call;
    /// those two are kept by reference.
    /// </summary>
    public enum LogValueKind : byte
    {
        /// <summary>A <see cref="bool"/>.</summary>
        Boolean,

        /// <summary>A <see cref="char"/>.</summary>
        Char,

        /// <summary>An <see cref="sbyte"/>.</summary>
        SByte,

        /// <summary>A <see cref="byte"/>.</summary>
        Byte,

        /// <summary>A <see cref="short"/>.</summary>
        Int16,

        /// <summary>A <see cref="ushort"/>.</summary>
        UInt16,

        /// <summary>An <see cref="int"/>.</summary>
        Int32,

        /// <summary>A <see cref="uint"/>.</summary>
        UInt32,

        /// <summary>A <see cref="long"/>, or an <see cref="IntPtr"/> widened to one.</summary>
        Int64,

        /// <summary>A <see cref="ulong"/>, or a <see cref="UIntPtr"/> widened to one.</summary>
        UInt64,

        /// <summary>A <see cref="float"/>.</summary>
        Single,

        /// <summary>A <see cref="double"/>.</summary>
        Double,

        /// <summary>A <see cref="decimal"/>.</summary>
        Decimal,

        /// <summary>A <see cref="System.Guid"/>.</summary>
        Guid,

        /// <summary>A <see cref="System.DateTime"/>.</summary>
        DateTime,

        /// <summary>A <see cref="System.DateTimeOffset"/>.</summary>
        DateTimeOffset,

        /// <summary>A <see cref="System.TimeSpan"/>.</summary>
        TimeSpan,

        /// <summary>An enum, held as a <see cref="long"/> and named by the table its field carries.</summary>
        Enum,

        /// <summary>A <see cref="string"/>, kept by reference, which is safe because it cannot change.</summary>
        String,

        /// <summary>An <see cref="System.Exception"/>, kept by reference.</summary>
        Exception,

        /// <summary>A <see cref="ReadOnlySpan{T}"/> of chars, copied four to a slot and cut at <see cref="LogWriter.MaxChars"/>.</summary>
        Chars,

        /// <summary>A value of an unmanaged <see cref="IFormattable"/> type, as <c>Vector3</c>, which formats itself when a reader reads it.</summary>
        Formattable,
    }

    /// <summary>What a sink leaves out of what it writes.</summary>
    [Flags]
    public enum LogRedaction : byte
    {
        /// <summary>Nothing: every field as it was logged.</summary>
        None = 0,

        /// <summary>Every field marked <see cref="PersonalAttribute"/>, written as <c>[personal]</c>.</summary>
        Personal = 1,

        /// <summary>The user's home folder, wherever a field's text has it, written as <c>~</c>.</summary>
        HomeFolder = 2,
    }
}
