using System.Collections.Generic;

namespace Tutan.Logging
{
    /// <summary>
    /// A session's log read back from its file by <see cref="LogFile.Read(string)"/>: its records, as
    /// an archive a console reads as it reads a live store, and the lines it could not read, which it
    /// went on past.
    /// </summary>
    public sealed class LogSessionFile
    {
        internal LogSessionFile(string path, LogArchive archive, IReadOnlyList<LogSkippedLine> skipped)
        {
            Path = path;
            Archive = archive;
            Skipped = skipped;
        }

        /// <summary>What it was read from: the file's path, or the name a stream was given.</summary>
        public string Path { get; }

        /// <summary>Its records, every one the file holds, which is its <see cref="LogRecords.Capacity"/>.</summary>
        public LogArchive Archive { get; }

        /// <summary>The lines it could not read, and why, in the order of the file.</summary>
        public IReadOnlyList<LogSkippedLine> Skipped { get; }
    }

    /// <summary>Why <see cref="LogFile.Read(string)"/> could not read a log at all: the <see cref="Tutan.Functional.Error.Code"/> of its error.</summary>
    public enum LogFileReadError
    {
        /// <summary>The file could not be opened or read.</summary>
        Unreadable = 1,

        /// <summary>The file's first line is not the line a <see cref="JsonLinesSink"/> starts a log with.</summary>
        NotALog,

        /// <summary>The file is a log of a format this reader does not read.</summary>
        UnknownFormat,
    }

    /// <summary>A line of a log's file that its reader could not read, which it went on past.</summary>
    /// <param name="Line">The line's number, from 1.</param>
    /// <param name="Reason">Why.</param>
    public sealed record LogSkippedLine(int Line, LogSkipReason Reason)
    {
        /// <summary>A sentence: <c>Line 12 is not JSON, ...</c>.</summary>
        public override string ToString() => $"Line {Line} {Reason}";
    }

    /// <summary>Why a line of a log's file was not read.</summary>
    public abstract record LogSkipReason
    {
        private LogSkipReason()
        {
        }

        /// <summary>The line is not JSON: the last line of a file is cut short when the program stopped while writing it.</summary>
        public sealed record NotJson : LogSkipReason
        {
            /// <inheritdoc />
            public override string ToString() =>
                "is not JSON, as the last line of a file is when the program stopped while writing it";
        }

        /// <summary>The line is JSON, but not a record, a declaration or a loss as the format writes them.</summary>
        public sealed record NotALine : LogSkipReason
        {
            /// <inheritdoc />
            public override string ToString() =>
                "is JSON, but not a record, a declaration or a loss as the format writes them";
        }

        /// <summary>A record that names a declaration the file does not make before it.</summary>
        /// <param name="Key">The event's key.</param>
        /// <param name="Id">The declaration it names.</param>
        public sealed record Undeclared(string Key, long Id) : LogSkipReason
        {
            /// <inheritdoc />
            public override string ToString() => $"is a record of {Key} as event {Id}, which the file does not declare before it";
        }

        /// <summary>A declaration that is not as the format writes one.</summary>
        /// <param name="Key">The key it declares.</param>
        /// <param name="Why">What is not as the format writes it.</param>
        public sealed record NotADeclaration(string Key, string Why) : LogSkipReason
        {
            /// <inheritdoc />
            public override string ToString() => $"declares {Key} as the format does not write a declaration: {Why}";
        }

        /// <summary>A declaration of what is not an event, as a program's declaration would have been refused when it was built.</summary>
        /// <param name="Key">The key it declares.</param>
        /// <param name="Why">Why it is not an event.</param>
        public sealed record NotAnEvent(string Key, string Why) : LogSkipReason
        {
            /// <inheritdoc />
            public override string ToString() => $"declares {Key}, which is not an event: {Why}";
        }

        /// <summary>A second declaration of one event number, of which the reader keeps the first.</summary>
        /// <param name="Key">The key the second declares.</param>
        /// <param name="Id">The number both declare.</param>
        public sealed record DeclaredTwice(string Key, long Id) : LogSkipReason
        {
            /// <inheritdoc />
            public override string ToString() => $"declares {Key} as event {Id}, which a line before it declares, and the first declaration is kept";
        }

        /// <summary>A record that is not as its event's declaration says.</summary>
        /// <param name="Key">The event's key.</param>
        /// <param name="Why">What is not as it says.</param>
        public sealed record NotItsDeclaration(string Key, string Why) : LogSkipReason
        {
            /// <inheritdoc />
            public override string ToString() => $"is a record of {Key} that is not as its declaration says: {Why}";
        }

        /// <summary>
        /// A record with a stamp no record can have: a sequence below 1, a frame below 0, a scope
        /// that does not come before it, or a call site that is not a file and a line.
        /// </summary>
        /// <param name="Stamp">The stamp, as the line names it: <c>frame</c>.</param>
        public sealed record NotAStamp(string Stamp) : LogSkipReason
        {
            /// <inheritdoc />
            public override string ToString() => $"is a record whose {Stamp} is not one a record can have";
        }

        /// <summary>A second record with one sequence, of which the reader keeps the first.</summary>
        /// <param name="Sequence">The sequence.</param>
        public sealed record SequenceTwice(long Sequence) : LogSkipReason
        {
            /// <inheritdoc />
            public override string ToString() => $"is a second record numbered {Sequence}, and the first is kept";
        }

        /// <summary>A loss after the last record, which no record follows to place it before.</summary>
        /// <param name="Count">How many records it says were lost.</param>
        public sealed record LostAtEnd(long Count) : LogSkipReason
        {
            /// <inheritdoc />
            public override string ToString() =>
                $"says {Count} records were lost after the last record, and no record follows to place the loss before";
        }
    }
}
