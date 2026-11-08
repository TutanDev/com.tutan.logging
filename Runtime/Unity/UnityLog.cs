using System;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Tutan.Logging
{
    /// <summary>
    /// The event Unity's own log reaches a store as, in category <c>unity</c>: every
    /// <c>Debug.Log</c>, warning, error, assertion and unhandled exception the
    /// <see cref="UnityLogBridge"/> hears, at the level it maps the message's type to.
    /// </summary>
    /// <remarks>
    /// Written by hand in the shape Tutan.Logging.Generators writes for a <c>[LogCategory]</c> class
    /// with an event whose level is chosen at the call, so that the package compiles without its own
    /// generator.
    /// </remarks>
    public static class UnityLog
    {
        /// <summary>The field of <c>unity/Logged</c> that holds the stack Unity handed over with the message.</summary>
        public const string StackTraceField = "StackTrace";

        /// <summary>Writes unity/Logged: {Message}.</summary>
        /// <remarks>The bridge passes an empty call site: where Unity's message came from is in its stack trace, not here.</remarks>
        internal static void Logged(this Log log, LogLevel level, string message, string stackTrace, LogType type,
            [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0)
        {
            if (!log.IsEnabled(Events.Logged, level))
                return;

            LogWriter writer = log.Write(Events.Logged, level, 0, callerFile, callerLine);
            try
            {
                writer.Ref(message);
                writer.Ref(stackTrace);
                writer.Value(unchecked((long)type));
            }
            finally
            {
                writer.Commit();
            }
        }

        /// <summary>The specs of the events this class declares, by name.</summary>
        public static class Events
        {
            /// <summary><c>unity/Logged</c>: {Message}</summary>
            public static readonly LogEventSpec Logged = LogEventSpec.Declare(
                "unity", "Logged", LogEventKind.Event, null,
                "{Message}",
                new LogField[]
                {
                    LogField.Ref("Message", LogValueKind.String, "string"),
                    LogField.Ref(StackTraceField, LogValueKind.String, "string"),
                    LogField.Enum("Type", "UnityEngine.LogType",
                        new[] { "Error", "Assert", "Warning", "Log", "Exception" },
                        new[] { 0L, 1L, 2L, 3L, 4L },
                        false),
                },
                0,
                Here());

            /// <summary>Every spec of the class, in the order declared.</summary>
            internal static LogEventSpec[] All => new[] { Logged };

            private static LogSource Here([CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
                new LogSource("Tutan.Logging", "Tutan.Logging.UnityLog", file, line);
        }

        /// <summary>The level a message of <paramref name="type"/> is captured at: a log is Information, a warning a Warning, and an error, a failed assertion or an exception an Error.</summary>
        public static LogLevel LevelOf(LogType type) => type switch
        {
            LogType.Log => LogLevel.Information,
            LogType.Warning => LogLevel.Warning,
            LogType.Error or LogType.Assert or LogType.Exception => LogLevel.Error,
            _ => LogLevel.Information,
        };
    }

    /// <summary>
    /// Whether the thread is inside a call of <see cref="UnityConsoleSink"/> to Unity's own log, which
    /// the <see cref="UnityLogBridge"/> must not capture, or each record would come back as Unity's,
    /// and go round again.
    /// </summary>
    internal static class UnityConsoleEcho
    {
        [ThreadStatic]
        private static bool t_writing;

        public static bool Writing => t_writing;

        /// <summary>Marks the thread for the length of a write: <c>using (UnityConsoleEcho.Mark()) { Debug.Log(...); }</c>.</summary>
        public static Marked Mark()
        {
            bool before = t_writing;
            t_writing = true;
            return new Marked(before);
        }

        public readonly struct Marked : IDisposable
        {
            private readonly bool _before;

            public Marked(bool before) => _before = before;

            public void Dispose() => t_writing = _before;
        }
    }
}
