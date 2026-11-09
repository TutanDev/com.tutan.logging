using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using Tutan.Functional;
using UnityEditor;

namespace Tutan.Logging.Editor
{
    /// <summary>
    /// Where the chrome stands with one session's errors: those it holds from <see cref="UnseenFrom"/>
    /// on are unseen, and <see cref="ArrivedThrough"/> is the sequence of the newest it saw arrive.
    /// </summary>
    /// <param name="Session">The session.</param>
    /// <param name="UnseenFrom">The sequence the unseen errors start at: where the session was when a window last showed it with errors in it.</param>
    /// <param name="ArrivedThrough">The sequence of the newest error the chrome saw arrive, or 0 before it saw any.</param>
    internal readonly record struct SessionErrors(LogSessionId Session, long UnseenFrom, long ArrivedThrough)
    {
        /// <summary>A session the chrome has seen nothing of: every error it holds is unseen, and arrived since the chrome last looked.</summary>
        public static SessionErrors Of(LogSessionId session) => new SessionErrors(session, 0, 0);

        /// <summary>
        /// What the errors <paramref name="held"/>, read from <see cref="UnseenFrom"/> on, say: whether one
        /// arrived, which counts only in a live session, and what is unseen, which is nothing while a
        /// window shows the session, <paramref name="shown"/>. It moves only when an error arrived or
        /// was seen, so a session that only logs other records changes nothing.
        /// </summary>
        public SessionRead Read(LogErrorsHeld held, bool live, bool shown)
        {
            bool arrived = held.Last.HasValue(out long newest) && newest > ArrivedThrough;
            long through = arrived ? newest : ArrivedThrough;
            if (!shown)
            {
                UnseenErrors unseen = held.First.HasValue(out long first) ? UnseenErrors.Of(held.Count, new UnseenError(Session, first)) : UnseenErrors.None;
                return new SessionRead(this with { ArrivedThrough = through }, live && arrived, unseen);
            }

            // Every error it holds is on screen, so what comes after is what is unseen.
            long from = held.Count > 0 ? held.Next : UnseenFrom;
            return new SessionRead(new SessionErrors(Session, from, through), live && arrived, UnseenErrors.None);
        }
    }

    /// <summary>What a tick read of one session's errors.</summary>
    /// <param name="Now">Where the chrome stands with the session now.</param>
    /// <param name="Arrived">Whether an error arrived in it since the last tick, while it is live.</param>
    /// <param name="Unseen">Its errors no window has shown.</param>
    internal readonly record struct SessionRead(SessionErrors Now, bool Arrived, UnseenErrors Unseen);

    /// <summary>A window and the session it shows while it is visible.</summary>
    /// <param name="Window">The window, by the number it was given as it opened.</param>
    /// <param name="Session">The session it shows.</param>
    internal readonly record struct WindowShows(WindowNumber Window, LogSessionId Session);

    /// <summary>A console window, by the number the chrome gave it, one per window the editor opened since its domain loaded.</summary>
    /// <param name="Value">The number.</param>
    internal readonly record struct WindowNumber(int Value);

    /// <summary>The session each visible window shows: a window hidden, closed, or showing none is not in it.</summary>
    internal sealed record ShownSessions
    {
        private readonly WindowShows[] _windows;

        private ShownSessions(WindowShows[] windows) => _windows = windows;

        /// <summary>No window shows a session.</summary>
        public static ShownSessions None { get; } = new ShownSessions(Array.Empty<WindowShows>());

        /// <summary>
        /// <paramref name="window"/> shows <paramref name="session"/>, or, for none, shows nothing: it is
        /// hidden, closed, or shows no session. This one when that is so already.
        /// </summary>
        public ShownSessions Showing(WindowNumber window, Optional<LogSessionId> session)
        {
            int at = Find(window);
            if (!session.HasValue(out LogSessionId id))
            {
                if (at < 0)
                    return this;

                var without = new WindowShows[_windows.Length - 1];
                Array.Copy(_windows, 0, without, 0, at);
                Array.Copy(_windows, at + 1, without, at, _windows.Length - at - 1);
                return new ShownSessions(without);
            }

            var now = new WindowShows(window, id);
            if (at >= 0 && _windows[at] == now)
                return this;

            WindowShows[] grown = at >= 0 ? (WindowShows[])_windows.Clone() : new WindowShows[_windows.Length + 1];
            if (at < 0)
                Array.Copy(_windows, grown, _windows.Length);
            grown[at >= 0 ? at : _windows.Length] = now;
            return new ShownSessions(grown);
        }

