using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using Tutan.Functional;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace Tutan.Logging.Editor
{
    internal sealed partial class LogConsoleWindow
    {
        private ListView MakeRows()
        {
            var rows = new ListView
            {
                itemsSource = new RowIndexes(() => _bound.HasValue(out Bound bound) ? bound.View.Count : 0),
                makeItem = MakeRow,
                bindItem = BindRow,
                fixedItemHeight = 20,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                selectionType = SelectionType.Multiple,
            };
            rows.AddToClassList("rows");
            rows.selectedIndicesChanged += Selected;
            rows.itemsChosen += Chosen;
            rows.AddManipulator(new ContextualMenuManipulator(RowMenu));
            rows.RegisterCallback<ValidateCommandEvent>(static command =>
            {
                if (command.commandName == "Copy")
                    command.StopPropagation();
            });
            rows.RegisterCallback<ExecuteCommandEvent>(command =>
            {
                if (command.commandName != "Copy")
                    return;

                Copy(json: false, LeftOut);
                command.StopPropagation();
            });

            // Scrolling up stops following, and scrolling to the newest row follows again.
            rows.RegisterCallback<WheelEvent>(wheel =>
            {
                if (wheel.delta.y < 0)
                    Apply(_state.Scrolled(toNewest: false));
                else
                    rows.schedule.Execute(() => FollowAtNewest(rows));
            }, TrickleDown.TrickleDown);
            Scroller scroller = rows.Q<ScrollView>().verticalScroller;
            scroller.RegisterCallback<PointerDownEvent>(_ => Apply(_state.Scrolled(toNewest: false)), TrickleDown.TrickleDown);
            scroller.RegisterCallback<PointerUpEvent>(_ => rows.schedule.Execute(() => FollowAtNewest(rows)), TrickleDown.TrickleDown);
            return rows;
        }

        private VisualElement MakeRow()
        {
            var row = new RowElement();
            row.Fold.RegisterCallback<ClickEvent>(click =>
            {
                if (!row.Scope.HasValue(out long scope))
                    return;

                Apply(_state.ToggleFold(scope));
                click.StopPropagation();
            });
            return row;
        }

        private void BindRow(VisualElement element, int index)
        {
            var row = (RowElement)element;
            if (!_bound.HasValue(out Bound bound) || index >= bound.View.Count)
            {
                row.Blank();
                return;
            }

            RowCells cells = bound.Cells(index);
            LogRow at = cells.Row;
            row.Time.text = cells.Time;
            row.Frame.text = cells.Frame;
            row.Level.text = TextLinesSink.Short(at.Level);
            for (int i = 0; i < LevelLook.Shown.Count; i++)
                row.EnableInClassList(LevelLook.Class(LevelLook.Shown[i]), at.Level == LevelLook.Shown[i]);

            bool scope = at.Kind == LogRowKind.Scope;
            row.Indent.style.width = at.Depth * 14f;
            row.Fold.text = cells.Fold;
            row.Fold.tooltip = cells.FoldTip;
            row.Scope = scope ? F.Some(at.Sequence) : F.None;
            row.Category.text = cells.Category;
            row.Category.style.color = cells.Held ? new StyleColor(Colour(cells.Category)) : new StyleColor(StyleKeyword.Null);
            row.Message.text = cells.Message;
            row.Duration.text = cells.Duration;
            row.Repeats.text = cells.Repeats;
            row.Suppressed.text = cells.Suppressed;
            row.Suppressed.tooltip = cells.SuppressedTip;
            row.Lost.text = cells.LostMark;
            row.Lost.tooltip = cells.LostBefore;
            row.EnableInClassList("context", cells.Context);
            row.EnableInClassList("gone", !cells.Held);
            row.EnableInClassList("scope", scope);
        }

        /// <summary>The colour <paramref name="category"/> is drawn in, for the skin the parts are drawn for.</summary>
        private Color Colour(string category) =>
            CategoryHue.Colour(category, _parts.HasValue(out Parts parts) ? parts.Dark : EditorGUIUtility.isProSkin);

        /// <summary>The list selected <paramref name="indexes"/>: the state selects their rows, and the list shows them already.</summary>
        private void Selected(IEnumerable<int> indexes)
        {
            if (_bound.HasValue(out Bound bound))
                Apply(_state.Select(Selection.Of(bound.View, indexes)), fromList: true);
        }

        /// <summary>
        /// Selects in the list the rows the state selects, as they are now, the one whose details show
        /// last: only when the selection or where the rows are changed since it was drawn, so a record
        /// that only adds a row selects nothing again.
        /// </summary>
        /// <param name="parts">The window's parts.</param>
        /// <param name="marks">What the parts are drawn of, as the rows say: none while no session is bound.</param>
        /// <param name="listShowsIt">Whether the list made the selection, so it shows it already and only the mark is taken.</param>
        private void ShowSelection(Parts parts, Optional<RowsMarks> marks, bool listShowsIt = false)
        {
            SelectionMark mark = marks.HasValue(out RowsMarks of) ? of.Selection : new SelectionMark(_state.Selection, F.None);
            if (parts.SelectionDrawn.HasValue(out SelectionMark drawn) && drawn == mark)
                return;

            parts.SelectionDrawn = mark;
            if (!listShowsIt)
                parts.Rows.SetSelectionWithoutNotify(SelectedNow().FocusLast());
        }

        /// <summary>The rows the state selects, as the view has them now: none while no session is shown.</summary>
        private SelectedRows SelectedNow() =>
            _bound.HasValue(out Bound bound) ? SelectedRows.Of(_state.Selection, bound.View) : SelectedRows.Nothing;

        /// <summary>A row double-clicked, or Enter pressed on it: what it opens opens.</summary>
        private void Chosen(IEnumerable<object> items)
        {
            object? first = items.FirstOrDefault();
            if (first is int index && Details(index).Bind(static details => details.Opens).HasValue(out SourcePlace place))
                OpenFile(place);
        }

        private void RowMenu(ContextualMenuPopulateEvent menu)
        {
            SelectedRows selected = SelectedNow();
            DropdownMenuAction.Status copy = selected.Indexes.Count > 0 ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled;
            menu.menu.AppendAction("Copy", _ => Copy(json: false, LeftOut), copy);
            menu.menu.AppendAction("Copy as JSON Lines", _ => Copy(json: true, LeftOut), copy);
            menu.menu.AppendAction("Copy, Nothing Left Out/As Text", _ => Copy(json: false, LogRedaction.None), copy);
            menu.menu.AppendAction("Copy, Nothing Left Out/As JSON Lines", _ => Copy(json: true, LogRedaction.None), copy);
            menu.menu.AppendSeparator();

            // The focus is an index of the bound view's rows, so it is none while no view is bound.
            Optional<long> foldable = _bound.HasValue(out Bound bound)
                ? selected.Focus.Map(index => bound.View[index]).Filter(static row => row.Kind == LogRowKind.Scope).Map(static row => row.Sequence)
                : F.None;
            if (foldable.HasValue(out long scope))
                menu.menu.AppendAction(_state.Folded.Contains(scope) ? "Unfold" : "Fold", _ => Apply(_state.ToggleFold(scope)));
            else
                menu.menu.AppendAction("Fold", static _ => { }, DropdownMenuAction.Status.Disabled);

            Optional<RecordDetails> details = selected.Focus.Bind(Details);
            if (details.Bind(static shown => shown.ScopeToShow).HasValue(out long only))
                menu.menu.AppendAction("Show Only This Scope", _ => Apply(_state.FilterToScope(only)));
            else
                menu.menu.AppendAction("Show Only This Scope", static _ => { }, DropdownMenuAction.Status.Disabled);

            if (details.Bind(static shown => shown.Opens).HasValue(out SourcePlace place))
                menu.menu.AppendAction($"Open {MenuText(place.Short)}", _ => OpenFile(place));
            else
                menu.menu.AppendAction("Open the Call Site", static _ => { }, DropdownMenuAction.Status.Disabled);
        }

        /// <summary>Copies the rows selected to the clipboard, as text or as JSON lines, leaving out <paramref name="redact"/>.</summary>
        private void Copy(bool json, LogRedaction redact)
        {
            ReadOnlyCollection<int> rows = SelectedNow().Indexes;
            if (!_bound.HasValue(out Bound bound) || rows.Count == 0)
                return;

            var options = new LogSinkOptions(LogLevel.Trace, 0, redact);
            EditorGUIUtility.systemCopyBuffer = json ? AsJson(bound.View, rows, options) : AsText(bound.View, rows, options);
        }

        private static string AsText(LogView view, IReadOnlyList<int> rows, LogSinkOptions options)
        {
            var sink = new TextLinesSink();
            view.Write(sink, rows, options);
            return sink.Text;
        }

        private static string AsJson(LogView view, IReadOnlyList<int> rows, LogSinkOptions options)
        {
            using var stream = new MemoryStream();
            using (var sink = new JsonLinesSink(stream, leaveOpen: true, name: "clipboard"))
                view.Write(sink, rows, options);

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private void ScrollToNewest(Parts parts)
        {
            if (_bound.HasValue(out Bound bound) && bound.View.Count > 0)
                parts.Rows.ScrollToItem(-1);
        }

        /// <summary>Follows again when <paramref name="rows"/> were scrolled to the newest row.</summary>
        private void FollowAtNewest(ListView rows)
        {
            Scroller scroller = rows.Q<ScrollView>().verticalScroller;
            if (scroller.highValue - scroller.value < 1)
                Apply(_state.Scrolled(toNewest: true));
        }

        /// <summary>A row of the list: its cells, each a label the stylesheet lays out.</summary>
        private sealed class RowElement : VisualElement
        {
            public RowElement()
            {
                AddToClassList("row");
                Indent.AddToClassList("indent");
                Add(Lost);
                Add(Time);
                Add(Frame);
                Add(Level);
                Add(Indent);
                Add(Fold);
                Add(Category);
                Add(Message);
                Add(Duration);
                Add(Repeats);
                Add(Suppressed);
            }

            public Label Lost { get; } = Cell("lost");

            public Label Time { get; } = Cell("time");

            public Label Frame { get; } = Cell("frame");

            public Label Level { get; } = Cell("level");

            public VisualElement Indent { get; } = new VisualElement();

            public Label Fold { get; } = Cell("fold");

            public Label Category { get; } = Cell("category");

            public Label Message { get; } = Cell("message");

            public Label Duration { get; } = Cell("duration");

            public Label Repeats { get; } = Cell("repeats");

            public Label Suppressed { get; } = Cell("suppressed");

            /// <summary>The scope the fold folds, the sequence of its opening, or none for a row that is not a scope's.</summary>
            public Optional<long> Scope { get; set; }

            /// <summary>Draws nothing, for a row the rows no longer have: no text or tip, no scope to fold, and no look of a level or a kind.</summary>
            public void Blank()
            {
                Scope = F.None;
                foreach (VisualElement child in Children())
                {
                    if (child is Label label)
                    {
                        label.text = "";
                        label.tooltip = "";
                    }
                }

                Indent.style.width = 0f;
                Category.style.color = new StyleColor(StyleKeyword.Null);
                for (int i = 0; i < LevelLook.Shown.Count; i++)
                    RemoveFromClassList(LevelLook.Class(LevelLook.Shown[i]));
                RemoveFromClassList("context");
                RemoveFromClassList("gone");
                RemoveFromClassList("scope");
            }

            private static Label Cell(string name)
            {
                var label = new Label();
                label.AddToClassList(name);
                return label;
            }
        }

        /// <summary>The rows, by index, as a list view wants its items: how many there are is the view's.</summary>
        private sealed class RowIndexes : IList
        {
            private readonly Func<int> _count;

            public RowIndexes(Func<int> count) => _count = count;

            public int Count => _count();

            public bool IsFixedSize => false;

            public bool IsReadOnly => true;

            public bool IsSynchronized => false;

            public object SyncRoot => this;

            public object? this[int index]
            {
                get => index;
                set => throw new NotSupportedException("The rows are the view's.");
            }

            public int Add(object? value) => throw new NotSupportedException("The rows are the view's.");

            public void Clear() => throw new NotSupportedException("The rows are the view's.");

            public bool Contains(object? value) => value is int index && index >= 0 && index < Count;

            public int IndexOf(object? value) => Contains(value) ? (int)value! : -1;

            public void Insert(int index, object? value) => throw new NotSupportedException("The rows are the view's.");

            public void Remove(object? value) => throw new NotSupportedException("The rows are the view's.");

            public void RemoveAt(int index) => throw new NotSupportedException("The rows are the view's.");

            public void CopyTo(Array array, int index)
            {
                for (int i = 0; i < Count; i++)
                    array.SetValue(i, index + i);
            }

            public IEnumerator GetEnumerator()
            {
                for (int i = 0; i < Count; i++)
                    yield return i;
            }
        }
    }
}
