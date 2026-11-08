using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Tutan.Functional;

namespace Tutan.Logging
{
    /// <summary>
    /// Which session of the process a <see cref="LogConsoleSession"/> is, whatever state it is in:
    /// numbered from 1, in the order the process's sessions started or were opened. Its
    /// <c>default</c> is no session, which <see cref="LogConsole.Find"/> never finds.
    /// </summary>
    public readonly record struct LogSessionId : IComparable<LogSessionId>
    {
        internal LogSessionId(long value) => Value = value;

        /// <summary>Its number, from 1; 0 for <c>default</c>.</summary>
        public long Value { get; }

        /// <summary>Whether <paramref name="left"/> started or was opened before <paramref name="right"/>.</summary>
        public static bool operator <(LogSessionId left, LogSessionId right) => left.Value < right.Value;

        /// <summary>Whether <paramref name="left"/> started or was opened after <paramref name="right"/>.</summary>
        public static bool operator >(LogSessionId left, LogSessionId right) => left.Value > right.Value;

        /// <summary>Whether <paramref name="left"/> is <paramref name="right"/>, or started or was opened before it.</summary>
        public static bool operator <=(LogSessionId left, LogSessionId right) => left.Value <= right.Value;

        /// <summary>Whether <paramref name="left"/> is <paramref name="right"/>, or started or was opened after it.</summary>
        public static bool operator >=(LogSessionId left, LogSessionId right) => left.Value >= right.Value;

        /// <summary>Its order among the process's sessions: a session that started or was opened later is greater.</summary>
        public int CompareTo(LogSessionId other) => Value.CompareTo(other.Value);

        /// <summary>Its number.</summary>
        public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// One run of a program's records, as the log console keeps it: <see cref="Live"/>, kept by a
    /// <see cref="LogConsoleSink"/> as the program runs; <see cref="Ended"/>, once the drain let that
    /// sink go; or <see cref="File"/>, read back from a session's file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A session is a value of its state, and its state never changes in place: when a live session
    /// ends, <see cref="LogConsole.Sessions"/> is a new list with an <see cref="Ended"/> where its
    /// <see cref="Live"/> was, under the same <see cref="Id"/>. So two values are equal when they are
    /// of one state, and a reader that holds a session finds its state now by its id.
    /// </para>
    /// <para>
    /// Its <see cref="Records"/> are one archive for the session's whole life, which a
    /// <see cref="LogView"/> reads. A live session's grows as its sink keeps records, and lets its
    /// oldest go past its cap and everything go when the console is cleared; an ended session's and a
    /// file's take nothing after.
    /// </para>
    /// </remarks>
    public abstract record LogConsoleSession
    {
        private LogConsoleSession()
        {
        }

        /// <summary>Which session it is.</summary>
        public abstract LogSessionId Id { get; }

        /// <summary>Its records, which a view reads as it reads a live store.</summary>
        public abstract LogArchive Records { get; }

        /// <summary>What the function for its state makes of it: so a reader that tells the states apart is told of every one.</summary>
        public abstract R Match<R>(Func<Live, R> live, Func<Ended, R> ended, Func<File, R> file);

        /// <summary>Whether it is the session of <paramref name="file"/>.</summary>
        /// <remarks>
        /// Only this assembly and its tests can override it, so no other assembly can make a session
        /// through the copy constructor C# gives a record that is not sealed.
        /// </remarks>
        internal abstract bool IsOf(LogSessionFile file);

        /// <summary>A session a console sink keeps as its program runs, and writes every record it is handed to.</summary>
        public sealed record Live : LogConsoleSession
        {
            internal Live(LogSessionId id, LogArchive records)
            {
                Id = id;
                Records = records;
            }

            /// <inheritdoc />
            public override LogSessionId Id { get; }

            /// <summary>Its records, at most its sink's cap of them, which its <see cref="LogRecords.Capacity"/> says.</summary>
            public override LogArchive Records { get; }

            /// <inheritdoc />
            public override R Match<R>(Func<Live, R> live, Func<Ended, R> ended, Func<File, R> file) => live(this);

            /// <inheritdoc />
            internal override bool IsOf(LogSessionFile file) => false;
        }

        /// <summary>
        /// A session whose drain let its sink go, or whose sink threw and is written to no more, which
        /// stays until the console is cleared, so a Play Mode session can be read after Play Mode exits.
        /// </summary>
        public sealed record Ended : LogConsoleSession
        {
            internal Ended(LogSessionId id, LogArchive records, DateTimeOffset at)
            {
                Id = id;
                Records = records;
                At = at;
            }

            /// <inheritdoc />
            public override LogSessionId Id { get; }

            /// <summary>What it kept before it ended, which takes nothing after.</summary>
            public override LogArchive Records { get; }

            /// <summary>When its drain let its sink go, or its sink threw.</summary>
            public DateTimeOffset At { get; }

            /// <inheritdoc />
            public override R Match<R>(Func<Live, R> live, Func<Ended, R> ended, Func<File, R> file) => ended(this);

            /// <inheritdoc />
            internal override bool IsOf(LogSessionFile file) => false;
        }

        /// <summary>A session read back from its file, which stays until it is closed.</summary>
        public sealed record File : LogConsoleSession
        {
            private readonly LogSessionFile _file;

            internal File(LogSessionId id, LogSessionFile file)
            {
                Id = id;
                _file = file;
            }

            /// <inheritdoc />
            public override LogSessionId Id { get; }

            /// <summary>Every record the file holds.</summary>
            public override LogArchive Records => _file.Archive;

            /// <summary>What it was read from: the file's path, or the name a stream was given.</summary>
            public string Path => _file.Path;

            /// <summary>The lines of the file that could not be read, and why, in the order of the file.</summary>
            public IReadOnlyList<LogSkippedLine> Skipped => _file.Skipped;

            /// <inheritdoc />
            public override R Match<R>(Func<Live, R> live, Func<Ended, R> ended, Func<File, R> file) => file(this);

            /// <inheritdoc />
            internal override bool IsOf(LogSessionFile file) => ReferenceEquals(_file, file);
        }
    }

    /// <summary>
    /// The sessions a console shows, and the one place they change: started and ended by console
    /// sinks, opened from files, closed, and cleared. Each change makes a new list, which it publishes
    /// whole, so a reader's list never changes under it. <see cref="LogConsole"/> holds the process's;
    /// a test makes its own.
    /// </summary>
    internal sealed class ConsoleSessions
    {
        private static readonly IReadOnlyList<LogConsoleSession> s_none = Array.AsReadOnly(Array.Empty<LogConsoleSession>());

        private readonly object _lock = new object();
        private readonly bool _live;
        private IReadOnlyList<LogConsoleSession> _sessions = s_none;
        private long _last;

        /// <summary>No sessions yet.</summary>
        /// <param name="live">Whether a console sink starts a live session in it, which is so in the editor only.</param>
        public ConsoleSessions(bool live) => _live = live;

        /// <summary>The sessions, the oldest first: a new list each time one starts, ends, is opened, is closed, or is cleared away.</summary>
        public IReadOnlyList<LogConsoleSession> Sessions => Volatile.Read(ref _sessions);

        /// <summary>The session <paramref name="id"/> as it is now, while it is one of them.</summary>
        public Optional<LogConsoleSession> Find(LogSessionId id) => Find(Sessions, id);

        /// <summary>
        /// A new live session of <paramref name="program"/>'s records, holding at most
        /// <paramref name="cap"/> of them, which lack <paramref name="redacted"/>: none when it starts
        /// no live sessions.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="cap"/> is less than 1.</exception>
        public Optional<LogConsoleSession.Live> Start(string program, LogClock clock, int cap, LogRedaction redacted)
        {
            if (cap < 1)
                throw new ArgumentOutOfRangeException(nameof(cap), $"A session holds at least one record, and a cap of {cap.ToString(CultureInfo.InvariantCulture)} was given.");
            if (!_live)
                return F.None;

            var records = new LogArchive(program, clock, cap, redacted);
            lock (_lock)
            {
                var live = new LogConsoleSession.Live(new LogSessionId(++_last), records);
                Publish(With(_sessions, live));
                return live;
            }
        }

        /// <summary>
        /// Ends <paramref name="live"/>, as of <paramref name="at"/>: its records take nothing after,
        /// and it stays until the console is cleared. Does nothing once it has ended, or when it is not one of them.
        /// </summary>
        public void End(LogConsoleSession.Live live, DateTimeOffset at)
        {
            if (live is null)
                throw new ArgumentNullException(nameof(live));

            lock (_lock)
            {
                // The session this list holds, and not another list's of the same number.
                if (!Find(_sessions, live.Id).HasValue(out LogConsoleSession now) || now is not LogConsoleSession.Live current
                    || !ReferenceEquals(current, live))
                    return;

                current.Records.Seal();
                Publish(Replaced(_sessions, new LogConsoleSession.Ended(current.Id, current.Records, at)));
            }
        }

        /// <summary>The session of <paramref name="file"/>, added to them; the one it is already, when it was opened before.</summary>
        public LogConsoleSession.File Open(LogSessionFile file)
        {
            if (file is null)
                throw new ArgumentNullException(nameof(file));

            lock (_lock)
            {
                foreach (LogConsoleSession session in _sessions)
                {
                    if (session is LogConsoleSession.File opened && opened.IsOf(file))
                        return opened;
                }

                var added = new LogConsoleSession.File(new LogSessionId(++_last), file);
                Publish(With(_sessions, added));
                return added;
            }
        }

        /// <summary>Lets the ended session go; does nothing when it is gone already.</summary>
        public void Close(LogConsoleSession.Ended session) => Remove((session ?? throw new ArgumentNullException(nameof(session))).Id);

        /// <summary>Lets the file's session go; does nothing when it is gone already.</summary>
        public void Close(LogConsoleSession.File session) => Remove((session ?? throw new ArgumentNullException(nameof(session))).Id);

        /// <summary>
        /// Lets ended sessions go, and empties live ones, which keep what they are handed after; a
        /// file's session stays until it is closed.
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                foreach (LogConsoleSession session in _sessions)
                {
                    if (session is LogConsoleSession.Live live)
                        live.Records.Clear();
                }

                if (_sessions.Any(static s => s is LogConsoleSession.Ended))
                    Publish(Only(_sessions, static s => s is not LogConsoleSession.Ended));
            }
        }