        /// <summary>Where <paramref name="window"/> is among the windows that show a session: -1 when it is not.</summary>
        private int Find(WindowNumber window)
        {
            for (int i = 0; i < _windows.Length; i++)
            {
                if (_windows[i].Window == window)
                    return i;
            }

            return -1;
        }

        /// <summary>Whether a visible window shows <paramref name="session"/>.</summary>
        public bool Shows(LogSessionId session)
        {
            for (int i = 0; i < _windows.Length; i++)
            {
                if (_windows[i].Session == session)
                    return true;
            }

            return false;
        }

        /// <summary>Equal when the same windows show the same sessions, in the same order.</summary>
        public bool Equals(ShownSessions? other) => other is not null && _windows.AsSpan().SequenceEqual(other._windows);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (WindowShows shows in _windows)
                hash.Add(shows);
            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// Where the chrome stands with the errors of every session a console sink keeps, live or ended:
    /// what of each is unseen, and the newest it saw arrive. A file's session is not in it, as its
    /// errors arrived in no run the console watched.
    /// </summary>
    internal sealed record ErrorsSeen
    {
        private readonly SessionErrors[] _sessions;

        private ErrorsSeen(SessionErrors[] sessions)
        {
            _sessions = sessions;
            Sessions = Array.AsReadOnly(sessions);
        }

        /// <summary>The chrome has seen no session.</summary>
        public static ErrorsSeen None { get; } = new ErrorsSeen(Array.Empty<SessionErrors>());

        /// <summary>Each session it counts the errors of, in the order the process lists them.</summary>
        public ReadOnlyCollection<SessionErrors> Sessions { get; }

        /// <summary>
        /// Reads the errors of each of <paramref name="sessions"/> a console sink keeps: marks seen what a
        /// session <paramref name="shown"/> holds, says whether an error arrived in a live session, and
        /// counts what is unseen, with the first of it in the oldest session that has one. Its
        /// <see cref="ErrorsTicked.Next"/> is this one when nothing changed, and then the tick allocates nothing.
        /// </summary>
        public ErrorsTicked Tick(IReadOnlyList<LogConsoleSession> sessions, ShownSessions shown)
        {
            if (sessions is null)
                throw new ArgumentNullException(nameof(sessions));
            if (shown is null)
                throw new ArgumentNullException(nameof(shown));

            // Made only once something changed, with what came before it as it was.
            SessionErrors[]? next = null;
            bool arrived = false;
            long count = 0;
            Optional<UnseenError> first = F.None;
            int kept = 0;
            for (int i = 0; i < sessions.Count; i++)
            {
                LogConsoleSession session = sessions[i];

                // Live when it keeps errors as they arrive, ended when it holds what it kept, and none for a file's, whose errors never arrived.
                Optional<bool> watched = session.Match<Optional<bool>>(static _ => true, static _ => false, static _ => F.None);
                if (!watched.HasValue(out bool live))
                    continue;

                LogSessionId id = session.Id;
                bool samePlace = kept < _sessions.Length && _sessions[kept].Session == id;
                SessionErrors was = samePlace ? _sessions[kept] : Prior(id);
                SessionRead read = was.Read(session.Records.ErrorsFrom(was.UnseenFrom), live, shown.Shows(id));
                if (next is null && (!samePlace || read.Now != was))
                {
                    next = new SessionErrors[sessions.Count];
                    Array.Copy(_sessions, next, kept);
                }

                if (next is not null)
                    next[kept] = read.Now;

                arrived |= read.Arrived;
                if (read.Unseen.First.HasValue(out UnseenError error))
                {
                    count += read.Unseen.Count;
                    if (!first.HasValue(out UnseenError oldest) || id < oldest.Session)
                        first = error;
                }

                kept++;
            }

            if (next is null && kept != _sessions.Length)
            {
                // Only sessions let go since: those before them are as they were.
                next = new SessionErrors[kept];
                Array.Copy(_sessions, next, kept);
            }

            ErrorsSeen now = next is null ? this : new ErrorsSeen(Fit(next, kept));
            return new ErrorsTicked(now, arrived, first.HasValue(out UnseenError firstUnseen) ? UnseenErrors.Of(count, firstUnseen) : UnseenErrors.None);
        }

