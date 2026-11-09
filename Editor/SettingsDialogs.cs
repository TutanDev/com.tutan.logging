using System;
using Tutan.Functional;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Tutan.Logging.Editor
{
    /// <summary>
    /// The dialog that saves a query as a filter the team shares, or edits one: its name, its query, and
    /// its colour, or its name's hue. What it holds is in serialized fields, so a domain reload keeps it.
    /// </summary>
    internal sealed class SavedFilterWindow : EditorWindow
    {
        // The name of the saved filter edited, as it was: empty for a new one.
        [SerializeField] private string _original = "";
        [SerializeField] private string _name = "";
        [SerializeField] private string _query = "";

        // Whether a colour is chosen, or the filter is drawn in its name's hue.
        [SerializeField] private bool _chosen;
        [SerializeField] private Color _colour = Color.white;

        private Optional<Button> _save;
        private Optional<Label> _refused;

        /// <summary>Opens the dialog that saves <paramref name="query"/> as a new filter.</summary>
        public static void New(string query)
        {
            if (query is null)
                throw new ArgumentNullException(nameof(query));

            Open("Save the Query as a Filter", "", query, F.None);
        }

        /// <summary>Opens the dialog that edits <paramref name="saved"/>.</summary>
        public static void Edit(SavedFilter saved)
        {
            if (saved is null)
                throw new ArgumentNullException(nameof(saved));

            Open("Edit a Saved Filter", saved.Name.Value, saved.Query, saved.Colour);
        }

        private static void Open(string title, string name, string query, Optional<ChosenColour> colour)
        {
            var window = CreateInstance<SavedFilterWindow>();
            window.titleContent = new GUIContent(title);
            window._original = name;
            window._name = name;
            window._query = query;
            window._chosen = colour.IsSome;
            window._colour = colour.Map(static chosen => chosen.Colour).Or(Color.white);
            window.minSize = new Vector2(440, 150);
            window.ShowUtility();
        }

        /// <summary>Builds the dialog, when it opens and after every domain reload.</summary>
        public void CreateGUI()
        {
            VisualElement root = SettingsLook.Dialog(rootVisualElement);

            var name = new TextField("Name") { value = _name, maxLength = FilterName.MostLength, tooltip = "The name its button shows, which no other saved filter has." };
            name.RegisterValueChangedCallback(changed =>
            {
                _name = changed.newValue ?? "";
                Check();
            });
            root.Add(name);

            var query = new TextField("Query") { value = _query, tooltip = "The query its button shows the rows of." };
            query.RegisterValueChangedCallback(changed =>
            {
                _query = changed.newValue ?? "";
                Check();
            });
            root.Add(query);

            var colour = new ColorField("Colour") { value = _colour, showAlpha = false };
            colour.SetEnabled(_chosen);
            colour.RegisterValueChangedCallback(changed =>
            {
                _colour = changed.newValue;
                Check();
            });

            var hue = new Toggle("Use the Name's Colour") { value = !_chosen, tooltip = "Draws it in a hue made of its name, as a category is drawn." };
            hue.RegisterValueChangedCallback(changed =>
            {
                _chosen = !changed.newValue;
                colour.SetEnabled(_chosen);
                Check();
            });
            root.Add(hue);
            root.Add(colour);

            var refused = new Label();
            refused.AddToClassList("dialog-refused");
            root.Add(refused);
            _refused = refused;

            var save = new Button(Save) { text = "Save" };
            _save = save;
            root.Add(SettingsLook.Buttons(new Button(Close) { text = "Cancel" }, save));
            root.RegisterCallback<KeyDownEvent>(key => SettingsLook.Keys(key, Save, Close), TrickleDown.TrickleDown);
            Check();
            name.Focus();
        }

        /// <summary>The filter as the dialog holds it, or why it is not one.</summary>
        private Result<SavedFilter> Draft() =>
            SavedFilter.Of(_name, _query, _chosen ? F.Some(ChosenColour.Of(_colour)) : F.None);

        /// <summary>What saving <paramref name="filter"/> makes of what the team shares: a new filter, or the one edited, renamed or not.</summary>
        private Func<SharedSettings, Result<SharedSettings>> Saving(SavedFilter filter)
        {
            string original = _original;
            return shared => original.Length == 0
                ? shared.Saving(filter)
                : FilterName.Of(original).Bind(name => shared.Replacing(name, filter));
        }

        /// <summary>Says why the filter cannot be saved as it is, and lets Save be pressed only when it can.</summary>
        private void Check()
        {
            Result<SharedSettings> saved = Draft().Bind(filter => Saving(filter)(ConsoleSettings.Shared));
            Optional<string> refused = saved.Match<Optional<string>>(static error => F.Some(SaidLines.Closed(error.Message)), static _ => F.None);
            Show(refused);
            if (_save.HasValue(out Button save))
                save.SetEnabled(refused.IsNone);
        }

        private void Save()
        {
            Draft().Bind(filter => ConsoleSettings.ChangeShared(Saving(filter))).Match(
                refused => Show(F.Some(SaidLines.Closed(refused.Message))),
                _ => Close());
        }

        private void Show(Optional<string> refused)
        {
            if (_refused.HasValue(out Label label))
                label.text = refused.Or("");
        }
    }

    /// <summary>
    /// The dialog that chooses the colour a category is drawn in, and the categories under it that have
    /// none of their own, for the team. What it holds is in serialized fields, so a domain reload keeps it.
    /// </summary>
    internal sealed class CategoryColourWindow : EditorWindow
    {
        [SerializeField] private string _category = "";
        [SerializeField] private Color _colour = Color.white;

        private Optional<Button> _choose;
        private Optional<Label> _refused;

        /// <summary>Opens the dialog that chooses the colour of <paramref name="category"/>, now drawn in <paramref name="colour"/>.</summary>
        public static void Open(CategoryName category, Color colour)
        {
            if (category is null)
                throw new ArgumentNullException(nameof(category));

            Open(category.Value, colour);
        }

        /// <summary>Opens the dialog that chooses the colour of a category the developer names in it.</summary>
        public static void OpenForAny() => Open("", Color.white);

        private static void Open(string category, Color colour)
        {
            var window = CreateInstance<CategoryColourWindow>();
            window.titleContent = new GUIContent("Choose a Category's Colour");
            window._category = category;
            window._colour = colour;
            window.minSize = new Vector2(400, 110);
            window.ShowUtility();
        }

        /// <summary>Builds the dialog, when it opens and after every domain reload.</summary>
        public void CreateGUI()
        {
            VisualElement root = SettingsLook.Dialog(rootVisualElement);

            var category = new TextField("Category") { value = _category, tooltip = "The category, as game.save: it and the categories under it that have no colour of their own are drawn in the colour." };
            category.RegisterValueChangedCallback(changed =>
            {
                _category = changed.newValue ?? "";
                Check();
            });
            root.Add(category);

            var colour = new ColorField("Colour") { value = _colour, showAlpha = false };
            colour.RegisterValueChangedCallback(changed => _colour = changed.newValue);
            root.Add(colour);

            var refused = new Label();
            refused.AddToClassList("dialog-refused");
            root.Add(refused);
            _refused = refused;

            var choose = new Button(Choose) { text = "Choose" };
            _choose = choose;
            root.Add(SettingsLook.Buttons(new Button(Close) { text = "Cancel" }, choose));
            root.RegisterCallback<KeyDownEvent>(key => SettingsLook.Keys(key, Choose, Close), TrickleDown.TrickleDown);
            Check();
            if (_category.Length == 0)
                category.Focus();
        }

        /// <summary>Says why the category is not one, and lets Choose be pressed only when it is.</summary>
        private void Check()
        {
            Optional<string> refused = CategoryName.Of(_category).Match<Optional<string>>(static error => F.Some(SaidLines.Closed(error.Message)), static _ => F.None);
            Show(refused);
            if (_choose.HasValue(out Button choose))
                choose.SetEnabled(refused.IsNone);
        }

        private void Choose()
        {
            ChosenColour colour = ChosenColour.Of(_colour);
            CategoryName.Of(_category)
                .Bind(category => ConsoleSettings.ChangeShared(shared => shared.Colouring(shared.Colours.Choosing(category, colour))))
                .Match(refused => Show(F.Some(SaidLines.Closed(refused.Message))), _ => Close());
        }

        private void Show(Optional<string> refused)
        {
            if (_refused.HasValue(out Label label))
                label.text = refused.Or("");
        }
    }

    /// <summary>What the console's dialogs and its settings page look alike in.</summary>
    internal static class SettingsLook
    {
        /// <summary><paramref name="root"/> made a dialog's: emptied, with the console's stylesheet and skin.</summary>
        public static VisualElement Dialog(VisualElement root)
        {
            Styled(root);
            root.AddToClassList("log-console-dialog");
            return root;
        }

        /// <summary><paramref name="root"/> emptied, with the console's stylesheet, and its variables for the editor's skin.</summary>
        public static VisualElement Styled(VisualElement root)
        {
            root.Clear();
            StyleSheet? style = AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(LogConsoleWindow.StyleGuid));
            if (style != null && !root.styleSheets.Contains(style))
                root.styleSheets.Add(style);
            root.AddToClassList("log-console");
            root.EnableInClassList("dark", EditorGUIUtility.isProSkin);
            return root;
        }

        /// <summary>A dialog's row of buttons, at its right: <paramref name="cancel"/>, then <paramref name="act"/>, which Enter presses.</summary>
        public static VisualElement Buttons(Button cancel, Button act)
        {
            var buttons = new VisualElement();
            buttons.AddToClassList("dialog-buttons");
            buttons.Add(cancel);
            buttons.Add(act);
            return buttons;
        }

        /// <summary>Enter does what <paramref name="act"/> does, and Escape what <paramref name="cancel"/> does.</summary>
        public static void Keys(KeyDownEvent key, Action act, Action cancel)
        {
            if (key.keyCode is KeyCode.Return or KeyCode.KeypadEnter)
            {
                act();
                key.StopPropagation();
            }
            else if (key.keyCode == KeyCode.Escape)
            {
                cancel();
                key.StopPropagation();
            }
        }
    }
}
