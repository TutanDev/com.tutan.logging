using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Tutan.Functional;
using UnityEngine;

namespace Tutan.Logging.Editor
{
    /// <summary>A query a team saved under a name, drawn in a colour chosen for it, or else in its name's hue.</summary>
    internal sealed record SavedFilter
    {
        /// <summary>A saved filter of <paramref name="filter"/>, named <paramref name="name"/>, drawn in <paramref name="colour"/>, or its name's hue for none.</summary>
        public SavedFilter(FilterName name, LogFilter filter, Optional<ChosenColour> colour)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Filter = filter ?? throw new ArgumentNullException(nameof(filter));
            Colour = colour;
        }

        /// <summary>The name its button shows.</summary>
        public FilterName Name { get; }

        /// <summary>The filter pressing it shows.</summary>
        public LogFilter Filter { get; }

        /// <summary>The colour chosen for it: none for its name's hue.</summary>
        public Optional<ChosenColour> Colour { get; }

        /// <summary>Its query, as its filter writes itself.</summary>
        public string Query => Filter.ToString();

        /// <summary>
        /// A saved filter of the text a developer typed or a file holds: <paramref name="name"/>, which must
        /// be a name, and <paramref name="query"/>, which must read, refused as
        /// <see cref="SettingsError.QueryUnreadable"/> with the filter's error inside when it does not.
        /// </summary>
        public static Result<SavedFilter> Of(string name, string query, Optional<ChosenColour> colour)
        {
            if (name is null)
                throw new ArgumentNullException(nameof(name));
            if (query is null)
                throw new ArgumentNullException(nameof(query));

            Result<FilterName> read = FilterName.Of(name);
            if (!read.IsSuccess(out FilterName named))
                return read.ErrorUnsafe();

            return LogFilter.Parse(query).Match<Result<SavedFilter>>(
                refused => SettingsErrors.Of(SettingsError.QueryUnreadable, $"The query of {named} does not read: {SaidLines.Closed(refused.Message)}", refused),
                filter => new SavedFilter(named, filter, colour));
        }

