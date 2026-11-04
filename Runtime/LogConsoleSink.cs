using System;
using System.Threading;

namespace Tutan.Logging
{
    /// <summary>
    /// Keeps a session for the log console: every record a drain hands it, whole and typed, in a
    /// <see cref="LogConsoleSession.Live"/> of <see cref="LogConsole.Sessions"/>, from when the drain
    /// opens it until the drain lets it go, when the session has ended.
    /// <code>
    /// var drain = new LogDrain(store,
    ///     (new UnityConsoleSink(), new LogSinkOptions(LogLevel.Warning)),
    ///     (new LogConsoleSink(), new LogSinkOptions(LogLevel.Debug)));
    /// </code>
    /// </summary>
    /// <remarks>
    /// <para>
    /// A session holds at most its <see cref="Cap"/> of records, and past it lets the oldest go a
    /// block at a time, which its archive's <see cref="LogArchive.Dropped"/> counts. So the console
    /// keeps everything since it was last cleared, as Unity's Console does, and not only what a
    /// store's rings hold.
    /// </para>
    /// <para>
    /// A record under a scope whose opening the sink was not written, being below its level, is kept
    /// after that opening, read from the drain's scopes, so every record kept can say what it is
    /// under. Such a scope's closing is below the level too, so the session has none, and shows the
    /// scope as open.
    /// </para>
    /// <para>
    /// It keeps records whole, so the console can show every field; the console leaves out what is
    /// personal when it copies and exports. So its options redact nothing: a sink opened with options
    /// that do refuses them, and the drain says why. Outside the editor it keeps nothing, since no
    /// window can read it. Its cost is the records it keeps: their entries and values grow its
    /// session's blocks up to the cap, and past it the block it lets go is the next it fills, so it
    /// keeps records in order without allocating. A late record, as an error's context is, that
    /// lands in a full block halves it, which makes a block, or two when it has just used its spare.
    /// </para>
    /// <para>
    /// A sink that throws is written to no more, so its session ends then, as when its drain lets it go.
    /// </para>
    /// </remarks>
    public sealed class LogConsoleSink : ILogSink, IDisposable
    {
        /// <summary>The most records a session holds unless its sink is given another cap: one million.</summary>
        public const int DefaultCap = 1_000_000;

        /// <summary>The deepest a chain of scopes is followed, as a view follows one.</summary>
        private const int MostDepth = 256;

        private readonly ConsoleSessions _sessions;

        // Opening and disposing change the stage whole, so a sink opened by two drains at once, or
        // disposed as it is opened, never leaves a live session no one ends. Writing only reads it.
        private readonly object _changing = new object();
        private Stage _stage = Stage.Unopened.Instance;

        /// <summary>A sink that keeps a session of the process's, once a drain opens it.</summary>
        /// <param name="cap">The most records its session holds, past which it lets the oldest go.</param>
        /// <param name="name">What the log calls the sink.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="cap"/> is less than 1.</exception>
        public LogConsoleSink(int cap = DefaultCap, string name = "log console")
            : this(cap, name, LogConsole.Process)
        {
        }

        /// <summary>A sink that keeps its session in <paramref name="sessions"/>.</summary>
        internal LogConsoleSink(int cap, string name, ConsoleSessions sessions)
        {
            if (cap < 1)
                throw new ArgumentOutOfRangeException(nameof(cap), $"A session holds at least one record, and a cap of {cap} was given.");

            Cap = cap;
            Name = name ?? throw new ArgumentNullException(nameof(name));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        }

        /// <inheritdoc />
        public string Name { get; }

        /// <summary>The most records its session holds.</summary>
        public int Cap { get; }

