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
        // The properties of a column whose change is a change of the layout: what Unity's Column raises them by.
        private static readonly BindingId s_widthProperty = nameof(Column.width);
        private static readonly BindingId s_visibleProperty = nameof(Column.visible);

        // What the header asked of the columns since the last tick, in turn: made one change of the
        // settings on the next tick, so a gesture is written once, and nothing when they net to the file's.
        private readonly List<ColumnAsked> _columnsAsked = new List<ColumnAsked>();

        // Whether the window is drawing a layout onto the columns: what it sets then asks nothing.
        private bool _drawingColumns;

        /// <summary>
        /// The rows, a column each of <see cref="ConsoleColumns.All"/>, made as <paramref name="layout"/>
        /// has them before any listens, so making them asks nothing: a list that never sorts and whose
        /// columns stay in their order, with no view data of its own, as the personal file keeps the layout.
        /// </summary>
        private MultiColumnListView MakeRows(ColumnLayout layout)
        {
            var rows = new MultiColumnListView
            {
                itemsSource = new RowIndexes(() => _bound.HasValue(out Bound bound) ? bound.View.Count : 0),
                fixedItemHeight = 20,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                selectionType = SelectionType.Multiple,
                sortingMode = ColumnSortingMode.None,
                horizontalScrollingEnabled = true,
            };
            rows.AddToClassList("rows");
            rows.columns.reorderable = false;
            rows.columns.resizePreview = true;
            rows.columns.stretchMode = Columns.StretchMode.GrowAndFill;
            ReadOnlyCollection<ConsoleColumn> all = ConsoleColumns.All;
            for (int i = 0; i < all.Count; i++)
                rows.columns.Add(MakeColumn(all[i], layout));

            // Joined last, once every column is as the layout has it; the one that fills asks nothing, so it is not joined.
            for (int i = 0; i < all.Count; i++)
            {
                ConsoleColumn column = all[i];
                if (ConsoleColumns.Fills(column))
                    continue;

                Column made = rows.columns[i];
                made.propertyChanged += (_, changed) => ColumnChanged(column, made, changed.propertyName);
            }

            rows.selectedIndicesChanged += Selected;
            rows.itemsChosen += Chosen;

            // On the rows' viewport, not the list, so a right-click on the header shows Unity's menu of the columns alone.
            ScrollView scroll = rows.Q<ScrollView>();
            scroll.contentViewport.AddManipulator(new ContextualMenuManipulator(RowMenu));
            rows.RegisterCallback<ValidateCommandEvent>(static command =>
            {
                if (command.commandName == "Copy")
                    command.StopPropagation();
            });
            rows.RegisterCallback<ExecuteCommandEvent>(command =>
            {
                if (command.commandName != "Copy")
                    return;

                Copy(json: false, Redacted);
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
            Scroller scroller = scroll.verticalScroller;
            scroller.RegisterCallback<PointerDownEvent>(_ => Apply(_state.Scrolled(toNewest: false)), TrickleDown.TrickleDown);
            scroller.RegisterCallback<PointerUpEvent>(_ => rows.schedule.Execute(() => FollowAtNewest(rows)), TrickleDown.TrickleDown);
            return rows;
        }

        /// <summary>
        /// The column of <paramref name="column"/>, as <paramref name="layout"/> has it and within its fit's
        /// bounds, so Unity holds it to no bounds of its own: the one that fills stretches, and is never hidden or resized.
        /// </summary>
        private Column MakeColumn(ConsoleColumn column, ColumnLayout layout)
        {
            Column made = ConsoleColumns.Fit(column).Match(
                static fills => new Column { stretchable = true, optional = false, resizable = false, minWidth = fills.Least, width = fills.Least },
                sized => new Column
                {
                    stretchable = false,
                    optional = true,
                    resizable = true,
                    minWidth = sized.Least,
                    maxWidth = sized.Most,
                    width = layout.Width(column).Or(sized.Default),
                });
            made.name = ConsoleColumns.Name(column);
            made.title = ConsoleColumns.Title(column);
            made.sortable = false;
            made.visible = layout.Shows(column);
            switch (column)
            {
                case ConsoleColumn.Message:
                    made.makeCell = MakeMessageCell;
                    made.bindCell = BindMessageCell;
                    made.unbindCell = static (cell, _) => ((MessageCell)cell).Scope = F.None;
                    break;
                case ConsoleColumn.Category:
                    made.makeCell = () => MakeTextCell(column);
                    made.bindCell = BindCategoryCell;
                    break;
                default:
                    made.makeCell = () => MakeTextCell(column);
                    made.bindCell = (cell, index) => BindTextCell(column, cell, index);
                    break;
            }

            return made;
        }

        /// <summary>
        /// A column's width, or whether it is shown, changed: kept as asked, unless the window set it as it
        /// drew a layout. What is kept is the column as the header left it, so the tick writes what is shown.
        /// </summary>
        private void ColumnChanged(ConsoleColumn column, Column changed, BindingId property)
        {
            if (_drawingColumns)
                return;

            if (property.Equals(s_widthProperty) && changed.width.unit == LengthUnit.Pixel)
                _columnsAsked.Add(new ColumnAsked.Sizing(column, changed.width.value));
            else if (property.Equals(s_visibleProperty))
                _columnsAsked.Add(new ColumnAsked.Showing(column, changed.visible));
        }

        /// <summary>
        /// Makes what the header asked since the last tick one change of the settings, to the file as read
        /// again: written, every window draws it; nothing written, as asks that net to the file's, or
        /// refused, this one draws the layout held. A tick with nothing asked allocates nothing.
        /// </summary>
        private void TakeColumnsAsked()
        {
            if (_columnsAsked.Count == 0)
                return;

            // Cleared first: the change draws the layout onto these columns, and what is kept is taken now.
            ColumnAsked[] asked = _columnsAsked.ToArray();
            _columnsAsked.Clear();
            ChangeColumns(asked);
        }

        // Apart from TakeColumnsAsked, so the closure over what was asked is made only when something was.
        private void ChangeColumns(IReadOnlyList<ColumnAsked> asked) =>
            ChangePersonal(personal => personal with { Columns = personal.Columns.Taking(asked) });

        /// <summary>
        /// Draws <paramref name="layout"/> onto the rows' columns: each shown or hidden, and as wide, as it
        /// holds. What it sets asks nothing, so drawing never writes; a column already so is not changed.
        /// </summary>
        private void DrawColumns(Parts parts, ColumnLayout layout)
        {
            _drawingColumns = true;
            try
            {
                ReadOnlyCollection<ConsoleColumn> all = ConsoleColumns.All;
                for (int i = 0; i < all.Count; i++)
                {
                    ConsoleColumn column = all[i];
                    Column drawn = parts.Rows.columns[i];
                    drawn.visible = layout.Shows(column);
                    if (layout.Width(column).HasValue(out int width))
                        drawn.width = width;
                }
            }
            finally
            {
                _drawingColumns = false;
            }
        }

        private static Label MakeTextCell(ConsoleColumn column)
        {
            var cell = new Label();
            cell.AddToClassList("cell");
            cell.AddToClassList(ConsoleColumns.Name(column));
            return cell;
        }

        private VisualElement MakeMessageCell()
        {
            var cell = new MessageCell();
            cell.Fold.RegisterCallback<ClickEvent>(click =>
            {
                if (!cell.Scope.HasValue(out long scope))
                    return;

                Apply(_state.ToggleFold(scope));
                click.StopPropagation();
            });
            return cell;
        }

        /// <summary>The cells of the row at <paramref name="index"/>: none for an index the rows no longer have.</summary>
        private Optional<RowCells> CellsAt(int index) =>
            _bound.HasValue(out Bound bound) && index < bound.View.Count ? F.Some(bound.Cells(index)) : F.None;

        private void BindTextCell(ConsoleColumn column, VisualElement element, int index) => DrawText(column, (Label)element, CellsAt(index));

        /// <summary>The category's cell, drawn in its category's colour while the session holds its record.</summary>
        private void BindCategoryCell(VisualElement element, int index)
        {
            Optional<RowCells> shown = CellsAt(index);
            DrawText(ConsoleColumn.Category, (Label)element, shown);
            element.style.color = shown.HasValue(out RowCells cells) && cells.Held ? new StyleColor(Colour(cells.Category)) : new StyleColor(StyleKeyword.Null);
        }

        /// <summary>Draws in <paramref name="cell"/> what <paramref name="shown"/> says of <paramref name="column"/>, and its row's classes: nothing for no row.</summary>
        private static void DrawText(ConsoleColumn column, Label cell, Optional<RowCells> shown)
        {
            if (!shown.HasValue(out RowCells cells))
            {
                cell.text = "";
                cell.tooltip = "";
                LeaveRow(cell);
                return;
            }

            cell.text = cells.Text(column);
            cell.tooltip = cells.Tip(column);
            DrawRow(cell, cells);
        }

        private void BindMessageCell(VisualElement element, int index)
        {
            var cell = (MessageCell)element;
            if (!CellsAt(index).HasValue(out RowCells cells))
            {
                cell.Blank();
                LeaveRow(cell);
                return;
            }

            LogRow at = cells.Row;
            cell.Lost.text = cells.LostMark;
            cell.Lost.tooltip = cells.LostBefore;
            cell.Indent.style.width = at.Depth * 14f;
            cell.Fold.text = cells.Fold;
            cell.Fold.tooltip = cells.FoldTip;
            cell.Scope = at.Kind == LogRowKind.Scope ? F.Some(at.Sequence) : F.None;
            cell.Line.text = cells.Message;
            cell.Repeats.text = cells.Repeats;
            cell.Suppressed.text = cells.Suppressed;
            cell.Suppressed.tooltip = cells.SuppressedTip;
            DrawRow(cell, cells);
        }

        /// <summary>Gives <paramref name="cell"/> its row's classes, the stylesheet's: its level's, and whether it is context, gone, or a scope's.</summary>
        private static void DrawRow(VisualElement cell, RowCells cells)
        {
            LogRow at = cells.Row;
            for (int i = 0; i < LevelLook.Shown.Count; i++)
                cell.EnableInClassList(LevelLook.Class(LevelLook.Shown[i]), at.Level == LevelLook.Shown[i]);
            cell.EnableInClassList("context", cells.Context);
            cell.EnableInClassList("gone", !cells.Held);
            cell.EnableInClassList("scope", at.Kind == LogRowKind.Scope);
        }

        /// <summary>Takes from <paramref name="cell"/> every class of a row, for an index the rows no longer have.</summary>
        private static void LeaveRow(VisualElement cell)
        {
            for (int i = 0; i < LevelLook.Shown.Count; i++)
                cell.RemoveFromClassList(LevelLook.Class(LevelLook.Shown[i]));
            cell.RemoveFromClassList("context");
            cell.RemoveFromClassList("gone");
            cell.RemoveFromClassList("scope");
        }

        /// <summary>The colour <paramref name="category"/> is drawn in: the one the team chose for it, or its name's hue for the skin the parts are drawn for.</summary>
        private Color Colour(string category) =>
            Look.Category(category, ConsoleSettings.Shared.Colours, _parts.HasValue(out Parts parts) ? parts.Dark : EditorGUIUtility.isProSkin);

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
            menu.menu.AppendAction("Copy", _ => Copy(json: false, Redacted), copy);
            menu.menu.AppendAction("Copy as JSON Lines", _ => Copy(json: true, Redacted), copy);
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
        private void FollowAtNewest(MultiColumnListView rows)
        {
            Scroller scroller = rows.Q<ScrollView>().verticalScroller;
            if (scroller.highValue - scroller.value < 1)
                Apply(_state.Scrolled(toNewest: true));
        }

        /// <summary>
        /// The message's cell: what a row shows of itself around its message, each a label the stylesheet
        /// lays out - the mark of records lost before it, the indent under its scopes, the fold, the
        /// message's first line, how many it repeats, and how many a limit dropped.
        /// </summary>
        private sealed class MessageCell : VisualElement
        {
            public MessageCell()
            {
                AddToClassList("cell");
                AddToClassList(ConsoleColumns.Name(ConsoleColumn.Message));
                Indent.AddToClassList("indent");
                Add(Lost);
                Add(Indent);
                Add(Fold);
                Add(Line);
                Add(Repeats);
                Add(Suppressed);
            }

            public Label Lost { get; } = Part("lost");

            public VisualElement Indent { get; } = new VisualElement();

            public Label Fold { get; } = Part("fold");

            public Label Line { get; } = Part("line");

            public Label Repeats { get; } = Part("repeats");

            public Label Suppressed { get; } = Part("suppressed");

            /// <summary>The scope the fold folds, the sequence of its opening: none for a row that is not a scope's, and once the cell is unbound.</summary>
            public Optional<long> Scope { get; set; }

            /// <summary>Draws nothing, for a row the rows no longer have: no text or tip, and no scope to fold.</summary>
            public void Blank()
            {
                Scope = F.None;
                Blank(Lost);
                Indent.style.width = 0f;
                Blank(Fold);
                Blank(Line);
                Blank(Repeats);
                Blank(Suppressed);
            }

            private static void Blank(Label part)
            {
                part.text = "";
                part.tooltip = "";
            }

            private static Label Part(string name)
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
