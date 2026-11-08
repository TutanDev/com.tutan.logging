using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Tutan.Functional;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine.UIElements;

namespace Tutan.Logging.Editor
{
    internal sealed partial class LogConsoleWindow
    {
        // How many lines of source the details show on each side of a call site.
        private const int SourceAround = 3;

        // The record of the row focused, read into here.
        private readonly LogBatch _batch = new LogBatch(1);

        // The source files read, by path, with when each was written, so a file is read again only when it changed.
        private readonly Dictionary<string, (DateTime Written, string[] Lines)> _sources =
            new Dictionary<string, (DateTime Written, string[] Lines)>(StringComparer.Ordinal);

        /// <summary>The details, drawn of no row focused, as the parts are until a row is.</summary>
        private static ScrollView MakeDetails()
        {
            var details = new ScrollView(ScrollViewMode.Vertical);
            details.AddToClassList("details");
            DrawNothingFocused(details);
            return details;
        }

        /// <summary>The details of the row at <paramref name="index"/>: none when the session no longer holds its record.</summary>
        private Optional<RecordDetails> Details(int index)
        {
            if (!_bound.HasValue(out Bound bound) || index < 0 || index >= bound.View.Count)
                return F.None;

            LogView view = bound.View;
            LogRow row = view[index];
            if (view.Read(index, 1, _batch) != 1)
                return F.None;

            // The openings are read into a batch of the chain's own, as the record lives in the one it was read into.
            LogRecord record = _batch[0];
            ReadOnlyCollection<ScopeShown> scopes = ScopeShown.Chain(view, record.Scope);
            return RecordDetails.Of(record, row, view.Source.Clock, scopes, Paths);
        }

        /// <summary>
        /// Draws the details of the row focused, when what they are of changed since they were drawn,
        /// as its key says: the details are made only then, not as each record arrives.
        /// </summary>
        private void DrawDetails(Parts parts, Optional<RowsMarks> marks)
        {
            Optional<FocusMark> focus = marks.HasValue(out RowsMarks of) ? of.Focus : F.None;
            Optional<DetailsKey> key = focus.HasValue(out FocusMark focused) ? F.Some(focused.Details) : F.None;
            if (parts.DetailsDrawn == key)
                return;

            parts.DetailsDrawn = key;
            ScrollView details = parts.Details;
            details.Clear();
            if (focus.IsNone)
                DrawNothingFocused(details);
            else if (focused.Details.Held.IsSome && Details(focused.Index).HasValue(out RecordDetails shown))
                DrawRecord(details, shown);
            else
                details.Add(Text("The session no longer holds this row's record: it was overwritten since.", "hint"));
        }

        private static void DrawNothingFocused(ScrollView details) =>
            details.Add(Text("Select a row to see its record: its fields, the scopes it is under, where it was written, and the source around it.", "hint"));

        private void DrawRecord(ScrollView details, RecordDetails shown)
        {
            var header = new VisualElement();
            header.AddToClassList("header");
            header.Add(Text(TextLinesSink.Short(shown.Level), "level", LevelLook.Class(shown.Level)));
            header.Add(Selectable(shown.Key, "key"));
            details.Add(header);
            details.Add(Text(shown.Stamp, "meta"));
            foreach (string note in shown.Notes)
                details.Add(Text(note, "note"));

            details.Add(Selectable(shown.Message, "message"));
            DrawFields(details, shown.Fields);
            DrawScopes(details, shown.Scopes);
            if (shown.CallSite.HasValue(out SourcePlace site))
            {
                details.Add(Text("Written at", "section"));
                details.Add(Link(site.ToString(), () => OpenFile(site), "Opens the file at this line."));
                DrawSource(details, site);
            }

            if (shown.Declaration.HasValue(out SourcePlace declared))
            {
                details.Add(Text("Declared at", "section"));
                details.Add(Link(declared.ToString(), () => OpenFile(declared), "Opens the file at the event's declaration."));
            }

            DrawStack(details, shown.Stack);
        }

