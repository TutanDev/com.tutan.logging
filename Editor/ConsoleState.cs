using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tutan.Functional;

namespace Tutan.Logging.Editor
{
    /// <summary>
    /// What the log console shows and how, as the developer asked for it: the session, with what of it
    /// is folded and selected, the query line, and whether it follows the newest. Each input is a method
    /// that gives the next state, so the window draws a state and runs the effects at its edge, and a
    /// test drives the inputs without a window.
    /// </summary>
    /// <remarks>
    /// The rows are not part of it: they are a <see cref="LogView"/> of the session, which the window
    /// keeps up to date and sets the filter and folds of from the state, leaving out after it what the
    /// project always leaves out.
    /// </remarks>
    internal sealed record ConsoleState
    {
        private ConsoleState(Optional<SessionShown> shown, Optional<LogSessionId> newestSeen, QueryLine query, bool following)
        {
            Shown = shown;
            NewestSeen = newestSeen;
            Query = query;
            Following = following;
        }

        /// <summary>A window just opened: no session yet, an empty query, following the newest.</summary>
        public static ConsoleState Start { get; } = new ConsoleState(F.None, F.None, QueryLine.Empty, following: true);

        /// <summary>The session shown, with what of it is folded and selected; none while the process has none.</summary>
        public Optional<SessionShown> Shown { get; private init; }

        /// <summary>The newest session it has seen, so a session that starts is told from one it saw: none before it saw any.</summary>
        public Optional<LogSessionId> NewestSeen { get; private init; }

        /// <summary>The query line, whose filter the rows are of.</summary>
        public QueryLine Query { get; private init; }

        /// <summary>Whether the newest row stays in sight, and a live session that starts is chosen.</summary>
        public bool Following { get; private init; }

        /// <summary>
        /// The line as it was before a saved filter was pressed, while the line is a saved filter's: pressing
        /// that filter again brings it back. None once the line is changed otherwise, and after a domain
        /// reload, which does not keep it.
        /// </summary>
        public Optional<QueryLine> BeforeSaved { get; private init; }

        /// <summary>The sequence of the record of the session shown to bring into sight once its rows have it: none while none is.</summary>
        public Optional<long> Revealing => Shown.Bind(static shown => shown.Revealing);

        /// <summary>The id of the session shown; none while the process has none.</summary>
        public Optional<LogSessionId> Session => Shown.Map(static shown => shown.Id);

        /// <summary>The scopes of the session shown whose rows hide what is under them: none without a session.</summary>
        public ScopeSet Folded => Shown.Map(static shown => shown.Folded).Or(ScopeSet.Empty);

        /// <summary>The rows selected of the session shown: none without a session.</summary>
        public Selection Selection => Shown.Map(static shown => shown.Selection).Or(Selection.None);

        /// <summary>The filter the rows are of: the query line's.</summary>
        public LogFilter Filter => Query.Filter;

        /// <summary>
        /// A window's state read back from what it saved, as after a domain reload: the filter its rows
        /// were of, then the line as it was, which keeps that filter when it does not read. A filter
        /// that does not read, as one an older version saved might not, leaves every record kept.
        /// </summary>
        /// <param name="query">The query line's text.</param>
        /// <param name="filter">The filter the rows were of, as its <see cref="LogFilter.ToString"/> wrote it.</param>
        /// <param name="following">Whether it followed the newest.</param>
        public static ConsoleState Restored(string query, string filter, bool following)
        {
            if (query is null)
                throw new ArgumentNullException(nameof(query));
            if (filter is null)
                throw new ArgumentNullException(nameof(filter));

            ConsoleState state = Start.Type(filter).Type(query);
            return following ? state : state.StopFollowing();
        }

        /// <summary>The query line as typed: a filter when it reads, and the filter it had and why it does not when it does not.</summary>
        public ConsoleState Type(string text) => this with { Query = QueryLine.Of(text, Filter), BeforeSaved = F.None };

