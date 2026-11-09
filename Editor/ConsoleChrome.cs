using System;
using System.Collections.Generic;
using Tutan.Functional;
using UnityEditor;
using UnityEngine;

namespace Tutan.Logging.Editor
{
    /// <summary>
    /// The console's chrome, one per editor: on each editor tick it counts the errors no window has
    /// shown and pauses the game for one that arrived, as Pause on Error asks; it clears the console
    /// as Play Mode is asked for, as Clear on Play asks; and every clear the editor makes is its own,
    /// so each open window lets go of what it held of the rows cleared.
    /// </summary>
    /// <remarks>
    /// An imperative shell, as deviation 10 of the profile says: what it holds is values,
    /// <see cref="ErrorsSeen"/> and <see cref="ShownSessions"/>, which pure functions change, in static
    /// fields each change replaces. A tick with nothing new allocates nothing.
    /// </remarks>
    [InitializeOnLoad]
    internal static class ConsoleChrome
    {
        private static ErrorsSeen s_errors = ErrorsSeen.None;
        private static ShownSessions s_shown = ShownSessions.None;
        private static UnseenErrors s_unseen = UnseenErrors.None;
        private static int s_windows;

        static ConsoleChrome()
        {
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += PlayModeChanged;
        }

        /// <summary>Raised after the editor cleared the console, with the process's sessions as they were before.</summary>
        public static event Action<IReadOnlyList<LogConsoleSession>>? Cleared;

        /// <summary>Raised when the count of unseen errors changed.</summary>
        public static event Action? UnseenChanged;

        /// <summary>The errors no window has shown, as the last tick counted them.</summary>
        public static UnseenErrors Unseen => s_unseen;

        /// <summary>A number for a window that opens, which it tells the session it shows by.</summary>
        public static WindowNumber NumberWindow() => new WindowNumber(++s_windows);

        /// <summary>
        /// Window <paramref name="window"/> shows <paramref name="session"/>, or, for none, nothing: it is
        /// hidden, closed, or shows no session. Its errors are seen on every tick while it does.
        /// </summary>
        public static void Show(WindowNumber window, Optional<LogSessionId> session) => s_shown = s_shown.Showing(window, session);

        /// <summary>
        /// Clears the console: the live sessions are emptied and the ended ones let go, as
        /// <see cref="LogConsole.Clear"/> does. Then each open window is told, with the sessions as they
        /// were before, and the errors are counted again.
        /// </summary>
        public static void Clear()
        {
            IReadOnlyList<LogConsoleSession> before = LogConsole.Sessions;
            LogConsole.Clear();
            Cleared?.Invoke(before);
            Tick();
        }

        /// <summary>Shows the console at the first error no window has shown, when there is one.</summary>
        public static void ShowFirstUnseen()
        {
            if (s_unseen.First.HasValue(out UnseenError first))
                LogConsoleWindow.ShowError(first);
        }

        private static void Tick()
        {
            ErrorsTicked ticked = s_errors.Tick(LogConsole.Sessions, s_shown);
            s_errors = ticked.Next;
            if (ChromeRules.Pauses(ticked.Arrived, ConsoleSettings.Personal, EditorApplication.isPlaying, EditorApplication.isPaused))
                Debug.Break();

            UnseenErrors was = s_unseen;
            s_unseen = ticked.Unseen;
            if (ticked.Unseen.Count != was.Count)
                UnseenChanged?.Invoke();
        }

        private static void PlayModeChanged(PlayModeStateChange change)
        {
            if (ChromeRules.Clears(change, ConsoleSettings.Personal))
                Clear();
        }
    }
}
