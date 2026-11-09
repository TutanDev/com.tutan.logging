using System.Collections.ObjectModel;
using System.Linq;
using Tutan.Functional;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace Tutan.Logging.Editor
{
    internal sealed partial class LogConsoleWindow
    {
        private ListView MakeCategories()
        {
            var categories = new ListView
            {
                itemsSource = Tree.Nodes,
                makeItem = MakeCategory,
                bindItem = BindCategory,
                fixedItemHeight = 18,
                virtualizationMethod = CollectionVirtualizationMethod.FixedHeight,
                selectionType = SelectionType.None,
                tooltip = "Click: shows or hides a category and all under it. Ctrl+click: shows only it, or every category again. Shift+click: shows every category.",
            };
            categories.AddToClassList("categories");
            return categories;
        }

        private VisualElement MakeCategory()
        {
            var item = new CategoryElement();
            item.RegisterCallback<ClickEvent>(click =>
            {
                // The right button opens the category's menu, and presses nothing.
                if (click.button == 0)
                    PressCategory(item.Index, click);
            });
            item.AddManipulator(new ContextualMenuManipulator(menu => CategoryMenu(item.Index, menu)));
            return item;
        }

        /// <summary>The menu of the category at <paramref name="index"/>: the colour the team draws it in.</summary>
        private void CategoryMenu(int index, ContextualMenuPopulateEvent menu)
        {
            CategoryTree tree = Tree;
            if (index < 0 || index >= tree.Nodes.Count || !CategoryName.Of(tree.Nodes[index].Category).IsSuccess(out CategoryName category))
            {
                menu.menu.AppendAction("Choose Colour...", static _ => { }, DropdownMenuAction.Status.Disabled);
                menu.menu.AppendAction("Use the Name's Colour", static _ => { }, DropdownMenuAction.Status.Disabled);
                return;
            }

            menu.menu.AppendAction("Choose Colour...", _ => CategoryColourWindow.Open(category, Colour(category.Value)));
            DropdownMenuAction.Status chosen = ConsoleSettings.Shared.Colours.IsChosen(category) ? DropdownMenuAction.Status.Normal : DropdownMenuAction.Status.Disabled;
            menu.menu.AppendAction("Use the Name's Colour", _ => ChangeShared(shared => shared.Colouring(shared.Colours.Unchoosing(category))), chosen);
        }

        private void BindCategory(VisualElement element, int index)
        {
            var item = (CategoryElement)element;
            item.Index = index;
            CategoryTree tree = Tree;
            if (index >= tree.Nodes.Count)
            {
                item.Name.text = "";
                item.DrawCount(F.None);
                return;
            }

            CategoryNode node = tree.Nodes[index];
            CategoryShown shown = tree.Shown(node, _state.Filter);
            item.Indent.style.width = node.Depth * 12f;
            item.Mark.EnableInClassList("all", shown == CategoryShown.All);
            item.Mark.EnableInClassList("some", shown == CategoryShown.Some);
            item.EnableInClassList("hidden", shown == CategoryShown.None);
            item.Name.text = node.Label;
            item.Name.style.color = Colour(node.Category);
            item.tooltip = node.Category;
            item.DrawCount(_bound.HasValue(out Bound bound) ? F.Some(tree.Count(node, bound.View)) : F.None);
        }

        private void PressCategory(int index, ClickEvent click)
        {
            CategoryTree tree = Tree;
            if (index < 0 || index >= tree.Nodes.Count)
                return;

            CategoryPress press = click.actionKey ? CategoryPress.Only : click.shiftKey ? CategoryPress.Everything : CategoryPress.Toggle;
            Apply(_state.PressCategory(tree, tree.Nodes[index], press));
        }

        private static void DrawCategories(Parts parts) => parts.Categories.RefreshItems();

        /// <summary>A badge for each level, in the order of <see cref="LevelLook.Shown"/>.</summary>
        private ReadOnlyCollection<ToolbarButton> MakeBadges() => System.Array.AsReadOnly(LevelLook.Shown.Select(MakeBadge).ToArray());

        private ToolbarButton MakeBadge(LogLevel level)
        {
            var badge = new ToolbarButton(() => Apply(_state.PressBadge(level)));
            badge.AddToClassList("badge");
            badge.AddToClassList(LevelLook.Class(level));
            return badge;
        }

        /// <summary>Draws the badges, when the counts or the levels kept changed since they were drawn.</summary>
        private void DrawBadges(Parts parts, Optional<RowsMarks> marks)
        {
            BadgesMark mark = marks.HasValue(out RowsMarks of) ? of.Badges : new BadgesMark(default, _state.Filter.Levels);
            bool wasDrawn = parts.BadgesDrawn.HasValue(out BadgesMark drawn);
            if (wasDrawn && drawn == mark)
                return;

            parts.BadgesDrawn = mark;
            for (int i = 0; i < LevelLook.Shown.Count; i++)
            {
                // A record changes one level's count, so only that badge is drawn again.
                LogLevel level = LevelLook.Shown[i];
                long count = mark.Counts[level];
                bool on = mark.Levels.Contains(level);
                if (wasDrawn && drawn.Counts[level] == count && drawn.Levels.Contains(level) == on)
                    continue;

                ToolbarButton badge = parts.Badges[i];
                badge.text = LevelLook.Badge(level, count);
                badge.EnableInClassList("on", on);
                badge.EnableInClassList("zero", count == 0);
                badge.tooltip = LevelLook.BadgeTip(level, count);
            }
        }

        /// <summary>A category of the list: an indent, a mark of how much of it is shown, its name, and its count.</summary>
        private sealed class CategoryElement : VisualElement
        {
            public CategoryElement()
            {
                AddToClassList("category-item");
                Indent.AddToClassList("indent");
                Mark.AddToClassList("mark");
                Name.AddToClassList("name");
                Count.AddToClassList("count");
                Add(Indent);
                Add(Mark);
                Add(Name);
                Add(Count);
            }

            public int Index { get; set; } = -1;

            public VisualElement Indent { get; } = new VisualElement();

            public VisualElement Mark { get; } = new VisualElement();

            public Label Name { get; } = new Label();

            public Label Count { get; } = new Label();

            /// <summary>The count last drawn: none for none, as the item was made.</summary>
            private Optional<long> CountDrawn { get; set; }

            /// <summary>Draws <paramref name="count"/>, or nothing for none, when it is not the count drawn: the list draws every item again as records arrive.</summary>
            public void DrawCount(Optional<long> count)
            {
                if (CountDrawn == count)
                    return;

                CountDrawn = count;
                Count.text = count.HasValue(out long shown) ? CountText.Compact(shown) : "";
            }
        }
    }
}