        /// <summary>
        /// A saved filter's button pressed: while it is on, the line goes back to what it was before a
        /// saved filter was pressed, or to the empty line; else the line is the saved filter's, and the
        /// line before is remembered, the first line when another saved filter was on.
        /// </summary>
        public ConsoleState PressSaved(SavedFilter saved)
        {
            if (saved is null)
                throw new ArgumentNullException(nameof(saved));

            if (saved.IsOn(Filter))
                return this with { Query = BeforeSaved.Or(QueryLine.Empty), BeforeSaved = F.None };

            Optional<QueryLine> before = BeforeSaved.IsSome ? BeforeSaved : Query;
            return this with { Query = QueryLine.Written(saved.Filter), BeforeSaved = before };
        }

        /// <summary>
        /// An error to show, as the toolbar's badge asks: its session is chosen, following stops so the
        /// newest row does not take the rows away from it, and its record is brought into sight once the
        /// rows have it, as <see cref="RevealIn"/> says.
        /// </summary>
        public ConsoleState Reveal(UnseenError error)
        {
            if (error.Session == default)
                throw new ArgumentException("The default id is no session, and an error to show is of one.", nameof(error));

            SessionShown shown = Shown.HasValue(out SessionShown current) && current.Id == error.Session ? current : SessionShown.Of(error.Session);
            return this with { Shown = shown.Reveal(error.Record), Following = false };
        }

        /// <summary>
        /// What bringing the record <see cref="Revealing"/> into sight asks of <paramref name="rows"/>, the
        /// view of the session shown, as it is now: to wait while there is no such record or the view
        /// still scans; to say it is gone, when there are no rows, as the process let the session go, or
        /// the record is older than every record they are of, let go past the cap or by a clear; to select
        /// the row that holds it, for the window to scroll to; or, when the scan caught up and no row holds
        /// it, as a query, a fold or what is always left out hides it, to say so.
        /// </summary>
        public RevealStep RevealIn(Optional<LogView> rows)
        {
            if (!Shown.HasValue(out SessionShown shown) || !shown.Revealing.HasValue(out long record))
                return RevealStep.Waiting.Instance;

            if (!rows.HasValue(out LogView view) || record < view.Source.First)
                return new RevealStep.Gone(this with { Shown = shown.Revealed(shown.Selection) });

            if (view.IsScanning)
                return RevealStep.Waiting.Instance;

            if (!view.Find(record).HasValue(out int index))
                return new RevealStep.Hidden(this with { Shown = shown.Revealed(shown.Selection) });

            // The row's own sequence, as a repeat's row holds the records of its run.
            long row = view[index].Sequence;
            return new RevealStep.Found(index, this with { Shown = shown.Revealed(Selection.Of(new[] { row }, row)) });
        }

        /// <summary>
        /// What bringing the record <see cref="Revealing"/> into sight comes to among <paramref name="sessions"/>,
        /// the process's sessions as they are now: to say it is gone, as <see cref="RevealIn"/> does with no
        /// rows, when the session shown is not among them; else to wait, for <see cref="RevealIn"/> to read
        /// the rows. Asked before <see cref="SessionsChanged"/>, which chooses another session and so would
        /// end it saying nothing.
        /// </summary>
        public RevealStep RevealAmong(IReadOnlyList<LogConsoleSession> sessions)
        {
            if (sessions is null)
                throw new ArgumentNullException(nameof(sessions));

            if (!Shown.HasValue(out SessionShown shown) || shown.Revealing.IsNone)
                return RevealStep.Waiting.Instance;

            for (int i = 0; i < sessions.Count; i++)
            {
                if (sessions[i].Id == shown.Id)
                    return RevealStep.Waiting.Instance;
            }

            return RevealIn(F.None);
        }