        private static void DrawFields(ScrollView details, ReadOnlyCollection<FieldShown> fields)
        {
            if (fields.Count == 0)
                return;

            details.Add(Text("Fields", "section"));
            foreach (FieldShown field in fields)
            {
                var line = new VisualElement();
                line.AddToClassList("field");
                line.Add(Text(field.Name, "field-name"));
                line.Add(Selectable(field.Value, "field-value"));
                if (field.Personal)
                    line.Add(Text("personal", "tag"));
                details.Add(line);
            }
        }

        private void DrawScopes(ScrollView details, ReadOnlyCollection<ScopeShown> scopes)
        {
            if (scopes.Count == 0)
                return;

            details.Add(Text("Under", "section"));
            foreach (ScopeShown scope in scopes)
            {
                long id = scope.Scope;
                details.Add(Link(scope.Line, () => Apply(_state.FilterToScope(id)),
                    "Shows only this scope's records, and those under it."));
            }
        }

        private void DrawSource(ScrollView details, SourcePlace place)
        {
            if (!Source(place).HasValue(out string[] lines))
                return;

            ReadOnlyCollection<SourceLine> around = SourceLines.Around(lines, place.Line, SourceAround);
            if (around.Count == 0)
                return;

            var source = new VisualElement();
            source.AddToClassList("source");
            source.tooltip = "Opens the file at this line.";
            source.RegisterCallback<ClickEvent>(_ => OpenFile(place));
            foreach (SourceLine line in around)
            {
                var row = new VisualElement();
                row.AddToClassList("source-line");
                row.EnableInClassList("current", line.Number == place.Line);
                row.Add(Text(line.Number.ToString(CultureInfo.InvariantCulture), "number"));
                row.Add(Text(line.Text, "code"));
                source.Add(row);
            }

            details.Add(source);
        }

        private static void DrawStack(ScrollView details, ReadOnlyCollection<StackFrame> stack)
        {
            if (stack.Count == 0)
                return;

            details.Add(Text("Stack", "section"));
            foreach (StackFrame frame in stack)
            {
                if (frame.At.HasValue(out SourcePlace at))
                    details.Add(Link($"{frame.Method}  ({at.Short})", () => OpenFile(at), at.ToString()));
                else
                    details.Add(Text(frame.Method, "frame"));
            }
        }

        /// <summary>The lines of the file at <paramref name="place"/>, read again only when it was written since: none when it cannot be read.</summary>
        private Optional<string[]> Source(SourcePlace place)
        {
            try
            {
                string path = Physical(place);
                DateTime written = File.GetLastWriteTimeUtc(path);
                if (_sources.TryGetValue(path, out (DateTime Written, string[] Lines) held) && held.Written == written)
                    return held.Lines;

                string[] lines = File.ReadAllLines(path);
                _sources[path] = (written, lines);
                return lines;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return F.None;
            }
        }

        /// <summary>Opens <paramref name="place"/> as Unity opens a script: in the project through the asset, and otherwise by its path.</summary>
        private static void OpenFile(SourcePlace place)
        {
            if (place.InProject)
            {
                UnityEngine.Object? asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(place.File);
                if (asset != null && AssetDatabase.OpenAsset(asset, place.Line))
                    return;
            }

            InternalEditorUtility.OpenFileAtLineExternal(Physical(place), place.Line);
        }

        /// <summary>Where the file at <paramref name="place"/> is on disk: a package's path in the project is not, until mapped.</summary>
        private static string Physical(SourcePlace place) =>
            place.InProject ? Path.GetFullPath(FileUtil.GetPhysicalPath(place.File)) : place.File;

        private static Label Text(string text, params string[] classes)
        {
            var label = new Label(text);
            foreach (string name in classes)
                label.AddToClassList(name);
            return label;
        }

        private static Label Selectable(string text, string name)
        {
            Label label = Text(text, name);
            label.selection.isSelectable = true;
            return label;
        }

        private static Label Link(string text, Action open, string tooltip)
        {
            Label label = Text(text, "link");
            label.tooltip = tooltip;
            label.RegisterCallback<ClickEvent>(_ => open());
            return label;
        }
    }
}