        /// <summary>Whether it is on: the rows are of its filter, <paramref name="shown"/>.</summary>
        public bool IsOn(LogFilter shown)
        {
            if (shown is null)
                throw new ArgumentNullException(nameof(shown));

            return Filter.Equals(shown);
        }
    }

    /// <summary>A colour chosen for a category, and the categories under it that have none of their own.</summary>
    /// <param name="Category">The category.</param>
    /// <param name="Colour">The colour.</param>
    internal sealed record CategoryColour(CategoryName Category, ChosenColour Colour);

    /// <summary>The colours chosen for categories, each category once, in order of the category's name.</summary>
    internal sealed record CategoryColours
    {
        private readonly CategoryColour[] _chosen;

        private CategoryColours(CategoryColour[] chosen)
        {
            _chosen = chosen;
            Chosen = Array.AsReadOnly(chosen);
        }

        /// <summary>No colour chosen: every category is drawn in its name's hue.</summary>
        public static CategoryColours None { get; } = new CategoryColours(Array.Empty<CategoryColour>());

        /// <summary>The colours chosen, in ordinal order of the category, as a file writes them.</summary>
        public ReadOnlyCollection<CategoryColour> Chosen { get; }

        /// <summary>
        /// The colour <paramref name="category"/> is drawn in: the one chosen for it, or that of the
        /// nearest category it is under that has one; none for its name's hue. It allocates nothing, as
        /// a row is drawn with it.
        /// </summary>
        public Optional<ChosenColour> Of(string category)
        {
            if (category is null)
                throw new ArgumentNullException(nameof(category));

            int nearest = -1;
            for (int i = 0; i < _chosen.Length; i++)
            {
                CategoryName name = _chosen[i].Category;
                if (name.Holds(category) && (nearest < 0 || name.Value.Length > _chosen[nearest].Category.Value.Length))
                    nearest = i;
            }

            return nearest >= 0 ? _chosen[nearest].Colour : F.None;
        }

        /// <summary>Whether a colour is chosen for <paramref name="category"/> itself, not for a category it is under.</summary>
        public bool IsChosen(CategoryName category) => IndexOf(category) >= 0;

        /// <summary><paramref name="colour"/> chosen for <paramref name="category"/>, in place of the one chosen before.</summary>
        public CategoryColours Choosing(CategoryName category, ChosenColour colour)
        {
            if (category is null)
                throw new ArgumentNullException(nameof(category));

            var chosen = new CategoryColour(category, colour);
            int at = IndexOf(category);
            if (at >= 0)
            {
                if (_chosen[at] == chosen)
                    return this;

                var replaced = (CategoryColour[])_chosen.Clone();
                replaced[at] = chosen;
                return new CategoryColours(replaced);
            }

            CategoryColour[] added = _chosen.Append(chosen).OrderBy(static colour => colour.Category.Value, StringComparer.Ordinal).ToArray();
            return new CategoryColours(added);
        }

        /// <summary>No colour chosen for <paramref name="category"/>, which is drawn as the category it is under is, or in its name's hue; this one when none was.</summary>
        public CategoryColours Unchoosing(CategoryName category)
        {
            if (category is null)
                throw new ArgumentNullException(nameof(category));

            return IndexOf(category) >= 0
                ? new CategoryColours(_chosen.Where(colour => !colour.Category.Equals(category)).ToArray())
                : this;
        }

        /// <summary>Equal when the same colours are chosen for the same categories.</summary>
        public bool Equals(CategoryColours? other) => other is not null && _chosen.SequenceEqual(other._chosen);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (CategoryColour colour in _chosen)
                hash.Add(colour);
            return hash.ToHashCode();
        }

        private int IndexOf(CategoryName category) => Array.FindIndex(_chosen, colour => colour.Category.Equals(category));
    }

    /// <summary>
    /// What every view of the console leaves out, whatever its query line says: words after <c>-</c>
    /// only, as <c>-unity/Logged -"Shader warning"</c>. The rows leave it out after the line's own
    /// words, and the line never shows it.
    /// </summary>
    internal sealed record LeftOut
    {
        private LeftOut(LogFilter filter) => Filter = filter;

        /// <summary>Nothing is always left out.</summary>
        public static LeftOut None { get; } = new LeftOut(LogFilter.All);

        /// <summary>The words left out.</summary>
        public IReadOnlyList<LogFilterTerm> Terms => Filter.LeaveOut;

        /// <summary>It as a query: <c>-unity/Logged -"Shader warning"</c>, or empty for nothing.</summary>
        public string Query => Filter.ToString();

        /// <summary>A filter that only leaves out its words.</summary>
        private LogFilter Filter { get; }

        /// <summary>
        /// <paramref name="text"/> as what is always left out: refused as <see cref="SettingsError.QueryUnreadable"/>,
        /// with the filter's error inside, when it does not read, and as <see cref="SettingsError.LeftOutKeeps"/>
        /// when it names levels, frames or a word kept, since leaving out is all it can do.
        /// </summary>
        public static Result<LeftOut> Parse(string text)
        {
            if (text is null)
                throw new ArgumentNullException(nameof(text));

            return LogFilter.Parse(text).Match<Result<LeftOut>>(
                refused => SettingsErrors.Of(SettingsError.QueryUnreadable, $"What is always left out does not read: {SaidLines.Closed(refused.Message)}", refused),
                filter => filter.Levels.IsAll && filter.Frames.IsAll && filter.Keep.Count == 0
                    ? new LeftOut(filter)
                    : SettingsErrors.Of(SettingsError.LeftOutKeeps,
                        $"What is always left out is words after - only, as -unity/Logged; \"{text.Trim()}\" also says what to keep."));
        }

        /// <summary><paramref name="filter"/>, leaving out its words after its own, each once: <paramref name="filter"/> itself when nothing is left out, or it leaves out all of them already.</summary>
        public LogFilter From(LogFilter filter)
        {
            if (filter is null)
                throw new ArgumentNullException(nameof(filter));

            if (Terms.Count == 0)
                return filter;

            LogFilterTerm[] more = Terms.Where(term => !filter.LeaveOut.Contains(term)).ToArray();
            return more.Length == 0 ? filter : filter with { LeaveOut = filter.LeaveOut.Concat(more).ToArray() };
        }
    }

    /// <summary>What a team shares of the console, committed with the project: saved filters, the colours chosen for categories, and what is always left out.</summary>
    internal sealed record SharedSettings
    {
        private readonly SavedFilter[] _filters;
        private readonly ReadOnlyCollection<SavedFilter> _filterList;

        private SharedSettings(SavedFilter[] filters, CategoryColours colours, LeftOut leftOut)
        {
            _filters = filters;
            _filterList = Array.AsReadOnly(filters);
            Colours = colours;
            LeftOut = leftOut;
        }

        /// <summary>Nothing saved, nothing chosen, nothing left out.</summary>
        public static SharedSettings None { get; } = new SharedSettings(Array.Empty<SavedFilter>(), CategoryColours.None, LeftOut.None);

        /// <summary>The saved filters, in the order their buttons show, no two of one name.</summary>
        public ReadOnlyCollection<SavedFilter> Filters => _filterList;

        /// <summary>The colours chosen for categories.</summary>
        public CategoryColours Colours { get; private init; }

        /// <summary>What every view leaves out.</summary>
        public LeftOut LeftOut { get; private init; }

        /// <summary>The saved filter named <paramref name="name"/>, ignoring case, if one is.</summary>
        public Optional<SavedFilter> Find(FilterName name)
        {
            int at = IndexOf(name);
            return at >= 0 ? _filters[at] : F.None;
        }

        /// <summary><paramref name="filter"/> saved after the others, or refused as <see cref="SettingsError.NameTaken"/> when one has its name.</summary>
        public Result<SharedSettings> Saving(SavedFilter filter)
        {
            if (filter is null)
                throw new ArgumentNullException(nameof(filter));

            if (IndexOf(filter.Name) >= 0)
                return Taken(filter.Name);

            return this with { FilterList = _filters.Append(filter).ToArray() };
        }

        /// <summary>
        /// <paramref name="filter"/> in the place of the one named <paramref name="name"/>: refused as
        /// <see cref="SettingsError.NoSuchFilter"/> when none is, and as <see cref="SettingsError.NameTaken"/>
        /// when it is renamed to another's name.
        /// </summary>
        public Result<SharedSettings> Replacing(FilterName name, SavedFilter filter)
        {
            if (name is null)
                throw new ArgumentNullException(nameof(name));
            if (filter is null)
                throw new ArgumentNullException(nameof(filter));

            int at = IndexOf(name);
            if (at < 0)
                return SettingsErrors.Of(SettingsError.NoSuchFilter, $"No saved filter is named {name}: it was removed or renamed since.");

            int taken = IndexOf(filter.Name);
            if (taken >= 0 && taken != at)
                return Taken(filter.Name);

            if (_filters[at] == filter)
                return this;

            var replaced = (SavedFilter[])_filters.Clone();
            replaced[at] = filter;
            return this with { FilterList = replaced };
        }

        /// <summary>No saved filter named <paramref name="name"/>; this one when none is.</summary>
        public SharedSettings Removing(FilterName name)
        {
            int at = IndexOf(name);
            return at >= 0 ? this with { FilterList = _filters.Where((_, index) => index != at).ToArray() } : this;
        }

        /// <summary>
        /// The saved filter named <paramref name="name"/> moved <paramref name="by"/> places from where
        /// these settings hold it, left when negative, kept among the others; this one when none is
        /// named so, or it cannot move further. A step, not a place, so a move asked of the filters as
        /// drawn is the same move of the file as it is read again; there is no move to a place.
        /// </summary>
        public SharedSettings MovingBy(FilterName name, int by)
        {
            int at = IndexOf(name);
            if (at < 0)
                return this;

            // In long, so no step wraps past the ends.
            int to = (int)Math.Max(0L, Math.Min((long)at + by, _filters.Length - 1L));
            if (to == at)
                return this;

            List<SavedFilter> moved = _filters.ToList();
            SavedFilter filter = moved[at];
            moved.RemoveAt(at);
            moved.Insert(to, filter);
            return this with { FilterList = moved.ToArray() };
        }

        /// <summary><paramref name="colours"/> chosen for the categories, in place of those before.</summary>
        public SharedSettings Colouring(CategoryColours colours)
        {
            if (colours is null)
                throw new ArgumentNullException(nameof(colours));

            return colours.Equals(Colours) ? this : this with { Colours = colours };
        }

        /// <summary><paramref name="leftOut"/> always left out, in place of what was.</summary>
        public SharedSettings LeavingOut(LeftOut leftOut)
        {
            if (leftOut is null)
                throw new ArgumentNullException(nameof(leftOut));

            return leftOut.Equals(LeftOut) ? this : this with { LeftOut = leftOut };
        }

        /// <summary>Equal when the same filters are saved in the same order, and the same colours and words left out.</summary>
        public bool Equals(SharedSettings? other) =>
            other is not null && _filters.SequenceEqual(other._filters) && Colours.Equals(other.Colours) && LeftOut.Equals(other.LeftOut);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (SavedFilter filter in _filters)
                hash.Add(filter);
            hash.Add(Colours);
            hash.Add(LeftOut);
            return hash.ToHashCode();
        }

        // The filters, in place of those before: what `with` sets, so Filters stays the read-only face of them.
        private SavedFilter[] FilterList
        {
            get => _filters;
            init
            {
                _filters = value;
                _filterList = Array.AsReadOnly(value);
            }
        }

        private static Error Taken(FilterName name) =>
            SettingsErrors.Of(SettingsError.NameTaken, $"A saved filter is named {name} already: give this one another name.");

        private int IndexOf(FilterName name)
        {
            if (name is null)
                throw new ArgumentNullException(nameof(name));

            return Array.FindIndex(_filters, filter => filter.Name.SameAs(name));
        }
    }

    /// <summary>What one developer keeps of the console, in their own settings.</summary>
    /// <param name="ClearOnPlay">Whether entering Play Mode clears the console.</param>
    /// <param name="PauseOnError">Whether an error that arrives while playing pauses the game.</param>
    /// <param name="Columns">Which columns of the rows every console window shows, and how wide.</param>
    internal sealed record PersonalSettings(bool ClearOnPlay, bool PauseOnError, ColumnLayout Columns)
    {
        private readonly ColumnLayout _columns = Held(Columns);

        /// <summary>Clear on Play, as Unity's Console has it, no Pause on Error, and the columns as they are at first.</summary>
        public static PersonalSettings Default { get; } = new PersonalSettings(ClearOnPlay: true, PauseOnError: false, ColumnLayout.Default);

        /// <summary>Which columns every console window shows, and how wide: a layout always, whether made or set by <c>with</c>.</summary>
        public ColumnLayout Columns
        {
            get => _columns;
            init => _columns = Held(value);
        }

        private static ColumnLayout Held(ColumnLayout columns) =>
            columns ?? throw new ArgumentNullException(nameof(Columns), "Personal settings hold a column layout: ColumnLayout.Default is the one at first.");
    }

    /// <summary>The colour a category or a saved filter is drawn in.</summary>
    internal static class Look
    {
        /// <summary><paramref name="category"/>'s colour: the one <paramref name="chosen"/> for it, or its name's hue for the skin, <paramref name="dark"/>.</summary>
        public static Color Category(string category, CategoryColours chosen, bool dark)
        {
            if (chosen is null)
                throw new ArgumentNullException(nameof(chosen));

            return chosen.Of(category).HasValue(out ChosenColour colour) ? colour.Colour : CategoryHue.Colour(category, dark);
        }

        /// <summary><paramref name="saved"/>'s colour: the one chosen for it, or its name's hue for the skin, <paramref name="dark"/>.</summary>
        public static Color Saved(SavedFilter saved, bool dark)
        {
            if (saved is null)
                throw new ArgumentNullException(nameof(saved));

            return saved.Colour.HasValue(out ChosenColour colour) ? colour.Colour : CategoryHue.Colour(saved.Name.Value, dark);
        }
    }
}