        /// <summary>A level's badge pressed: only that level is shown, or every level when only it was.</summary>
        public ConsoleState PressBadge(LogLevel level)
        {
            if (!LevelLook.Shown.Contains(level))
                throw new ArgumentOutOfRangeException(nameof(level), $"A badge is of a level a record has, from Trace to Critical, and {level} was given.");

            LogLevelRange only = LogLevelRange.Only(level);
            return Written(Filter with { Levels = Filter.Levels == only ? LogLevelRange.All : only });
        }

        /// <summary>A category of <paramref name="tree"/> pressed: shown or hidden with everything under it, shown alone, or every one shown.</summary>
        public ConsoleState PressCategory(CategoryTree tree, CategoryNode node, CategoryPress press)
        {
            if (tree is null)
                throw new ArgumentNullException(nameof(tree));

            return Written(tree.Press(Filter, node, press));
        }

        /// <summary>A scope's press in the details: its records, and those under it, are kept, in place of the scope kept before.</summary>
        public ConsoleState FilterToScope(long scope)
        {
            var term = new LogFilterTerm.Scope(scope);
            LogFilterTerm[] keep = Filter.Keep.Where(static kept => kept is not LogFilterTerm.Scope).Append(term).ToArray();
            return Written(Filter with { Keep = keep });
        }

        /// <summary>A session chosen: its rows are read afresh, with nothing folded or selected, and the query is kept.</summary>
        public ConsoleState Choose(LogSessionId session)
        {
            if (Session.HasValue(out LogSessionId shown) && shown == session)
                return this;

            return this with { Shown = SessionShown.Of(session) };
        }

        /// <summary>
        /// The process's sessions as they are now: a live session that started since it last looked is
        /// chosen while it follows, and when the session shown is gone, or none is, the newest live one
        /// is chosen, or the newest of all, or none.
        /// </summary>
        public ConsoleState SessionsChanged(IReadOnlyList<LogConsoleSession> sessions)
        {
            if (sessions is null)
                throw new ArgumentNullException(nameof(sessions));

            Optional<LogSessionId> newest = Newest(sessions);
            Optional<LogSessionId> newestLive = Newest(sessions.Where(static session => session.Match(static _ => true, static _ => false, static _ => false)));
            ConsoleState seen = newest.HasValue(out LogSessionId latest) && Unseen(latest) ? this with { NewestSeen = latest } : this;

            // A live session newer than every one it saw started since it last looked.
            if (Following && newestLive.HasValue(out LogSessionId started) && Unseen(started))
                return seen.Choose(started);

            if (Session.HasValue(out LogSessionId shown) && sessions.Any(session => session.Id == shown))
                return seen;

            Optional<LogSessionId> chosen = newestLive.IsSome ? newestLive : newest;
            return chosen.HasValue(out LogSessionId choose)
                ? seen.Choose(choose)
                : seen.Shown.IsSome ? seen with { Shown = F.None } : seen;
        }

        /// <summary>Rows of the session shown selected: nothing changes without a session.</summary>
        public ConsoleState Select(Selection selection)
        {
            if (selection is null)
                throw new ArgumentNullException(nameof(selection));

            return Shown.HasValue(out SessionShown shown) ? this with { Shown = shown.Select(selection) } : this;
        }

        /// <summary>A scope's row of the session shown folded, hiding what is under it, or unfolded: nothing changes without a session.</summary>
        public ConsoleState ToggleFold(long scope)
        {
            if (scope < 1)
                throw new ArgumentOutOfRangeException(nameof(scope), $"A scope is the sequence of the record that opened it, which counts from 1, and {scope.ToString(CultureInfo.InvariantCulture)} was given.");

            return Shown.HasValue(out SessionShown shown) ? this with { Shown = shown.ToggleFold(scope) } : this;
        }

        /// <summary>Follow pressed on: the newest row is in sight, and a live session that starts is chosen; a record being brought into sight is no more.</summary>
        public ConsoleState Follow()
        {
            ConsoleState revealed = Shown.HasValue(out SessionShown shown) && shown.Revealing.IsSome
                ? this with { Shown = shown.Revealed(shown.Selection) }
                : this;
            return revealed.Following ? revealed : revealed with { Following = true };
        }

