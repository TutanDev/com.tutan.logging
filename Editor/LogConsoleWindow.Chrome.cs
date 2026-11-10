using System;
using System.Collections.ObjectModel;
using Tutan.Functional;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Tutan.Logging.Editor
{
    internal sealed partial class LogConsoleWindow
    {
        /// <summary>
        /// Shows the console at <paramref name="error"/>, as the toolbar's badge asks: the first open
        /// window, or a new one, shows its session, and brings its row into sight once the rows have it,
        /// which a tick after the view read the records does. When the process let the session go since
        /// the chrome counted the error, the window says it is gone, and shows what it showed.
        /// </summary>
        internal static void ShowError(UnseenError error)
        {
            LogConsoleWindow window = OpenWindow();
            if (LogConsole.Find(error.Session).IsNone)
            {
                window.Say(Told.Gone.Instance);
                return;
            }

            window.Say(F.None);
            window.Apply(window._state.Reveal(error));
        }

        /// <summary>Tells the chrome the session the window shows while it is visible, and none while it is not, so only what is in sight is seen.</summary>
        private void TellShown() => ConsoleChrome.Show(Numbered(), _visible ? _state.Session : F.None);

        /// <summary>
        /// Draws the title with the count of errors no window has shown, below Unity 6.3, which has no
        /// main toolbar for a package's badge; from 6.3 on, the badge says it and the title is the name.
        /// </summary>
        private void DrawTitle()
        {
#if !UNITY_6000_3_OR_NEWER
            string title = UnseenText.Title(ConsoleChrome.Unseen.Count);
            if (titleContent.text != title)
                titleContent = new GUIContent(title);
#endif
        }

        /// <summary>
        /// Brings the error the state reveals into sight in the rows bound, once they were read, as
        /// <see cref="ConsoleState.RevealIn"/> says: no rows are bound when the process let the session go.
        /// </summary>
        private void TryReveal(Parts parts) => Reveal(parts, _state.RevealIn(_bound.HasValue(out Bound bound) ? bound.View : F.None));

        /// <summary>
        /// Does what <paramref name="step"/> asks: selects the row and scrolls to it, or says why no row
        /// holds the error, or that it is gone. It runs on every tick while the rows are scanned, so it
        /// reads the step with a switch, which captures nothing.
        /// </summary>
        private void Reveal(Parts parts, RevealStep step)
        {
            switch (step)
            {
                case RevealStep.Waiting:
                    return;
                case RevealStep.Found found:
                    Apply(found.Next);
                    parts.Rows.ScrollToItem(found.Index);
                    return;
                case RevealStep.Hidden hidden:
                    Apply(hidden.Next);
                    Say(Told.Hidden.Instance);
                    return;
                case RevealStep.Gone gone:
                    Apply(gone.Next);
                    Say(Told.Gone.Instance);
                    return;
                default:
                    throw new InvalidOperationException($"No reveal reads a {step.GetType().Name}.");
            }
        }

        /// <summary>
        /// The settings changed, here or on disk: the rows leave out what the project now does, and
        /// everything the settings draw is drawn again, the columns among it, as every window draws the one layout.
        /// </summary>
        private void SettingsChanged()
        {
            if (!_parts.HasValue(out Parts parts))
                return;

            if (_bound.HasValue(out Bound bound))
                bound.Show(_state, _state, ConsoleSettings.Shared.LeftOut);

            DrawPersonal(parts);
            DrawSaved(parts);
            parts.Rows.RefreshItems();
            parts.Categories.RefreshItems();
            DrawSaid(parts, FindShown(_state), Marks());
        }

        private ToolbarToggle MakePause()
        {
            var pause = new ToolbarToggle
            {
                text = "Pause on Error",
                tooltip = "Pauses the game when an error is logged while it plays. Yours alone: kept in UserSettings, which the team does not share.",
            };
            pause.AddToClassList("pause");
            pause.RegisterValueChangedCallback(changed => ChangePersonal(personal => personal with { PauseOnError = changed.newValue }));
            return pause;
        }

        private ToolbarMenu MakeClearMenu()
        {
            var menu = new ToolbarMenu { tooltip = "When the console clears by itself." };
            menu.AddToClassList("clear-menu");
            menu.menu.AppendAction(
                "Clear on Play",
                _ =>
                {
                    bool on = !ConsoleSettings.Personal.ClearOnPlay;
                    ChangePersonal(personal => personal with { ClearOnPlay = on });
                },
                static _ => ConsoleSettings.Personal.ClearOnPlay ? DropdownMenuAction.Status.Checked : DropdownMenuAction.Status.Normal);
            return menu;
        }

        /// <summary>Draws what the developer keeps, as the settings hold it now: the toggles and the columns.</summary>
        private void DrawPersonal(Parts parts)
        {
            PersonalSettings personal = ConsoleSettings.Personal;
            parts.Pause.SetValueWithoutNotify(personal.PauseOnError);
            DrawColumns(parts, personal.Columns);
        }

        private static Toolbar MakeSaved()
        {
            var saved = new Toolbar();
            saved.AddToClassList("saved-filters");
            saved.style.display = DisplayStyle.None;
            return saved;
        }

        /// <summary>
        /// Draws the saved filters' buttons: made again when the filters or the skin changed since they
        /// were made, and marked on, the one whose filter the rows are of, when the filter changed.
        /// </summary>
        private void DrawSaved(Parts parts)
        {
            ReadOnlyCollection<SavedFilter> filters = ConsoleSettings.Shared.Filters;
            var mark = new SavedMark(filters, parts.Dark);
            if (!parts.SavedDrawn.HasValue(out SavedMark drawn) || drawn != mark)
            {
                parts.SavedDrawn = mark;
                parts.SavedOnDrawn = F.None;
                parts.Saved.Clear();
                for (int i = 0; i < filters.Count; i++)
                    parts.Saved.Add(MakeSavedButton(filters[i], i, filters.Count, parts.Dark));

                var add = new ToolbarButton(SaveQueryAsFilter) { text = "+", tooltip = "Saves the query as a filter, which the team shares." };
                add.AddToClassList("saved-add");
                parts.Saved.Add(add);
                parts.Saved.style.display = filters.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            }

            LogFilter shown = _state.Filter;
            if (parts.SavedOnDrawn.HasValue(out LogFilter on) && on.Equals(shown))
                return;

            parts.SavedOnDrawn = shown;
            for (int i = 0; i < filters.Count; i++)
                parts.Saved[i].EnableInClassList("on", filters[i].IsOn(shown));
        }

        private ToolbarButton MakeSavedButton(SavedFilter saved, int place, int count, bool dark)
        {
            string query = saved.Query.Length > 0 ? saved.Query : "Everything";
            var button = new ToolbarButton(() => Apply(_state.PressSaved(saved)))
            {
                tooltip = $"{query}\nPress to show it, and again to show what was shown before.",
            };
            button.AddToClassList("saved");

            var swatch = new VisualElement();
            swatch.AddToClassList("swatch");
            swatch.style.backgroundColor = Look.Saved(saved, dark);
            button.Add(swatch);
            button.Add(new Label(saved.Name.Value));
            button.AddManipulator(new ContextualMenuManipulator(menu => SavedMenu(saved, place, count, menu)));
            return button;
        }

        /// <summary>The menu of the saved filter at <paramref name="place"/> of <paramref name="count"/>.</summary>
        private void SavedMenu(SavedFilter saved, int place, int count, ContextualMenuPopulateEvent menu)
        {
            FilterName name = saved.Name;
            menu.menu.AppendAction("Edit...", _ => SavedFilterWindow.Edit(saved));
            menu.menu.AppendAction("Remove", _ => ChangeShared(shared => shared.Removing(name)));
            menu.menu.AppendSeparator();
            menu.menu.AppendAction("Move Left", _ => ChangeShared(shared => shared.MovingBy(name, -1)),
                place > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
            menu.menu.AppendAction("Move Right", _ => ChangeShared(shared => shared.MovingBy(name, 1)),
                place < count - 1 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled);
        }

        /// <summary>Opens the dialog that saves the filter the rows are of, as its query writes it, under a name.</summary>
        private void SaveQueryAsFilter() => SavedFilterWindow.New(_state.Filter.ToString());

        /// <summary>Changes what the team shares, as <paramref name="change"/> makes it, and says why when it is refused or cannot be written.</summary>
        private void ChangeShared(Func<SharedSettings, Result<SharedSettings>> change) =>
            ConsoleSettings.ChangeShared(change).Match(refused => Say(new Told.Trouble(refused.Message)), static _ => { });

        /// <summary>
        /// Changes what the developer keeps, as <paramref name="change"/> makes it, and says why when it
        /// cannot be written. Then it draws what the settings hold in every case: written, as every window
        /// does; nothing written, or refused, so this one shows no toggle or column that is neither saved nor the others'.
        /// </summary>
        private void ChangePersonal(Func<PersonalSettings, PersonalSettings> change)
        {
            ConsoleSettings.ChangePersonal(change).Match(refused => Say(new Told.Trouble(refused.Message)), static _ => { });
            if (_parts.HasValue(out Parts parts))
                DrawPersonal(parts);
        }

        /// <summary>What the saved filters' buttons were made of: the filters, by reference, as a change makes another list, and the skin their colours are of.</summary>
        /// <param name="Filters">The saved filters.</param>
        /// <param name="Dark">Whether they were drawn for the dark skin.</param>
        private readonly record struct SavedMark(ReadOnlyCollection<SavedFilter> Filters, bool Dark);
    }
}
