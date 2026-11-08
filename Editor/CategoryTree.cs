using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace Tutan.Logging.Editor
{
    /// <summary>How much of a category the filter's event terms show.</summary>
    internal enum CategoryShown
    {
        /// <summary>Every event of it and under it.</summary>
        All,

        /// <summary>Some of them, and not others.</summary>
        Some,

        /// <summary>None of them.</summary>
        None,
    }

    /// <summary>How a category was pressed.</summary>
    internal enum CategoryPress
    {
        /// <summary>A click: it and everything under it are hidden when all of it is shown, and shown when not.</summary>
        Toggle,

        /// <summary>A Ctrl+click: only it and everything under it are shown, or every category is when only it was.</summary>
        Only,

        /// <summary>A Shift+click: every category is shown.</summary>
        Everything,
    }

    /// <summary>The hue the console draws a category in.</summary>
    internal static class CategoryHue
    {
        /// <summary>
        /// The hue <paramref name="category"/> is drawn in, from 0 to 1: the same in every session and
        /// every run, and far from most others, since it is a hash of the name.
        /// </summary>
        public static float Of(string category)
        {
            if (category is null)
                throw new ArgumentNullException(nameof(category));

            // FNV-1a, which spreads names that differ by a letter.
            uint hash = 2166136261;
            foreach (char c in category)
                hash = unchecked((hash ^ c) * 16777619);

            return hash % 360 / 360f;
        }

        /// <summary>
        /// The colour <paramref name="category"/> is drawn in, of its hue: lighter on the dark skin,
        /// <paramref name="dark"/>, and deeper on the light one, so it reads on either.
        /// </summary>
        public static UnityEngine.Color Colour(string category, bool dark) =>
            UnityEngine.Color.HSVToRGB(Of(category), dark ? 0.45f : 0.7f, dark ? 0.9f : 0.55f);
    }

    /// <summary>A count as a badge or a category shows it.</summary>
    internal static class CountText
    {
        /// <summary><paramref name="count"/> in at most four or five characters: as it is under 10,000, then in thousands, then in millions.</summary>
        public static string Compact(long count) => count switch
        {
            < 10_000 => count.ToString(CultureInfo.InvariantCulture),
            < 10_000_000 => (count / 1_000).ToString(CultureInfo.InvariantCulture) + "k",
            _ => (count / 1_000_000).ToString(CultureInfo.InvariantCulture) + "M",
        };

        /// <summary>How many rows there are, whole, and whether a scan is reading more: <c>1,234 rows · reading</c>.</summary>
        public static string Rows(int count, bool scanning)
        {
            string rows = count == 1 ? "1 row" : $"{count.ToString("N0", CultureInfo.InvariantCulture)} rows";
            return scanning ? rows + " · reading" : rows;
        }
    }

    /// <summary>
    /// A category of a session's events: its dotted name, its last word, how deep it is, and the
    /// places in the session's events of its own events and of every event under it, its own among them.
    /// </summary>
    internal sealed record CategoryNode
    {
        internal CategoryNode(string category, int depth, ReadOnlyCollection<int> own, ReadOnlyCollection<int> under)
        {
            Category = category;
            Label = category.Substring(category.LastIndexOf('.') + 1);
            Depth = depth;
            Own = own;
            Under = under;
        }

        /// <summary>Its dotted name, as <c>game.save</c>.</summary>
        public string Category { get; }

        /// <summary>Its last word, as <c>save</c>, which is drawn under its parent: cut once, as the list draws it again as records arrive.</summary>
        public string Label { get; }

        /// <summary>How many categories it is under: 0 for <c>game</c>, 1 for <c>game.save</c>.</summary>
        public int Depth { get; }

        /// <summary>The places of the events of this category itself, from the first.</summary>
        public ReadOnlyCollection<int> Own { get; }

        /// <summary>The places of the events of it and of every category under it, from the first.</summary>
        public ReadOnlyCollection<int> Under { get; }

        /// <summary>Equal when they are of one category with the same events.</summary>
        public bool Equals(CategoryNode? other) =>
            other is not null && Category == other.Category && Depth == other.Depth
            && Own.SequenceEqual(other.Own) && Under.SequenceEqual(other.Under);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Category, Under.Count);
    }

    /// <summary>
    /// The categories of a session's events, as a tree drawn in order: each category before those
    /// under it, and those of one parent by name. A category no event is of is a node too when events
    /// are under it, as <c>game</c> is of <c>game.save</c>.
    /// </summary>
    /// <remarks>
    /// It knows the events it was made of; a session that declares more is a new tree.
    /// </remarks>
    internal sealed class CategoryTree
    {
        private readonly IReadOnlyList<LogEventSpec> _events;
        private readonly CategoryNode[] _nodes;

        // The place in the nodes past each node's subtree, which lies just after it.
        private readonly int[] _ends;
        private readonly Dictionary<string, int> _byCategory;

        private CategoryTree(IReadOnlyList<LogEventSpec> events, CategoryNode[] nodes, int[] ends)
        {
            _events = events;
            _nodes = nodes;
            _ends = ends;
            _byCategory = new Dictionary<string, int>(nodes.Length, StringComparer.Ordinal);
            for (int i = 0; i < nodes.Length; i++)
                _byCategory.Add(nodes[i].Category, i);

            Nodes = Array.AsReadOnly(nodes);
        }

        /// <summary>The categories, in the order drawn.</summary>
        public ReadOnlyCollection<CategoryNode> Nodes { get; }

        /// <summary>How many events it was made of, so a window knows when the session declared more.</summary>
        public int Events => _events.Count;

        /// <summary>The tree of the categories of <paramref name="events"/>, the events of a session's records by place.</summary>
        public static CategoryTree Of(IReadOnlyList<LogEventSpec> events)
        {
            if (events is null)
                throw new ArgumentNullException(nameof(events));

            LogEventSpec[] held = events.ToArray();
            ILookup<string, int> own = Enumerable.Range(0, held.Length).ToLookup(place => held[place].Category, StringComparer.Ordinal);

            // Ordinal order puts "game" before "game.save" and "game.save" before "game.saves": a
            // dot sorts before every letter and digit, so a subtree lies just after its category.
            string[] categories = held.SelectMany(static spec => AndAbove(spec.Category)).Distinct(StringComparer.Ordinal)
                .OrderBy(static category => category, StringComparer.Ordinal).ToArray();
            var nodes = new CategoryNode[categories.Length];
            var ends = new int[categories.Length];
            for (int i = categories.Length - 1; i >= 0; i--)
            {
                string category = categories[i];
                int end = i + 1;
                while (end < categories.Length && IsUnder(categories[end], category))
                    end = ends[end];

                // The node and everything under it are categories i to end.
                IEnumerable<int> under = Enumerable.Range(i, end - i).SelectMany(at => own[categories[at]]);

                int depth = category.Count(static c => c == '.');
                nodes[i] = new CategoryNode(category, depth, Array.AsReadOnly(own[category].ToArray()), Array.AsReadOnly(under.OrderBy(static place => place).ToArray()));
                ends[i] = end;
            }

            return new CategoryTree(held, nodes, ends);
        }

        /// <summary>How much of <paramref name="node"/> the event terms of <paramref name="filter"/> show.</summary>
        public CategoryShown Shown(CategoryNode node, LogFilter filter)
        {
            if (filter is null)
                throw new ArgumentNullException(nameof(filter));

            // A for over the places, as the window draws each category shown again as records arrive.
            int shown = 0;
            ReadOnlyCollection<int> under = Under(node);
            for (int i = 0; i < under.Count; i++)
            {
                if (filter.KeepsEvent(_events[under[i]]))
                    shown++;
            }

            return shown == under.Count ? CategoryShown.All : shown == 0 ? CategoryShown.None : CategoryShown.Some;
        }

        /// <summary>How many records of <paramref name="node"/> and under it <paramref name="view"/> counted: what showing it all would show.</summary>
        public long Count(CategoryNode node, LogView view)
        {
            if (view is null)
                throw new ArgumentNullException(nameof(view));

            long count = 0;
            ReadOnlyCollection<int> under = Under(node);
            for (int i = 0; i < under.Count; i++)
                count += view.EventCount(under[i]);

            return count;
        }

        /// <summary>
        /// <paramref name="filter"/> with its event terms written anew for what the press shows: the
        /// fewer of the events left out and the events kept, the left out when they tie, before its
        /// other terms, which stay as they were.
        /// </summary>
        public LogFilter Press(LogFilter filter, CategoryNode node, CategoryPress press)
        {
            if (filter is null)
                throw new ArgumentNullException(nameof(filter));

            var pressed = new HashSet<int>(Under(node));
            bool[] shown = _events.Select(filter.KeepsEvent).ToArray();
            bool[] next = press switch
            {
                CategoryPress.Toggle => Toggled(shown, pressed),
                CategoryPress.Only => Only(shown, pressed),
                CategoryPress.Everything => shown.Select(static _ => true).ToArray(),
                _ => throw new ArgumentOutOfRangeException(nameof(press), $"{press} is not a way a category is pressed."),
            };

            return Written(filter, next);
        }

        /// <summary>The events <paramref name="pressed"/> hidden when all of them are shown, and shown when not; the others as they were.</summary>
        private static bool[] Toggled(bool[] shown, HashSet<int> pressed)
        {
            bool all = pressed.All(place => shown[place]);
            return shown.Select((isShown, place) => pressed.Contains(place) ? !all : isShown).ToArray();
        }

        /// <summary>Only the events <paramref name="pressed"/> shown, or every event when only they were.</summary>
        private static bool[] Only(bool[] shown, HashSet<int> pressed)
        {
            bool onlyThem = shown.Select((isShown, place) => isShown == pressed.Contains(place)).All(static same => same);
            return shown.Select((_, place) => onlyThem || pressed.Contains(place)).ToArray();
        }

        /// <summary><paramref name="filter"/> with event terms that show what <paramref name="shown"/> says, and its other terms after.</summary>
        private LogFilter Written(LogFilter filter, bool[] shown)
        {
            (IEnumerable<string> keep, IEnumerable<string> leaveOut) = EventTerms(shown);
            return filter with
            {
                Keep = keep.Select(static pattern => (LogFilterTerm)new LogFilterTerm.Event(pattern))
                    .Concat(filter.Keep.Where(static term => term is not LogFilterTerm.Event)).ToArray(),
                LeaveOut = leaveOut.Select(static pattern => (LogFilterTerm)new LogFilterTerm.Event(pattern))
                    .Concat(filter.LeaveOut.Where(static term => term is not LogFilterTerm.Event)).ToArray(),
            };
        }

        /// <summary>
        /// The event patterns kept and those left out that show what <paramref name="shown"/> says:
        /// none for every event, <c>-*</c> for none, and else the fewer of the two, the left out on a tie.
        /// </summary>
        private (IEnumerable<string> Keep, IEnumerable<string> LeaveOut) EventTerms(bool[] shown)
        {
            IEnumerable<string> none = Enumerable.Empty<string>();
            if (shown.All(static isShown => isShown))
                return (none, none);
            if (!shown.Any(static isShown => isShown))
                return (none, new[] { "*" });

            string[] hidden = Terms(shown, want: false).ToArray();
            string[] kept = Terms(shown, want: true).ToArray();
            return kept.Length < hidden.Length ? (kept, none) : (none, hidden);
        }

        /// <summary>
        /// The fewest patterns that name exactly the events whose <paramref name="shown"/> is
        /// <paramref name="want"/>: a category and everything under it where that is all of them, its
        /// own category where that is, and each event's key where only some of a category's are.
        /// </summary>
        private IEnumerable<string> Terms(bool[] shown, bool want) => Children(-1).SelectMany(node => Terms(node, shown, want));

        private IEnumerable<string> Terms(int node, bool[] shown, bool want)
        {
            CategoryNode at = _nodes[node];
            int wanted = at.Under.Count(place => shown[place] == want);
            if (wanted == 0)
                return Enumerable.Empty<string>();
            if (wanted == at.Under.Count)
                return new[] { at.Category + ".*" };

            // Two specs of one key, as a key declared twice makes, are one term.
            int ownWanted = at.Own.Count(place => shown[place] == want);
            IEnumerable<string> own = ownWanted > 0 && ownWanted == at.Own.Count
                ? new[] { at.Category }
                : at.Own.Where(place => shown[place] == want).Select(place => _events[place].Key).Distinct(StringComparer.Ordinal);
            return own.Concat(Children(node).SelectMany(child => Terms(child, shown, want)));
        }

        /// <summary>The nodes just under <paramref name="node"/>, in order: those at the tree's top for -1.</summary>
        private IEnumerable<int> Children(int node)
        {
            int end = node < 0 ? _nodes.Length : _ends[node];
            for (int child = node + 1; child < end; child = _ends[child])
                yield return child;
        }

        /// <summary>The places under <paramref name="node"/>, once it is known to be one of this tree's.</summary>
        private ReadOnlyCollection<int> Under(CategoryNode node)
        {
            if (node is null)
                throw new ArgumentNullException(nameof(node));
            // A record's != tries the reference first, and the window passes the tree's own nodes, drawing each every tick it logs.
            if (!_byCategory.TryGetValue(node.Category, out int at) || _nodes[at] != node)
                throw new ArgumentException($"{node.Category} is not a category of this tree's events.", nameof(node));

            return _nodes[at].Under;
        }

        /// <summary>Every category above <paramref name="category"/>, from the top, then it: <c>game</c>, then <c>game.save</c>, for <c>game.save</c>.</summary>
        private static IEnumerable<string> AndAbove(string category)
        {
            for (int dot = category.IndexOf('.'); dot >= 0; dot = category.IndexOf('.', dot + 1))
                yield return category.Substring(0, dot);

            yield return category;
        }

        private static bool IsUnder(string category, string ancestor) =>
            category.Length > ancestor.Length && category[ancestor.Length] == '.' && category.StartsWith(ancestor, StringComparison.Ordinal);
    }
}