        /// <summary>Starts its session, of the records' program and clock; in the editor only.</summary>
        /// <exception cref="InvalidOperationException">It was opened before: a sink keeps one session.</exception>
        /// <exception cref="ObjectDisposedException">It was disposed.</exception>
        /// <exception cref="ArgumentException">Its options redact, and it keeps records whole.</exception>
        public void Open(LogContext context)
        {
            if (context is null)
                throw new ArgumentNullException(nameof(context));
            if (context.Options.Redact != LogRedaction.None)
                throw new ArgumentException(
                    $"The console sink {Name} keeps records whole, and its options leave {context.Options.Redact} out: the console leaves that out of what it copies and exports, so give it options that redact nothing.",
                    nameof(context));

            lock (_changing)
            {
                switch (_stage)
                {
                    case Stage.Disposed:
                        throw new ObjectDisposedException(Name, $"The console sink {Name} was disposed, and its session, if it kept one, has ended.");
                    case not Stage.Unopened:
                        throw new InvalidOperationException($"The console sink {Name} keeps one session, and was opened again: give each drain a sink of its own.");
                }

                // What the records already lack, as a file's do, the session lacks too.
                Stage opened = _sessions.Start(context.Program, context.Clock, Cap, context.Redact)
                    .Match<Stage>(static () => Stage.Idle.Instance, static live => new Stage.Keeping(live));
                Volatile.Write(ref _stage, opened);
            }
        }

        /// <summary>Keeps the record, after the openings of the scopes it is under that its session lacks.</summary>
        public void Write(LogRecord record, bool backlog, LogContext context)
        {
            if (Volatile.Read(ref _stage) is not Stage.Keeping keeping)
                return;

            try
            {
                LogArchive records = keeping.Session.Records;
                KeepOpenings(records, record.Kind == LogRecordKind.ScopeEnd ? record.Begin : record.Scope, context, 0);
                records.Keep(record, backlog);
            }
            catch
            {
                Failed(keeping);
                throw;
            }
        }

        /// <summary>Counts the records lost before the next it keeps.</summary>
        public void Lost(long count, LogContext context)
        {
            if (Volatile.Read(ref _stage) is not Stage.Keeping keeping)
                return;

            try
            {
                keeping.Session.Records.Lose(count);
            }
            catch
            {
                Failed(keeping);
                throw;
            }
        }

        /// <inheritdoc />
        public void Flush()
        {
        }

        /// <summary>Ends its session, which stays in the console until it is cleared.</summary>
        public void Dispose()
        {
            Stage was;
            lock (_changing)
            {
                was = _stage;
                Volatile.Write(ref _stage, Stage.Disposed.Instance);
            }

            if (was is Stage.Keeping keeping)
                _sessions.End(keeping.Session, DateTimeOffset.Now);
        }

        /// <summary>
        /// Ends its session once it has thrown: its drain writes to it no more and does not dispose it,
        /// so the session would stay live, and could neither be closed nor end.
        /// </summary>
        private void Failed(Stage.Keeping keeping)
        {
            lock (_changing)
            {
                // Disposed meanwhile, and ended by that.
                if (!ReferenceEquals(_stage, keeping))
                    return;

                Volatile.Write(ref _stage, Stage.Idle.Instance);
            }

            _sessions.End(keeping.Session, DateTimeOffset.Now);
        }

        /// <summary>
        /// Keeps the opening of <paramref name="scope"/>, and before it those of the scopes it is under,
        /// that the session would keep and the drain still has, outermost first.
        /// </summary>
        private static void KeepOpenings(LogArchive records, long scope, LogContext context, int depth)
        {
            if (scope == 0 || depth == MostDepth || !records.CanKeep(scope) || !context.TryScope(scope, out LogRecord opening))
                return;

            KeepOpenings(records, opening.Scope, context, depth + 1);
            records.Keep(opening, backlog: false);
        }

        /// <summary>
        /// Where a sink is in its one session: not opened yet; keeping it; opened and keeping none,
        /// outside the editor or once it threw and its session ended; or disposed, when its session,
        /// if it kept one, has ended.
        /// </summary>
        private abstract record Stage
        {
            private Stage()
            {
            }

            public sealed record Unopened : Stage
            {
                public static readonly Unopened Instance = new Unopened();

                private Unopened()
                {
                }
            }

            public sealed record Keeping(LogConsoleSession.Live Session) : Stage;

            public sealed record Idle : Stage
            {
                public static readonly Idle Instance = new Idle();

                private Idle()
                {
                }
            }

            public sealed record Disposed : Stage
            {
                public static readonly Disposed Instance = new Disposed();

                private Disposed()
                {
                }
            }
        }
    }
}
