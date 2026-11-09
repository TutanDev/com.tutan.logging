using System;
using System.Collections.ObjectModel;
using Tutan.Functional;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Tutan.Logging.Editor
{
    /// <summary>
    /// Project Settings > Log Console: what the team shares of the console, the saved filters, the
    /// colours chosen for categories, and what is always left out, drawn of
    /// <see cref="ConsoleSettings.Shared"/> and drawn again each time it changes. Each change goes
    /// through <see cref="ConsoleSettings.ChangeShared"/>, and the page says why one was refused.
    /// </summary>
    internal sealed class LogConsoleSettingsProvider : SettingsProvider
    {
        /// <summary>Where the page is in Project Settings.</summary>
        public const string PagePath = "Project/Log Console";

        private Optional<VisualElement> _root;
        private Optional<HelpBox> _troubleBox;

        // Why the last change made on the page was refused, until the next change: none when it was not.
        private Optional<string> _trouble;

        private LogConsoleSettingsProvider()
            : base(PagePath, SettingsScope.Project, new[] { "log", "console", "saved filter", "filter", "category", "colour", "color", "left out" })
        {
        }

        /// <summary>Opens Project Settings at the page.</summary>
        public static void Open() => SettingsService.OpenProjectSettings(PagePath);

        [SettingsProvider]
        private static SettingsProvider Create() => new LogConsoleSettingsProvider();

        /// <inheritdoc />
        public override void OnActivate(string searchContext, VisualElement rootElement)
        {
            _root = F.Some(rootElement);
            _trouble = F.None;
            ConsoleSettings.Changed += Draw;
            Draw();
        }

        /// <inheritdoc />
        public override void OnDeactivate()
        {
            ConsoleSettings.Changed -= Draw;
            _root = F.None;
            _troubleBox = F.None;
        }

        /// <summary>Draws the page again, of the settings as they are now.</summary>
        private void Draw()
        {
            if (!_root.HasValue(out VisualElement page))
                return;

            SettingsLook.Styled(page);
            page.AddToClassList("log-console-settings");
            bool dark = EditorGUIUtility.isProSkin;
            SharedSettings shared = ConsoleSettings.Shared;

            page.Add(Note("Log Console", "settings-title"));
            page.Add(Note(
                $"What the team shares of the log console, kept in {ConsoleSettings.SharedPath} and committed with the project. "
                + "Clear on Play and Pause on Error are each developer's own, in the console's toolbar.", "settings-note"));

            ConsoleSettings.SharedRead.Match(
                read =>
                {
                    foreach (SettingSkipped skipped in read.Skipped)
                        page.Add(new HelpBox($"{skipped.Entry} was left out, as it does not read: {skipped.Reason} The next change writes the file without it.", HelpBoxMessageType.Warning));
                    return default(Unit);
                },
                unreadable =>
                {
                    page.Add(new HelpBox(
                        $"{ConsoleSettings.SharedPath} cannot be read, so the console shares nothing and writes nothing over it: {unreadable.Reason} "
                        + "Fix or delete it, and the console reads it again when the editor has focus again.", HelpBoxMessageType.Error));
                    return default(Unit);
                });

            var trouble = new HelpBox("", HelpBoxMessageType.Error);
            page.Add(trouble);
            _troubleBox = F.Some(trouble);
            DrawTrouble();

            page.Add(Note("Saved Filters", "settings-header"));
            page.Add(Note("Buttons over the query line: pressing one shows the rows of its query, and pressing it again shows what was shown before.", "settings-note"));
            ReadOnlyCollection<SavedFilter> filters = shared.Filters;
            for (int i = 0; i < filters.Count; i++)
                page.Add(FilterRow(filters[i], i, filters.Count, dark));
            page.Add(AddButton("Add a Filter...", static () => SavedFilterWindow.New("")));

            page.Add(Note("Category Colours", "settings-header"));
            page.Add(Note("A category is drawn in the colour chosen for it, and so is each category under it that has none of its own; every other in a hue made of its name.", "settings-note"));
            foreach (CategoryColour chosen in shared.Colours.Chosen)
                page.Add(ColourRow(chosen));
            page.Add(AddButton("Add a Category Colour...", CategoryColourWindow.OpenForAny));

            page.Add(Note("Always Left Out", "settings-header"));
            page.Add(Note("Words after - that every view of the console leaves out, after its query line's own, as -unity/Logged. The query line does not show them; a line under it says they are left out.", "settings-note"));
            var leftOut = new TextField { value = shared.LeftOut.Query, isDelayed = true, tooltip = "Pressing Enter, or leaving the field, saves it." };
            leftOut.AddToClassList("settings-left-out");
            leftOut.textEdition.placeholder = "-unity/Logged -\"Shader warning\"";
            leftOut.RegisterValueChangedCallback(changed =>
                Change(current => LeftOut.Parse(changed.newValue ?? "").Map(always => current.LeavingOut(always))));
            page.Add(leftOut);
        }

        private VisualElement FilterRow(SavedFilter saved, int place, int count, bool dark)
        {
            FilterName name = saved.Name;
            VisualElement row = Row(Look.Saved(saved, dark));
            row.Add(Note(name.Value, "settings-name"));
            Label query = Note(saved.Query.Length > 0 ? saved.Query : "Everything", "settings-query");
            query.tooltip = saved.Query;
            row.Add(query);
            row.Add(RowButton("Edit...", "Changes its name, its query or its colour.", () => SavedFilterWindow.Edit(saved)));

            Button up = RowButton("▲", "Moves its button left.", () => Change(current => current.MovingBy(name, -1)));
            up.SetEnabled(place > 0);
            row.Add(up);
            Button down = RowButton("▼", "Moves its button right.", () => Change(current => current.MovingBy(name, 1)));
            down.SetEnabled(place < count - 1);
            row.Add(down);
            row.Add(RowButton("Remove", "Removes it, for the whole team.", () => Change(current => current.Removing(name))));
            return row;
        }

        private VisualElement ColourRow(CategoryColour chosen)
        {
            CategoryName category = chosen.Category;
            VisualElement row = Row(chosen.Colour.Colour);
            row.Add(Note(category.Value, "settings-name"));
            row.Add(Note(chosen.Colour.Hex, "settings-query"));
            row.Add(RowButton("Choose Colour...", "Chooses another colour for it.", () => CategoryColourWindow.Open(category, chosen.Colour.Colour)));
            row.Add(RowButton("Remove", "Draws it as the category it is under is drawn, or in its name's hue.", () => Change(current => current.Colouring(current.Colours.Unchoosing(category)))));
            return row;
        }

        /// <summary>Makes the change, and says why when it is refused or cannot be written; the page is drawn again when it is made.</summary>
        private void Change(Func<SharedSettings, Result<SharedSettings>> change)
        {
            _trouble = ConsoleSettings.ChangeShared(change).Match<Optional<string>>(static refused => F.Some(SaidLines.Closed(refused.Message)), static _ => F.None);
            DrawTrouble();
        }

        private void DrawTrouble()
        {
            if (!_troubleBox.HasValue(out HelpBox box))
                return;

            box.text = _trouble.Or("");
            box.style.display = _trouble.IsSome ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private static VisualElement Row(Color swatch)
        {
            var row = new VisualElement();
            row.AddToClassList("settings-row");
            var mark = new VisualElement();
            mark.AddToClassList("swatch");
            mark.style.backgroundColor = swatch;
            row.Add(mark);
            return row;
        }

        private static Button RowButton(string text, string tooltip, Action press)
        {
            var button = new Button(press) { text = text, tooltip = tooltip };
            button.AddToClassList("settings-button");
            return button;
        }

        private static Button AddButton(string text, Action press)
        {
            var button = new Button(press) { text = text };
            button.AddToClassList("settings-add");
            return button;
        }

        private static Label Note(string text, string kind)
        {
            var label = new Label(text);
            label.AddToClassList(kind);
            return label;
        }
    }
}