        /// <summary>Follow pressed off: the rows stay where they are, and so does the session.</summary>
        public ConsoleState StopFollowing() => Following ? this with { Following = false } : this;

        /// <summary>The developer scrolled: up, which stops following, or to the newest row, which follows again.</summary>
        public ConsoleState Scrolled(bool toNewest) => toNewest ? Follow() : StopFollowing();

        /// <summary>
        /// The console cleared while it showed <paramref name="cleared"/>, as the process had it before:
        /// a live session's records are emptied, so what was folded and selected of it goes with them,
        /// and the session stays. A file's session keeps all it had, and an ended one is let go, which
        /// the sessions changing says; nothing changes for a session that is not the one shown.
        /// </summary>
        public ConsoleState Cleared(LogConsoleSession cleared)
        {
            if (cleared is null)
                throw new ArgumentNullException(nameof(cleared));

            if (!Shown.HasValue(out SessionShown shown) || shown.Id != cleared.Id)
                return this;

            bool emptied = cleared.Match(static _ => true, static _ => false, static _ => false);
            return emptied ? this with { Shown = shown.Cleared() } : this;
        }

        /// <summary>
        /// The console cleared, as <see cref="Cleared(LogConsoleSession)"/> says, given <paramref name="before"/>,
        /// the sessions as they were before the clear: this one when the session shown was not among them.
        /// </summary>
        public ConsoleState Cleared(IReadOnlyList<LogConsoleSession> before)
        {
            if (before is null)
                throw new ArgumentNullException(nameof(before));

            if (!Session.HasValue(out LogSessionId shown))
                return this;

            for (int i = 0; i < before.Count; i++)
            {
                if (before[i].Id == shown)
                    return Cleared(before[i]);
            }

            return this;
        }

        /// <summary>Whether <paramref name="id"/> is newer than every session it has seen.</summary>
        private bool Unseen(LogSessionId id) => !NewestSeen.HasValue(out LogSessionId seen) || id > seen;

        private ConsoleState Written(LogFilter filter) => this with { Query = QueryLine.Written(filter), BeforeSaved = F.None };

        private static Optional<LogSessionId> Newest(IEnumerable<LogConsoleSession> sessions) =>
            sessions.Aggregate(default(Optional<LogSessionId>), static (newest, session) =>
                newest.HasValue(out LogSessionId id) && id > session.Id ? newest : F.Some(session.Id));
    }

    /// <summary>
    /// The session the console shows, what of it the developer folded and selected, and the record to
    /// bring into sight, if one is: these are of its rows, so choosing another session starts them
    /// afresh, and a state with no session has none.
    /// </summary>
    internal sealed record SessionShown
    {
        private SessionShown(LogSessionId id, ScopeSet folded, Selection selection)
        {
            Id = id;
            Folded = folded;
            Selection = selection;
        }

        /// <summary>The session's id.</summary>
        public LogSessionId Id { get; }

        /// <summary>The scopes whose rows hide what is under them.</summary>
        public ScopeSet Folded { get; private init; }

        /// <summary>The rows selected.</summary>
        public Selection Selection { get; private init; }

        /// <summary>The sequence of the record to bring into sight once the rows have it: none while none is.</summary>
        public Optional<long> Revealing { get; private init; }

        /// <summary><paramref name="id"/> shown afresh: nothing folded, nothing selected.</summary>
        /// <exception cref="ArgumentException"><paramref name="id"/> is the default, which is no session.</exception>
        public static SessionShown Of(LogSessionId id)
        {
            if (id == default)
                throw new ArgumentException("The default id is no session, and a session shown is one.", nameof(id));

            return new SessionShown(id, ScopeSet.Empty, Selection.None);
        }

