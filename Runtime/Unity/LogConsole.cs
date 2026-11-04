using System.Collections.Generic;
using Tutan.Functional;

namespace Tutan.Logging
{
    /// <summary>
    /// The sessions the log console shows, of the whole process: each <see cref="LogConsoleSink"/>
    /// a drain opens starts one, and each file opened adds one. It is to the log console what Unity's
    /// own list of messages is to Unity's Console, a display the editor has one of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is not a logger: nothing is written through it, and nothing reaches it unless a
    /// composition root adds a console sink to its drain. Code that logs never sees it.
    /// </para>
    /// <para>
    /// Outside the editor a console sink starts no session, since no window can read one, so a root
    /// adds the sink without an <c>#if</c>. A domain reload lets every session go with the domain.
    /// </para>
    /// </remarks>
    public static class LogConsole
    {
#if UNITY_EDITOR
        private const bool InEditor = true;
#else
        private const bool InEditor = false;
#endif

        /// <summary>
        /// The sessions, the oldest first: a new list each time one starts, ends, is opened, is closed
        /// or is cleared away, so a reader's list never changes under it. A session's records change as
        /// its sink keeps them.
        /// </summary>
        public static IReadOnlyList<LogConsoleSession> Sessions => Process.Sessions;

        /// <summary>The process's sessions, which console sinks start live ones in only in the editor.</summary>
        internal static ConsoleSessions Process { get; } = new ConsoleSessions(live: InEditor);

        /// <summary>The session <paramref name="id"/> as it is now, while it is one of them.</summary>
        public static Optional<LogConsoleSession> Find(LogSessionId id) => Process.Find(id);

        /// <summary>The session of a file <see cref="LogFile.Read(string)"/> read, added to them; the one it is already, when it was opened before.</summary>
        public static LogConsoleSession.File Open(LogSessionFile file) => Process.Open(file);

        /// <summary>Lets the ended session go; does nothing when it is gone already. A live one ends when its drain lets its sink go.</summary>
        public static void Close(LogConsoleSession.Ended session) => Process.Close(session);

        /// <summary>Lets the file's session go; does nothing when it is gone already.</summary>
        public static void Close(LogConsoleSession.File session) => Process.Close(session);

        /// <summary>
        /// Clears the console, as its Clear does: lets ended sessions go, and empties live ones, which
        /// keep what they are handed after. A file's session stays until it is closed.
        /// </summary>
        public static void Clear() => Process.Clear();
    }
}