        // An ended session and a file's are as they will be until they go, so the id names the one closed.
        private void Remove(LogSessionId id)
        {
            lock (_lock)
            {
                if (Find(_sessions, id).IsSome)
                    Publish(Only(_sessions, s => s.Id != id));
            }
        }

        private static Optional<LogConsoleSession> Find(IReadOnlyList<LogConsoleSession> sessions, LogSessionId id)
        {
            for (int i = 0; i < sessions.Count; i++)
            {
                if (sessions[i].Id == id)
                    return F.Some(sessions[i]);
            }

            return F.None;
        }

        private static LogConsoleSession[] With(IReadOnlyList<LogConsoleSession> sessions, LogConsoleSession added) =>
            sessions.Append(added).ToArray();

        /// <summary>The sessions with <paramref name="state"/> where the session of its id was.</summary>
        private static LogConsoleSession[] Replaced(IReadOnlyList<LogConsoleSession> sessions, LogConsoleSession state) =>
            sessions.Select(s => s.Id == state.Id ? state : s).ToArray();

        private static LogConsoleSession[] Only(IReadOnlyList<LogConsoleSession> sessions, Func<LogConsoleSession, bool> keep) =>
            sessions.Where(keep).ToArray();

        // With the lock held.
        private void Publish(LogConsoleSession[] sessions) => Volatile.Write(ref _sessions, Array.AsReadOnly(sessions));
    }
}
