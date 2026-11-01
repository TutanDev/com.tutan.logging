#if UNITY_EDITOR
using System;
using System.Reflection;
using UnityEngine;

namespace Tutan.Logging
{
    /// <summary>
    /// Writes a Console row that opens a given file and line when it is double-clicked, as a
    /// <c>Debug.Log</c> written at that line does. Unity has no public way to say where a row opens,
    /// so this binds the internal <c>Debug.LogInfo</c>, <c>LogWarning</c> and <c>LogError</c> overloads
    /// that take a file, a line and a column, which Unity uses for messages of its own about a file.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="UnityConsoleSink"/>, and Editor-only, so that a Unity upgrade that
    /// renames or removes them costs only the double-click: the sink falls back to a row with the call
    /// site as a link, for good, the first time binding or a call fails.
    /// </remarks>
    internal sealed class ConsoleRowLocation
    {
        private readonly LogAt _info;
        private readonly LogAt _warning;
        private readonly LogAt _error;
        private bool _broken;

        private ConsoleRowLocation(LogAt info, LogAt warning, LogAt error)
        {
            _info = info;
            _warning = warning;
            _error = error;
        }

        private delegate void LogAt(string message, string file, int line, int column);

        /// <summary>The adapter, or null when this version of Unity does not have all three overloads.</summary>
        public static ConsoleRowLocation? TryBind()
        {
            LogAt? info = Bind("LogInfo");
            LogAt? warning = Bind("LogWarning");
            LogAt? error = Bind("LogError");
            return info is null || warning is null || error is null ? null : new ConsoleRowLocation(info, warning, error);
        }

        /// <summary>
        /// Writes <paramref name="message"/> as a row of <paramref name="type"/> that opens
        /// <paramref name="file"/> at <paramref name="line"/>, or says it could not, and will not try
        /// again.
        /// </summary>
        public bool TryWrite(LogType type, string message, string file, int line)
        {
            if (_broken)
                return false;

            LogAt write = type switch
            {
                LogType.Warning => _warning,
                LogType.Error or LogType.Assert or LogType.Exception => _error,
                _ => _info,
            };

            try
            {
                write(message, file, line, 0);
                return true;
            }
            catch (Exception)
            {
                // An internal method that changed what it does: the link in the message still opens the call site.
                _broken = true;
                return false;
            }
        }

        private static LogAt? Bind(string name)
        {
            try
            {
                MethodInfo? method = typeof(Debug).GetMethod(
                    name,
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                    null,
                    new[] { typeof(string), typeof(string), typeof(int), typeof(int) },
                    null);
                return method is null ? null : Delegate.CreateDelegate(typeof(LogAt), method, throwOnBindFailure: false) as LogAt;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
#endif
