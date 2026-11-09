using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Tutan.Functional;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Tutan.Logging.Editor
{
    /// <summary>
    /// The log console, at Window > General > Log Console: a session's records as rows, which a query
    /// line filters, with a badge for each level, a tree of the categories, and the details of the
    /// row selected.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the developer asked for is a <see cref="ConsoleState"/>, and every input goes through
    /// <see cref="Apply(ConsoleState, bool)"/>, which does what the change asks of the rows and draws what it changed.
    /// The rows are a <see cref="LogView"/> of the session shown, updated each editor tick, and each
    /// part of the window is drawn again only when what it shows changed.
    /// </para>
    /// <para>
    /// The chrome is the editor's, one for every window: <see cref="ConsoleChrome"/> clears the console
    /// and tells each window, and counts the errors no window has shown, which each window tells the
    /// session it shows while it is visible. <see cref="ConsoleSettings"/> holds what the team shares
    /// and what the developer keeps, and each window draws again when they change.
    /// </para>
    /// <para>
    /// A shortcut that opens it is registered with no key, for a developer to bind in Edit > Shortcuts.
    /// </para>
    /// </remarks>
    internal sealed partial class LogConsoleWindow : EditorWindow
    {
        // Loaded by GUID, from the .meta beside it: its path depends on how the package was installed.
        internal const string StyleGuid = "5e11c617538f44b0adebc3b90288d55b"; // LogConsoleWindow.uss

        // What a copy or an export leaves out, unless the developer picks the item that says it does not.
        private const LogRedaction Redacted = LogRedaction.Personal | LogRedaction.HomeFolder;

        private static readonly CategoryTree s_noCategories = CategoryTree.Of(Array.Empty<LogEventSpec>());
        private static readonly ReadOnlyCollection<SaidLine> s_nothingSaid = Array.AsReadOnly(Array.Empty<SaidLine>());

        // What survives a domain reload, as when Play Mode starts, and a restart, in the editor's layout.
        [SerializeField] private SavedConsole _saved = new SavedConsole();

        private ConsoleState _state = ConsoleState.Start;
        private IReadOnlyList<LogConsoleSession> _sessions = Array.Empty<LogConsoleSession>();

        // The view of the session shown, while the process has it.
        private Optional<Bound> _bound;
        private UnityPaths? _paths;

        // What the last try at opening a file or exporting told, said under the query line until the next try.
        private Optional<Told> _told;

        // The window's parts: none until CreateGUI made them, so nothing is drawn into parts not there.
        private Optional<Parts> _parts;

        // The number the chrome gave the window, which it tells the session it shows by: none until it is asked.
        private Optional<WindowNumber> _number;

        // Whether the window is visible, as Unity says: its session is seen only while it is.
        private bool _visible;

        /// <summary>Opens the log console, or brings it forward.</summary>
        [MenuItem("Window/General/Log Console")]
        public static void Open() => OpenWindow();

        /// <summary>Opens the log console, or brings the first open one forward, and gives it.</summary>
        private static LogConsoleWindow OpenWindow()
        {
            var window = GetWindow<LogConsoleWindow>();
            window.titleContent = new GUIContent(UnseenText.Name);
            window.minSize = new Vector2(640, 320);
            window.Show();
            window.DrawTitle();
            return window;
        }

        [Shortcut("Log Console/Open")]
        private static void OpenByShortcut() => Open();

        private UnityPaths Paths => _paths ??= UnityPaths.ForThisProject();

        /// <summary>The categories of the session shown: none while none is.</summary>
        private CategoryTree Tree => _bound.HasValue(out Bound bound) ? bound.Tree : s_noCategories;

        /// <summary>The number the chrome gave the window, asked for the first time it is needed, whichever of Unity's calls comes first.</summary>
        private WindowNumber Numbered()
        {
            if (!_number.HasValue(out WindowNumber number))
            {
                number = ConsoleChrome.NumberWindow();
                _number = number;
            }

            return number;
        }

        private void OnEnable()
        {
            _state = _saved.ToDomain().Or(ConsoleState.Start);
            EditorApplication.update += Tick;
            ConsoleChrome.Cleared += ChromeCleared;
            ConsoleChrome.UnseenChanged += DrawTitle;
            ConsoleSettings.Changed += SettingsChanged;
            TellShown();
            DrawTitle();
        }

        private void OnDisable()
        {
            EditorApplication.update -= Tick;
            ConsoleChrome.Cleared -= ChromeCleared;
            ConsoleChrome.UnseenChanged -= DrawTitle;
            ConsoleSettings.Changed -= SettingsChanged;
            ConsoleChrome.Show(Numbered(), F.None);
        }

        private void OnBecameVisible()
        {
            _visible = true;
            TellShown();
        }

        private void OnBecameInvisible()
        {
            _visible = false;
            TellShown();
        }

        /// <summary>Builds the window, when it opens and after every domain reload.</summary>
        public void CreateGUI()
        {
            VisualElement root = rootVisualElement;
            root.Clear();
            StyleSheet? style = AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(StyleGuid));
            if (style != null)
                root.styleSheets.Add(style);
            root.AddToClassList("log-console");
            bool dark = EditorGUIUtility.isProSkin;
            root.EnableInClassList("dark", dark);

            ToolbarButton session = MakeSession();
            ToolbarToggle follow = MakeFollow();
            ToolbarToggle pause = MakePause();
            ReadOnlyCollection<ToolbarButton> badges = MakeBadges();
            var status = new Label();
            status.AddToClassList("status");
            root.Add(MakeToolbar(session, follow, pause, badges, status));

            Toolbar saved = MakeSaved();
            root.Add(saved);

            TextField query = MakeQuery();
            root.Add(query);

            var said = new VisualElement();
            said.AddToClassList("said");
            root.Add(said);

            ListView rows = MakeRows();
            ScrollView details = MakeDetails();
            var rowsAndDetails = new TwoPaneSplitView(1, 220, TwoPaneSplitViewOrientation.Vertical) { viewDataKey = "log-console-rows" };
            rowsAndDetails.Add(rows);
            rowsAndDetails.Add(details);

            ListView categories = MakeCategories();
            var panes = new TwoPaneSplitView(0, 200, TwoPaneSplitViewOrientation.Horizontal) { viewDataKey = "log-console-panes" };
            panes.AddToClassList("panes");
            panes.Add(categories);
            panes.Add(rowsAndDetails);
            root.Add(panes);

            // The parts are new, and nothing is drawn in them yet.
            var parts = new Parts(root, dark, session, follow, pause, saved, status, query, said, rows, details, categories, badges);
            _parts = parts;
            _sessions = Array.Empty<LogConsoleSession>();
            DrawToggles(parts);
            Refresh(parts, redraw: true);
        }

        private void Tick()
        {
            if (_parts.HasValue(out Parts parts))
                Refresh(parts, redraw: false);
        }

        /// <summary>
        /// Takes in what changed since the last tick, the process's sessions and the records of the
        /// session shown, and draws it, or every part when <paramref name="redraw"/>. It reads the
        /// session shown once, after the state chose it, and hands that to all it draws. Then, while an
        /// error is to be brought into sight, it is, once the rows have it. An error to be brought into
        /// sight in a session the sessions let go is said to be gone first, before they choose another.
        /// </summary>
        private void Refresh(Parts parts, bool redraw)
        {
            DrawSkin(parts);
            IReadOnlyList<LogConsoleSession> sessions = LogConsole.Sessions;
            bool changed = !ReferenceEquals(sessions, _sessions);

            // Of the list read, before it chooses another session, which would end a reveal saying nothing.
            if (changed)
                Reveal(parts, _state.RevealAmong(sessions));

            ConsoleState next = changed ? _state.SessionsChanged(sessions) : _state;
            Optional<LogConsoleSession> shown = FindShown(next);
            if (changed)
            {
                _sessions = sessions;
                Apply(next, shown, fromList: false);

                // A session shown can end, or be read from a file again, under the same id.
                Bind(parts, shown);
                DrawSessions(parts, shown);
            }

            bool draw = redraw;
            if (_bound.HasValue(out Bound bound))
            {
                RowsChange change = bound.Update();
                if (change == RowsChange.RowsAndCategories)
                    parts.Categories.itemsSource = bound.Tree.Nodes;
                draw |= change != RowsChange.Nothing;
            }

            if (draw)
                DrawAll(parts, shown);

            if (_state.Revealing.IsSome)
                TryReveal(parts);
        }

        /// <summary>Makes <paramref name="next"/> the state, does what the change asks of the rows, and draws what it changed.</summary>
        /// <param name="next">The state an input made.</param>
        /// <param name="fromList">Whether the list of rows made it, as when a row is clicked: the list then shows its selection already.</param>
        private void Apply(ConsoleState next, bool fromList = false)
        {
            if (!next.Equals(_state))
                Apply(next, FindShown(next), fromList);
        }

        /// <summary>Makes <paramref name="next"/> the state as <see cref="Apply(ConsoleState, bool)"/> does, drawing <paramref name="shown"/>, the session it shows as the caller read it.</summary>
        private void Apply(ConsoleState next, Optional<LogConsoleSession> shown, bool fromList)
        {
            ConsoleState was = _state;
            if (next.Equals(was))
                return;

            _state = next;
            _saved = SavedConsole.FromDomain(next);
            if (next.Session != was.Session)
                TellShown();
            if (!_parts.HasValue(out Parts parts))
                return;

            if (next.Session != was.Session)
                Bind(parts, shown);
            else if (_bound.HasValue(out Bound bound))
                bound.Show(was, next, ConsoleSettings.Shared.LeftOut);

            if (parts.Query.value != next.Query.Text)
                parts.Query.SetValueWithoutNotify(next.Query.Text);
            parts.Follow.SetValueWithoutNotify(next.Following);

            Optional<RowsMarks> marks = Marks();
            DrawSessions(parts, shown);
            DrawSaid(parts, shown, marks);
            DrawBadges(parts, marks);
            if (!next.Filter.Equals(was.Filter))
            {
                DrawCategories(parts);
                DrawSaved(parts);
            }

            if (!next.Selection.Equals(was.Selection))
            {
                ShowSelection(parts, marks, listShowsIt: fromList);
                DrawDetails(parts, marks);
            }

            if (next.Following && !was.Following)
                ScrollToNewest(parts);
        }

        /// <summary>Binds a view of <paramref name="shown"/>, the session shown, when its records are not the bound view's already.</summary>
        private void Bind(Parts parts, Optional<LogConsoleSession> shown)
        {
            Optional<LogArchive> records = shown.Map(static session => session.Records);
            bool same = records.HasValue(out LogArchive source)
                ? _bound.HasValue(out Bound bound) && ReferenceEquals(source, bound.View.Source)
                : _bound.IsNone;
            if (same)
                return;

            _bound = records.HasValue(out LogArchive archive) ? F.Some(Bound.Of(archive, _state, ConsoleSettings.Shared.LeftOut)) : F.None;
            parts.Categories.itemsSource = Tree.Nodes;
            DrawAll(parts, shown);
        }

        /// <summary>Draws every part again, of <paramref name="shown"/>, the session shown.</summary>
        private void DrawAll(Parts parts, Optional<LogConsoleSession> shown)
        {
            parts.Rows.RefreshItems();
            Optional<RowsMarks> marks = Marks();
            ShowSelection(parts, marks);
            DrawSessions(parts, shown);
            DrawSaid(parts, shown, marks);
            DrawBadges(parts, marks);
            DrawCategories(parts);
            DrawSaved(parts);
            DrawStatus(parts, marks);
            DrawDetails(parts, marks);
            if (_state.Following)
                ScrollToNewest(parts);
        }

        /// <summary>What the parts are drawn of, as the rows of the session shown say: none while no session is bound.</summary>
        private Optional<RowsMarks> Marks() => _bound.HasValue(out Bound bound) ? F.Some(bound.Marks(_state)) : F.None;

        /// <summary>Draws the parts again for the editor's skin, when it changed since they were drawn: the colours of the categories and the saved filters are the skin's.</summary>
        private void DrawSkin(Parts parts)
        {
            bool dark = EditorGUIUtility.isProSkin;
            if (parts.Dark == dark)
                return;

            parts.Dark = dark;
            parts.Root.EnableInClassList("dark", dark);
            parts.Rows.RefreshItems();
            parts.Categories.RefreshItems();
            DrawSaved(parts);
        }

        private ToolbarButton MakeSession()
        {
            // Drawn of no session, as the parts are until a session is shown.
            var session = new ToolbarButton(ShowSessionMenu)
            {
                text = SessionText(F.None),
                tooltip = "The session shown. Choose another, open a session's file, or close one.",
            };
            session.AddToClassList("session");
            return session;
        }

        private ToolbarToggle MakeFollow()
        {
            var follow = new ToolbarToggle { text = "Follow", tooltip = "Keeps the newest row in sight, and shows a live session as it starts. Scrolling up stops it." };
            follow.SetValueWithoutNotify(_state.Following);
            follow.RegisterValueChangedCallback(changed => Apply(changed.newValue ? _state.Follow() : _state.StopFollowing()));
            return follow;
        }

        private Toolbar MakeToolbar(ToolbarButton session, ToolbarToggle follow, ToolbarToggle pause, IEnumerable<ToolbarButton> badges, Label status)
        {
            var toolbar = new Toolbar();
            toolbar.Add(session);
            var clear = new ToolbarButton(ConsoleChrome.Clear)
            {
                text = "Clear",
                tooltip = "Empties the live sessions, which keep what is logged after, and lets the ended ones go. A file's session stays until it is closed.",
            };
            clear.AddToClassList("clear");
            toolbar.Add(clear);
            toolbar.Add(MakeClearMenu());
            toolbar.Add(follow);
            toolbar.Add(pause);
            toolbar.Add(new ToolbarSpacer { flex = true });
            foreach (ToolbarButton badge in badges)
                toolbar.Add(badge);

            toolbar.Add(status);
            var more = new ToolbarButton(ShowMoreMenu) { text = "⋮", tooltip = "Open a session's file, export the rows shown, or save the query as a filter." };
            more.AddToClassList("more");
            toolbar.Add(more);
            return toolbar;
        }

        private TextField MakeQuery()
        {
            var query = new TextField { tooltip = QueryHelp };
            query.AddToClassList("query");
            query.textEdition.placeholder = "level>=warning game.* -game.input.* \"no image\" /fail(ed|ure)/ Slot:3";
            query.SetValueWithoutNotify(_state.Query.Text);
            query.RegisterValueChangedCallback(changed => Apply(_state.Type(changed.newValue ?? "")));
            return query;
        }

        /// <summary>Draws the label of <paramref name="shown"/>, the session shown, when it is another session than it was drawn of.</summary>
        private static void DrawSessions(Parts parts, Optional<LogConsoleSession> shown)
        {
            if (parts.SessionDrawn == shown)
                return;

            parts.SessionDrawn = shown;
            parts.Session.text = SessionText(shown);
        }

        private static string SessionText(Optional<LogConsoleSession> shown) => shown.Map(static session => SessionLabel.Of(session)).Or("No session") + " ▾";

        private void ShowSessionMenu()
        {
            if (!_parts.HasValue(out Parts parts))
                return;

            var menu = new GenericMenu();
            foreach (LogConsoleSession session in _sessions)
            {
                LogSessionId id = session.Id;
                bool shown = _state.Session.HasValue(out LogSessionId current) && current == id;
                menu.AddItem(new GUIContent(MenuText(SessionLabel.Of(session))), shown, () => Apply(_state.Choose(id)));
            }

            if (_sessions.Count == 0)
                menu.AddDisabledItem(new GUIContent("No sessions: a console sink starts one as the program logs"));

            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Open a Session File..."), false, OpenSessionFile);

            Optional<GenericMenu.MenuFunction> closing = FindShown(_state).Bind(static session => Closing(session));
            if (closing.HasValue(out GenericMenu.MenuFunction close))
                menu.AddItem(new GUIContent("Close This Session"), false, close);
            else
                menu.AddDisabledItem(new GUIContent("Close This Session"));

            menu.DropDown(parts.Session.worldBound);
        }

        private void ShowMoreMenu()
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent("Open a Session File..."), false, OpenSessionFile);
            menu.AddSeparator("");
            if (_bound.HasValue(out Bound bound) && bound.View.Count > 0)
            {
                menu.AddItem(new GUIContent("Export the Rows Shown..."), false, () => Export(Redacted));
                menu.AddItem(new GUIContent("Export the Rows Shown, Nothing Left Out..."), false, () => Export(LogRedaction.None));
            }
            else
            {
                menu.AddDisabledItem(new GUIContent("Export the Rows Shown..."));
                menu.AddDisabledItem(new GUIContent("Export the Rows Shown, Nothing Left Out..."));
            }

            menu.AddSeparator("");
            menu.AddItem(new GUIContent("Save the Query as a Filter..."), false, SaveQueryAsFilter);
            menu.AddItem(new GUIContent("Log Console Settings..."), false, LogConsoleSettingsProvider.Open);
            menu.ShowAsContext();
        }

        /// <summary>
        /// The editor cleared the console, which held <paramref name="before"/>: what was folded and
        /// selected of the session shown goes when the clear emptied it.
        /// </summary>
        private void ChromeCleared(IReadOnlyList<LogConsoleSession> before) => Apply(_state.Cleared(before));

        /// <summary>
        /// The session <paramref name="state"/> shows, as the console has it now, or none once it has let
        /// it go: a read of the process's sessions, as deviation 8 allows. Each tick reads it once, after
        /// the state chose, and each change of state once, and hands it to all it draws, so all of that is
        /// of one session.
        /// </summary>
        private static Optional<LogConsoleSession> FindShown(ConsoleState state) => state.Session.Bind(static id => LogConsole.Find(id));

        /// <summary>What closes <paramref name="session"/>: none for a live one, the program's, and for an ended session or a file's, its closing.</summary>
        private static Optional<GenericMenu.MenuFunction> Closing(LogConsoleSession session) =>
            session.Match<Optional<GenericMenu.MenuFunction>>(
                static _ => F.None,
                static ended => new GenericMenu.MenuFunction(() => LogConsole.Close(ended)),
                static file => new GenericMenu.MenuFunction(() => LogConsole.Close(file)));

        private void OpenSessionFile()
        {
            string path = EditorUtility.OpenFilePanel("Open a Session File", LogFile.Folder, LogFile.Extension.TrimStart('.'));
            if (string.IsNullOrEmpty(path))
                return;

            LogFile.Read(path).Match(
                refused => Say(new Told.Trouble(refused.Message)),
                file =>
                {
                    Say(F.None);
                    Apply(_state.Choose(LogConsole.Open(file).Id));
                });
        }

        private void Export(LogRedaction redact)
        {
            if (!_bound.HasValue(out Bound bound))
                return;

            string name = LogFile.ExportName(bound.View.Source.Program, DateTimeOffset.Now);
            string path = EditorUtility.SaveFilePanel("Export the Rows Shown", LogFile.Folder, name, LogFile.Extension.TrimStart('.'));
            if (string.IsNullOrEmpty(path))
                return;

            LogFile.Export(bound.View, path, new LogSinkOptions(LogLevel.Trace, 0, redact)).Match(
                refused => Say(new Told.Trouble(refused.Message)),
                exported => Say(exported.NotDeleted.Map<LogNotDeleted, Told>(left => new Told.NotDeleted(path, left))));
        }

        /// <summary>Says <paramref name="told"/> under the query line until the next try, or, for none, nothing of the last try.</summary>
        private void Say(Optional<Told> told)
        {
            _told = told;
            if (_parts.HasValue(out Parts parts))
                DrawSaid(parts, FindShown(_state), Marks());
        }

        /// <summary>
        /// Draws what is said under the query line of <paramref name="shown"/>, the session shown, when
        /// what it is of changed since it was drawn: the lines are made again then, and drawn again only
        /// when they differ, so a line's button stays as it is.
        /// </summary>
        private void DrawSaid(Parts parts, Optional<LogConsoleSession> shown, Optional<RowsMarks> marks)
        {
            Optional<RowsSaid> rows = marks.HasValue(out RowsMarks of) ? F.Some(of.Said) : F.None;
            LeftOut leftOut = ConsoleSettings.Shared.LeftOut;
            var mark = new SaidMark(_told, _state.Query, shown, rows, leftOut);
            if (parts.SaidMarked.HasValue(out SaidMark marked) && marked == mark)
                return;

            parts.SaidMarked = mark;
            ReadOnlyCollection<SaidLine> said = SaidLines.Of(_told, _state.Query, _bound.Map(static shownRows => shownRows.View), shown, leftOut);
            if (said.SequenceEqual(parts.SaidDrawn))
                return;

            parts.SaidDrawn = said;
            parts.Said.Clear();
            foreach (SaidLine line in said)
                parts.Said.Add(Said(line));
        }

        private VisualElement Said(SaidLine said)
        {
            var line = new VisualElement();
            line.AddToClassList("said-line");
            line.AddToClassList(SaidClass(said.Kind));
            var label = new Label(said.Text);
            label.selection.isSelectable = true;
            line.Add(label);
            if (said.Kind == SaidKind.GaveUp)
            {
                var retry = new Button(Retry) { text = "Retry" };
                retry.AddToClassList("retry");
                line.Add(retry);
            }
            else if (said.Kind == SaidKind.LeftOut)
            {
                var edit = new Button(LogConsoleSettingsProvider.Open) { text = "Edit", tooltip = "Opens Project Settings > Log Console, where the project says what it always leaves out." };
                edit.AddToClassList("retry");
                line.Add(edit);
            }

            return line;
        }

        private static string SaidClass(SaidKind kind) => kind switch
        {
            SaidKind.Trouble or SaidKind.Refused => "error",
            SaidKind.NotDeleted or SaidKind.MatchingNothing or SaidKind.GaveUp or SaidKind.Skipped or SaidKind.Hidden or SaidKind.Gone => "warning",
            SaidKind.Lost or SaidKind.LeftOut => "info",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), $"{kind} is not a kind of line the console says."),
        };

        private void Retry()
        {
            if (_bound.HasValue(out Bound bound))
                bound.Retry();
        }

        /// <summary>Draws how many rows there are, and whether a scan is reading, when either changed since it was drawn.</summary>
        private void DrawStatus(Parts parts, Optional<RowsMarks> marks)
        {
            Optional<StatusMark> mark = marks.HasValue(out RowsMarks of) ? F.Some(of.Status) : F.None;
            if (parts.StatusDrawn == mark)
                return;

            parts.StatusDrawn = mark;
            parts.Status.text = mark.HasValue(out StatusMark status) ? CountText.Rows(status.Rows, status.Scanning) : "";
        }

        /// <summary>A label a menu shows as it is: a slash in it would make a submenu.</summary>
        private static string MenuText(string text) => text.Replace('/', '∕');

        private const string QueryHelp =
            "Each word keeps records, and a word after - leaves them out.\n"
            + "level>=warning   the levels kept, with >, <=, <, = or : too\n"
            + "game.*  game.save  game.save/Saved   events, by category or key\n"
            + "\"no image\"  /fail(ed|ure)/   text or a regular expression in the message\n"
            + "Slot:3  File:*.png  File:/\\.png$/   a field's value\n"
            + "frame:1200..1300  scope:4182  thread:7";

        /// <summary>
        /// The parts of the window <see cref="CreateGUI"/> made, and what each was last drawn of:
        /// made together, so a part is there exactly when the others are. A part whose mark is none is
        /// drawn as it was made: of no session, no rows, and no row focused.
        /// </summary>
        private sealed class Parts
        {
            public Parts(VisualElement root, bool dark, ToolbarButton session, ToolbarToggle follow, ToolbarToggle pause, Toolbar saved, Label status,
                TextField query, VisualElement said, ListView rows, ScrollView details, ListView categories, ReadOnlyCollection<ToolbarButton> badges)
            {
                Root = root;
                Dark = dark;
                Session = session;
                Follow = follow;
                Pause = pause;
                Saved = saved;
                Status = status;
                Query = query;
                Said = said;
                Rows = rows;
                Details = details;
                Categories = categories;
                Badges = badges;
            }

            /// <summary>The window's root, which the stylesheet's <c>.dark</c> is set on under the dark skin.</summary>
            public VisualElement Root { get; }

            /// <summary>The button that shows the session shown, and opens the menu of sessions.</summary>
            public ToolbarButton Session { get; }

            /// <summary>The toggle that follows the newest row.</summary>
            public ToolbarToggle Follow { get; }

            /// <summary>The toggle of Pause on Error.</summary>
            public ToolbarToggle Pause { get; }

            /// <summary>The row of the saved filters' buttons, hidden while none is saved.</summary>
            public Toolbar Saved { get; }

            /// <summary>How many rows there are, and whether a scan is reading.</summary>
            public Label Status { get; }

            /// <summary>The query line.</summary>
            public TextField Query { get; }

            /// <summary>What is said under the query line.</summary>
            public VisualElement Said { get; }

            /// <summary>The rows.</summary>
            public ListView Rows { get; }

            /// <summary>The details of the row focused.</summary>
            public ScrollView Details { get; }

            /// <summary>The tree of the categories.</summary>
            public ListView Categories { get; }

            /// <summary>A badge for each level, in the order of <see cref="LevelLook.Shown"/>.</summary>
            public ReadOnlyCollection<ToolbarButton> Badges { get; }

            /// <summary>Whether the parts are drawn for the dark skin.</summary>
            public bool Dark { get; set; }

            /// <summary>The session whose label was last drawn: none for no session.</summary>
            public Optional<LogConsoleSession> SessionDrawn { get; set; }

            /// <summary>What the lines said under the query line were last made of: none until they are.</summary>
            public Optional<SaidMark> SaidMarked { get; set; }

            /// <summary>The lines said under the query line, as they were last drawn.</summary>
            public ReadOnlyCollection<SaidLine> SaidDrawn { get; set; } = s_nothingSaid;

            /// <summary>What the badges were last drawn of: none until they are.</summary>
            public Optional<BadgesMark> BadgesDrawn { get; set; }

            /// <summary>What the list's selection was last drawn of: none until it is.</summary>
            public Optional<SelectionMark> SelectionDrawn { get; set; }

            /// <summary>What the status was last drawn of: none for no rows.</summary>
            public Optional<StatusMark> StatusDrawn { get; set; }

            /// <summary>What the details were last drawn of: none for no row focused.</summary>
            public Optional<DetailsKey> DetailsDrawn { get; set; }

            /// <summary>What the saved filters' buttons were last made of: none until they are.</summary>
            public Optional<SavedMark> SavedDrawn { get; set; }

            /// <summary>The filter the saved filters' buttons were last marked on for: none until they are.</summary>
            public Optional<LogFilter> SavedOnDrawn { get; set; }
        }
    }
}
