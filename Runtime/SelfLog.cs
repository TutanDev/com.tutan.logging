using System;
using System.Runtime.CompilerServices;

namespace Tutan.Logging
{
    /// <summary>What the log says about itself, in category <c>log</c>, so its trouble is in the same place as everything else's.</summary>
    /// <remarks>
    /// Written by hand in the shape Tutan.Logging.Generators writes for a <c>[LogCategory]</c> class, so
    /// that the package compiles without its own generator. A change to what the generator writes is
    /// made here too.
    /// </remarks>
    internal static class SelfLog
    {
        /// <summary>Writes log/DrainFellBehind: {Count} records were overwritten before the drain read them.</summary>
        public static void DrainFellBehind(this Log log, long count,
            [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0)
        {
            if (!log.IsEnabled(Events.DrainFellBehind))
                return;

            LogWriter writer = log.Write(Events.DrainFellBehind, LogLevel.Warning, 0, callerFile, callerLine);
            try
            {
                writer.Value(count);
            }
            finally
            {
                writer.Commit();
            }
        }

        /// <summary>Writes log/SinkFailed: The sink {Sink} failed, and is not written to again.</summary>
        public static void SinkFailed(this Log log, string sink, Exception exception,
            [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0)
        {
            if (!log.IsEnabled(Events.SinkFailed))
                return;

            LogWriter writer = log.Write(Events.SinkFailed, LogLevel.Error, 0, callerFile, callerLine);
            try
            {
                writer.Ref(sink);
                writer.Ref(exception);
            }
            finally
            {
                writer.Commit();
            }
        }

        /// <summary>Writes log/KeyTaken: {Key} is declared by {Declarer} and again by {Other}, and a filter by its key finds only the first.</summary>
        public static void KeyTaken(this Log log, string key, string declarer, string other,
            [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0)
        {
            if (!log.IsEnabled(Events.KeyTaken))
                return;

            LogWriter writer = log.Write(Events.KeyTaken, LogLevel.Warning, 0, callerFile, callerLine);
            try
            {
                writer.Ref(key);
                writer.Ref(declarer);
                writer.Ref(other);
            }
            finally
            {
                writer.Commit();
            }
        }

        /// <summary>Writes log/CategoryTaken: The category {Category} is declared by {Declarer} and again by {Other}.</summary>
        public static void CategoryTaken(this Log log, string category, string declarer, string other,
            [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0)
        {
            if (!log.IsEnabled(Events.CategoryTaken))
                return;

            LogWriter writer = log.Write(Events.CategoryTaken, LogLevel.Warning, 0, callerFile, callerLine);
            try
            {
                writer.Ref(category);
                writer.Ref(declarer);
                writer.Ref(other);
            }
            finally
            {
                writer.Commit();
            }
        }

        /// <summary>Writes log/RuleMatchesNothing: The rule {Rule} matches no event the program declares.</summary>
        public static void RuleMatchesNothing(this Log log, string rule,
            [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0)
        {
            if (!log.IsEnabled(Events.RuleMatchesNothing))
                return;

            LogWriter writer = log.Write(Events.RuleMatchesNothing, LogLevel.Information, 0, callerFile, callerLine);
            try
            {
                writer.Ref(rule);
            }
            finally
            {
                writer.Commit();
            }
        }

        /// <summary>The specs of the events this class declares, by name, which the catalog holds and a call site asks about.</summary>
        public static class Events
        {
            /// <summary><c>log/DrainFellBehind</c>: {Count} records were overwritten before the drain read them</summary>
            public static readonly LogEventSpec DrainFellBehind = LogEventSpec.Declare(
                "log", "DrainFellBehind", LogEventKind.Event, LogLevel.Warning,
                "{Count} records were overwritten before the drain read them",
                new LogField[]
                {
                    LogField.Value<long>("Count", LogValueKind.Int64, "long"),
                },
                0,
                Here());

            /// <summary><c>log/SinkFailed</c>: The sink {Sink} failed, and is not written to again</summary>
            public static readonly LogEventSpec SinkFailed = LogEventSpec.Declare(
                "log", "SinkFailed", LogEventKind.Event, LogLevel.Error,
                "The sink {Sink} failed, and is not written to again",
                new LogField[]
                {
                    LogField.Ref("Sink", LogValueKind.String, "string"),
                    LogField.Ref("Exception", LogValueKind.Exception, "System.Exception"),
                },
                0,
                Here());

            /// <summary><c>log/KeyTaken</c>: {Key} is declared by {Declarer} and again by {Other}, and a filter by its key finds only the first</summary>
            public static readonly LogEventSpec KeyTaken = LogEventSpec.Declare(
                "log", "KeyTaken", LogEventKind.Event, LogLevel.Warning,
                "{Key} is declared by {Declarer} and again by {Other}, and a filter by its key finds only the first",
                new LogField[]
                {
                    LogField.Ref("Key", LogValueKind.String, "string"),
                    LogField.Ref("Declarer", LogValueKind.String, "string"),
                    LogField.Ref("Other", LogValueKind.String, "string"),
                },
                0,
                Here());

            /// <summary><c>log/CategoryTaken</c>: The category {Category} is declared by {Declarer} and again by {Other}</summary>
            public static readonly LogEventSpec CategoryTaken = LogEventSpec.Declare(
                "log", "CategoryTaken", LogEventKind.Event, LogLevel.Warning,
                "The category {Category} is declared by {Declarer} and again by {Other}",
                new LogField[]
                {
                    LogField.Ref("Category", LogValueKind.String, "string"),
                    LogField.Ref("Declarer", LogValueKind.String, "string"),
                    LogField.Ref("Other", LogValueKind.String, "string"),
                },
                0,
                Here());

            /// <summary><c>log/RuleMatchesNothing</c>: The rule {Rule} matches no event the program declares</summary>
            public static readonly LogEventSpec RuleMatchesNothing = LogEventSpec.Declare(
                "log", "RuleMatchesNothing", LogEventKind.Event, LogLevel.Information,
                "The rule {Rule} matches no event the program declares",
                new LogField[]
                {
                    LogField.Ref("Rule", LogValueKind.String, "string"),
                },
                0,
                Here());

            /// <summary>Every spec of the class, in the order declared.</summary>
            internal static LogEventSpec[] All => new[] { DrainFellBehind, SinkFailed, KeyTaken, CategoryTaken, RuleMatchesNothing };

            private static LogSource Here([CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
                new LogSource("Tutan.Logging", "Tutan.Logging.SelfLog", file, line);
        }
    }
}
