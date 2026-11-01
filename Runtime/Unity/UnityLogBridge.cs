using System;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Tutan.Logging
{
    /// <summary>
    /// Brings Unity's own log into a store: every <c>Debug.Log</c>, warning, error, failed assertion
    /// and unhandled exception, from any thread, as a record of <see cref="UnityLog.Events.Logged"/>
    /// at the level <see cref="UnityLog.LevelOf"/> maps its type to. So an exception Unity reports
    /// brings the records that led to it to a sink with a backlog, and code that does not log through
    /// a <see cref="Log"/>, as other packages', reaches the same file as code that does.
    /// <code>
    /// IDisposable bridge = UnityLogBridge.Attach(store.Log);
    /// // ...
    /// bridge.Dispose();
    /// </code>
    /// </summary>
    /// <remarks>
    /// The message and the stack trace are kept by reference, so a message costs the store nothing
    /// Unity has not already allocated. What <see cref="UnityConsoleSink"/> writes is not captured, or
    /// the two would feed each other. Attached in Play Mode in the editor, the bridge detaches itself
    /// when Play Mode ends, so a composition root that does not tear down still leaves nothing behind.
    /// </remarks>
    public static class UnityLogBridge
    {
        /// <summary>Starts writing Unity's log into <paramref name="log"/>, until what it returns is disposed.</summary>
        /// <exception cref="ArgumentException"><paramref name="log"/> is <c>default</c>: it was never handed a store.</exception>
        public static IDisposable Attach(Log log)
        {
            if (!log.IsHanded)
                throw new ArgumentException("The log is default: it was never handed a store, so Unity's log would go nowhere.", nameof(log));

            var bridge = new Bridge(log);
            Application.logMessageReceivedThreaded += bridge.Receive;
#if UNITY_EDITOR
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                EditorApplication.playModeStateChanged += bridge.PlayModeChanged;
#endif
            return bridge;
        }

        private sealed class Bridge : IDisposable
        {
            // Set while this thread writes a message into the store, so a message the write itself
            // makes Unity log, as an exception would, is not written again, and again.
            [ThreadStatic]
            private static bool t_receiving;

            private readonly Log _log;
            private volatile bool _disposed;

            public Bridge(Log log) => _log = log;

            public void Receive(string message, string stackTrace, LogType type)
            {
                if (_disposed || t_receiving || UnityConsoleEcho.Writing)
                    return;

                t_receiving = true;
                try
                {
                    _log.Logged(UnityLog.LevelOf(type), message, stackTrace, type, callerFile: "", callerLine: 0);
                }
                finally
                {
                    t_receiving = false;
                }
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                _disposed = true;
                Application.logMessageReceivedThreaded -= Receive;
#if UNITY_EDITOR
                EditorApplication.playModeStateChanged -= PlayModeChanged;
#endif
            }

#if UNITY_EDITOR
            public void PlayModeChanged(PlayModeStateChange change)
            {
                if (change == PlayModeStateChange.ExitingPlayMode)
                    Dispose();
            }
#endif
        }
    }
}
