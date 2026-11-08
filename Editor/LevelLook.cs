using System;
using System.Collections.ObjectModel;
using System.Globalization;

namespace Tutan.Logging.Editor
{
    /// <summary>How the console shows a level: the levels a badge is drawn for, the stylesheet's class of each, and its badge.</summary>
    internal static class LevelLook
    {
        /// <summary>The levels a record can have, from the least, in the order the badges are drawn.</summary>
        public static ReadOnlyCollection<LogLevel> Shown { get; } = Array.AsReadOnly(new[]
        {
            LogLevel.Trace, LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error, LogLevel.Critical,
        });

        /// <summary>The stylesheet's class of <paramref name="level"/>, as <c>level-warning</c>.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not a level a record can have.</exception>
        public static string Class(LogLevel level) => level switch
        {
            LogLevel.Trace => "level-trace",
            LogLevel.Debug => "level-debug",
            LogLevel.Information => "level-information",
            LogLevel.Warning => "level-warning",
            LogLevel.Error => "level-error",
            LogLevel.Critical => "level-critical",
            _ => throw NotALevel(level),
        };

        /// <summary>The badge of <paramref name="level"/>: its letter, and how many records match but for the level, as <c>W 12</c>.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not a level a record can have.</exception>
        public static string Badge(LogLevel level, long count) => $"{Letter(level)} {CountText.Compact(count)}";

        /// <summary>What the badge of <paramref name="level"/> says when pointed at: the count whole, and what a click does.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is not a level a record can have.</exception>
        public static string BadgeTip(LogLevel level, long count)
        {
            if (!Shown.Contains(level))
                throw NotALevel(level);

            string records = count == 1 ? "1 record matches" : $"{count.ToString("N0", CultureInfo.InvariantCulture)} records match";
            return $"{level}: {records} but for the level. Click to show only {level}, and again to show every level.";
        }

        private static string Letter(LogLevel level) => level switch
        {
            LogLevel.Trace => "T",
            LogLevel.Debug => "D",
            LogLevel.Information => "I",
            LogLevel.Warning => "W",
            LogLevel.Error => "E",
            LogLevel.Critical => "C",
            _ => throw NotALevel(level),
        };

        private static ArgumentOutOfRangeException NotALevel(LogLevel level) =>
            new ArgumentOutOfRangeException(nameof(level), $"{level} is not a level a record can have.");
    }
}
