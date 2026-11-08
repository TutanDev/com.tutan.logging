using System;
using System.Globalization;

namespace Tutan.Logging.Editor
{
    /// <summary>What the console calls a session.</summary>
    internal static class SessionLabel
    {
        /// <summary>
        /// What the console calls <paramref name="session"/>: its program and whether it is live, or
        /// when it ended, at the offset its end was told at, or its file's name, after its number, as
        /// <c>2. Sample Game (live)</c>.
        /// </summary>
        public static string Of(LogConsoleSession session)
        {
            if (session is null)
                throw new ArgumentNullException(nameof(session));

            string what = session.Match(
                static live => $"{Program(live.Records)} (live)",
                static ended => $"{Program(ended.Records)} (ended {ended.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture)})",
                static file => UnityPaths.FileName(file.Path.Replace('\\', '/')));
            return $"{session.Id}. {what}";
        }

        private static string Program(LogRecords records) => records.Program.Length > 0 ? records.Program : "A program";
    }
}