        /// <summary>Equal when it stands where the other does with the same sessions, in the same order.</summary>
        public bool Equals(ErrorsSeen? other) => other is not null && _sessions.AsSpan().SequenceEqual(other._sessions);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (SessionErrors session in _sessions)
                hash.Add(session);
            return hash.ToHashCode();
        }

        /// <summary>Where it stood with <paramref name="session"/>, wherever it is in the list, or nothing seen of it when it is new.</summary>
        private SessionErrors Prior(LogSessionId session)
        {
            for (int i = 0; i < _sessions.Length; i++)
            {
                if (_sessions[i].Session == session)
                    return _sessions[i];
            }

            return SessionErrors.Of(session);
        }

        private static SessionErrors[] Fit(SessionErrors[] sessions, int count)
        {
            if (sessions.Length == count)
                return sessions;

            var fit = new SessionErrors[count];
            Array.Copy(sessions, fit, count);
            return fit;
        }
    }

    /// <summary>What a tick of the errors found.</summary>
    /// <param name="Next">Where the chrome stands now.</param>
    /// <param name="Arrived">Whether an error arrived in a live session since the last tick.</param>
    /// <param name="Unseen">The errors no window has shown.</param>
    internal readonly record struct ErrorsTicked(ErrorsSeen Next, bool Arrived, UnseenErrors Unseen);

    /// <summary>
    /// The errors no window has shown: none, or how many, and the first, in the oldest session that
    /// has one, so a count is never without an error to show.
    /// </summary>
    internal readonly record struct UnseenErrors
    {
        private UnseenErrors(long count, Optional<UnseenError> first)
        {
            Count = count;
            First = first;
        }

        /// <summary>No error is unseen.</summary>
        public static UnseenErrors None => new UnseenErrors(0, F.None);

        /// <summary>How many are unseen.</summary>
        public long Count { get; }

        /// <summary>The first unseen: none when none is.</summary>
        public Optional<UnseenError> First { get; }

        /// <summary><paramref name="count"/> errors unseen, of which <paramref name="first"/> is the first.</summary>
        public static UnseenErrors Of(long count, UnseenError first) =>
            count >= 1
                ? new UnseenErrors(count, first)
                : throw new ArgumentOutOfRangeException(nameof(count),
                    $"At least one error is unseen when one is the first, and {count.ToString(CultureInfo.InvariantCulture)} was given.");
    }

    /// <summary>An error to show: its session, and the sequence of its record.</summary>
    /// <param name="Session">The session that holds it.</param>
    /// <param name="Record">The sequence of its record.</param>
    internal readonly record struct UnseenError(LogSessionId Session, long Record);

    /// <summary>When the chrome acts on the editor.</summary>
    internal static class ChromeRules
    {
        /// <summary>Whether the game pauses: an error <paramref name="arrived"/>, Pause on Error is set, and the game plays and is not paused.</summary>
        public static bool Pauses(bool arrived, PersonalSettings personal, bool playing, bool paused)
        {
            if (personal is null)
                throw new ArgumentNullException(nameof(personal));

            return arrived && personal.PauseOnError && playing && !paused;
        }

        /// <summary>Whether the console is cleared: Play Mode is asked for, <paramref name="change"/>, and Clear on Play is set.</summary>
        public static bool Clears(PlayModeStateChange change, PersonalSettings personal)
        {
            if (personal is null)
                throw new ArgumentNullException(nameof(personal));

            return change == PlayModeStateChange.ExitingEditMode && personal.ClearOnPlay;
        }
    }

    /// <summary>How the chrome says what is unseen.</summary>
    internal static class UnseenText
    {
        /// <summary>The console's name, with the count when any is unseen: <c>Log Console (3)</c>.</summary>
        public const string Name = "Log Console";

        /// <summary>The window's title: <see cref="Name"/>, with the count of unseen errors when there are any.</summary>
        public static string Title(long count) => count > 0 ? $"{Name} ({CountText.Compact(count)})" : Name;

        /// <summary>The badge's text: the count, compact.</summary>
        public static string Badge(long count) => CountText.Compact(count);

        /// <summary>What the badge says when the pointer is on it.</summary>
        public static string BadgeTip(long count) => count == 1
            ? "An error the log console has not shown. Press to show it."
            : $"{count.ToString("N0", CultureInfo.InvariantCulture)} errors the log console has not shown. Press to show the first.";
    }
}