        /// <summary><paramref name="scope"/>'s row folded, or unfolded when it was.</summary>
        public SessionShown ToggleFold(long scope) =>
            this with { Folded = Folded.Contains(scope) ? Folded.Without(scope) : Folded.With(scope) };

        /// <summary><paramref name="selection"/> selected in place of what was, which ends bringing a record into sight: the developer chose what to see.</summary>
        public SessionShown Select(Selection selection) => Revealed(selection);

        /// <summary><paramref name="record"/> to bring into sight once the rows have it, in place of one that was.</summary>
        public SessionShown Reveal(long record)
        {
            if (record < 1)
                throw new ArgumentOutOfRangeException(nameof(record), $"A record's sequence counts from 1, and {record.ToString(CultureInfo.InvariantCulture)} was given.");

            return this with { Revealing = record };
        }

        /// <summary>A record brought into sight, or not to be: <paramref name="selection"/> selected, and none is being brought into sight.</summary>
        public SessionShown Revealed(Selection selection) =>
            this with { Selection = selection ?? throw new ArgumentNullException(nameof(selection)), Revealing = F.None };

        /// <summary>The session's records cleared: nothing folded, nothing selected, and nothing to bring into sight.</summary>
        public SessionShown Cleared() => this with { Folded = ScopeSet.Empty, Selection = Selection.None, Revealing = F.None };
    }

    /// <summary>What bringing a record into sight asks of the rows now, as <see cref="ConsoleState.RevealIn"/> says.</summary>
    internal abstract record RevealStep
    {
        private RevealStep()
        {
        }

        /// <summary>Does what <paramref name="waiting"/>, <paramref name="found"/>, <paramref name="hidden"/> or <paramref name="gone"/> does with the case it is.</summary>
        public abstract R Match<R>(Func<Waiting, R> waiting, Func<Found, R> found, Func<Hidden, R> hidden, Func<Gone, R> gone);

        /// <summary>Nothing yet: no record is being brought into sight, or the rows are still being scanned.</summary>
        public sealed record Waiting : RevealStep
        {
            private Waiting()
            {
            }

            /// <summary>The one waiting step.</summary>
            public static Waiting Instance { get; } = new Waiting();

            /// <inheritdoc />
            public override R Match<R>(Func<Waiting, R> waiting, Func<Found, R> found, Func<Hidden, R> hidden, Func<Gone, R> gone) => waiting(this);
        }

        /// <summary>The row at <see cref="Index"/> holds the record: <see cref="Next"/> selects it, for the window to scroll to.</summary>
        /// <param name="Index">The row's index.</param>
        /// <param name="Next">The state with the row selected, bringing no record into sight.</param>
        public sealed record Found(int Index, ConsoleState Next) : RevealStep
        {
            /// <inheritdoc />
            public override R Match<R>(Func<Waiting, R> waiting, Func<Found, R> found, Func<Hidden, R> hidden, Func<Gone, R> gone) => found(this);
        }

        /// <summary>The scan caught up, and no row holds the record: <see cref="Next"/> brings it into sight no more, for the window to say why.</summary>
        /// <param name="Next">The state bringing no record into sight.</param>
        public sealed record Hidden(ConsoleState Next) : RevealStep
        {
            /// <inheritdoc />
            public override R Match<R>(Func<Waiting, R> waiting, Func<Found, R> found, Func<Hidden, R> hidden, Func<Gone, R> gone) => hidden(this);
        }

        /// <summary>The record was let go before its row was found, or its session was: <see cref="Next"/> brings it into sight no more, for the window to say so.</summary>
        /// <param name="Next">The state bringing no record into sight.</param>
        public sealed record Gone(ConsoleState Next) : RevealStep
        {
            /// <inheritdoc />
            public override R Match<R>(Func<Waiting, R> waiting, Func<Found, R> found, Func<Hidden, R> hidden, Func<Gone, R> gone) => gone(this);
        }
    }
}
