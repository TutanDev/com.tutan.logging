using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Tutan.Functional;
using UnityEngine;

namespace Tutan.Logging.Editor
{
    /// <summary>An entry of a settings file that did not read, and why: it is left out of the settings read.</summary>
    /// <param name="Entry">Which entry: <c>saved filter 3, "Saves"</c>.</param>
    /// <param name="Reason">Why it did not read, closed.</param>
    internal sealed record SettingSkipped(string Entry, string Reason);

    /// <summary>
    /// What a settings file read as: its settings, with the entries it skipped, or nothing, when the
    /// file cannot be read at all and so is not to be written over.
    /// </summary>
    /// <typeparam name="T">The settings the file holds.</typeparam>
    internal abstract record SettingsRead<T>
    {
        private SettingsRead()
        {
        }

        /// <summary>The settings read: those of an unreadable file are <paramref name="none"/>, the settings when nothing is saved.</summary>
        public T SettingsOr(T none) => Match(static read => read.Settings, _ => none);

        /// <summary>
        /// What <paramref name="change"/> makes of the settings read of the file at <paramref name="path"/>,
        /// to be written: refused as <see cref="SettingsError.Unwritable"/> while the file cannot be read,
        /// so it is never written over, or as the change refuses; none when the change makes the settings
        /// read, so nothing is written.
        /// </summary>
        public Result<Optional<T>> Changing(Func<T, Result<T>> change, string path)
        {
            if (change is null)
                throw new ArgumentNullException(nameof(change));
            if (path is null)
                throw new ArgumentNullException(nameof(path));

            return Match<Result<Optional<T>>>(
                read => change(read.Settings).Map<T, Optional<T>>(next => EqualityComparer<T>.Default.Equals(next, read.Settings) ? F.None : F.Some(next)),
                unreadable => SettingsErrors.Of(SettingsError.Unwritable,
                    $"{path} cannot be read, so it is not written over: {unreadable.Reason} Fix or delete it, and the console reads it again when the editor has focus again."));
        }

        /// <summary>Does what <paramref name="read"/> or <paramref name="unreadable"/> does with the case it is.</summary>
        public abstract R Match<R>(Func<Read, R> read, Func<Unreadable, R> unreadable);

        /// <summary>The file read, but for the entries in <see cref="Skipped"/>, which a write leaves out.</summary>
        /// <param name="Settings">The settings it holds.</param>
        /// <param name="Skipped">The entries that did not read.</param>
        public sealed record Read(T Settings, ReadOnlyCollection<SettingSkipped> Skipped) : SettingsRead<T>
        {
            /// <inheritdoc />
            public override R Match<R>(Func<Read, R> read, Func<Unreadable, R> unreadable) => read(this);

            /// <summary>Equal when the settings are, and the same entries were skipped for the same reasons, in the same order.</summary>
            public bool Equals(Read? other) =>
                other is not null && EqualityComparer<T>.Default.Equals(Settings, other.Settings) && Skipped.SequenceEqual(other.Skipped);

            /// <inheritdoc />
            public override int GetHashCode()
            {
                var hash = new HashCode();
                hash.Add(Settings);
                foreach (SettingSkipped skipped in Skipped)
                    hash.Add(skipped);
                return hash.ToHashCode();
            }
        }

        /// <summary>
        /// The file is not JSON, or of a version this package never wrote, or could not be read just now,
        /// as when another program holds it, as <see cref="Reason"/> says: it is read as nothing and never
        /// written over. One that could not be read just now is read again on the next try; one that is
        /// not JSON, or of a version this package never wrote, once it changes on disk.
        /// </summary>
        /// <param name="Reason">Why it cannot be read, closed.</param>
        public sealed record Unreadable(string Reason) : SettingsRead<T>
        {
            /// <inheritdoc />
            public override R Match<R>(Func<Read, R> read, Func<Unreadable, R> unreadable) => unreadable(this);
        }
    }

    /// <summary>How a settings file is read: the JSON in it, entry by entry, and the cases every file has alike.</summary>
    internal static class SettingsText
    {
        private static readonly ReadOnlyCollection<SettingSkipped> s_noneSkipped = Array.AsReadOnly(Array.Empty<SettingSkipped>());

        /// <summary>Nothing skipped.</summary>
        public static ReadOnlyCollection<SettingSkipped> NoneSkipped => s_noneSkipped;

        /// <summary>
        /// <paramref name="text"/>, a file's, read as <typeparamref name="TFile"/> and then as settings: none,
        /// or only white space, is nothing saved, <paramref name="none"/>; text that is not JSON is unreadable.
        /// </summary>
        public static SettingsRead<T> Read<TFile, T>(Optional<string> text, T none, Func<TFile, SettingsRead<T>> toDomain)
            where TFile : class
        {
            if (!text.HasValue(out string json) || json.Trim().Length == 0)
                return new SettingsRead<T>.Read(none, s_noneSkipped);

            TFile? file;
            try
            {
                file = JsonUtility.FromJson<TFile>(json);
            }
            catch (ArgumentException notJson)
            {
                return new SettingsRead<T>.Unreadable($"It is not JSON: {SaidLines.Closed(notJson.Message)}");
            }

            return file is null ? new SettingsRead<T>.Unreadable("It holds no settings.") : toDomain(file);
        }

        /// <summary>What a file of <paramref name="version"/>, which this package never wrote, is read as.</summary>
        public static SettingsRead<T> UnknownVersion<T>(int version) =>
            new SettingsRead<T>.Unreadable(
                $"It is of version {version.ToString(CultureInfo.InvariantCulture)}, which this package does not know: a newer package wrote it, or no console did.");
    }

    /// <summary>
    /// <c>ProjectSettings/TutanLogConsole.json</c>, what a team shares of the console, as
    /// <see cref="JsonUtility"/> writes it: a DTO of primitives and a <see cref="Version"/>, read entry
    /// by entry, so an entry that does not read is skipped and the rest are kept.
    /// </summary>
    /// <remarks>
    /// Every change of what the file holds is a new <see cref="Current"/>, a field added included:
    /// <see cref="JsonUtility"/> drops a field its type lacks, so a package that read a newer file as a
    /// version it knows would write it back without that field. Each version this package wrote stays
    /// readable, with a case of its own in <see cref="ToDomain"/>, and a test reads a file of it as it
    /// was written.
    /// </remarks>
    [Serializable]
    internal sealed class SharedSettingsFile
    {
        /// <summary>The version this package writes.</summary>
        public const int Current = 1;

        /// <summary>The version that wrote it: 0 for a file that says none.</summary>
        public int Version;

        /// <summary>The saved filters, in the order their buttons show.</summary>
        public SavedFilterEntry[]? Filters;

        /// <summary>The colours chosen for categories, by category.</summary>
        public CategoryColourEntry[]? Colours;

        /// <summary>What every view leaves out, as a query of words after <c>-</c>.</summary>
        public string? LeftOut;

        /// <summary>What <paramref name="settings"/> is written as: filters as they are ordered, and colours by category, so a change reads as a small diff.</summary>
        public static SharedSettingsFile FromDomain(SharedSettings settings)
        {
            if (settings is null)
                throw new ArgumentNullException(nameof(settings));

            return new SharedSettingsFile
            {
                Version = Current,
                Filters = settings.Filters.Select(static filter => new SavedFilterEntry
                {
                    Name = filter.Name.Value,
                    Query = filter.Query,
                    Colour = filter.Colour.HasValue(out ChosenColour colour) ? colour.Hex : "",
                }).ToArray(),
                Colours = settings.Colours.Chosen.Select(static chosen => new CategoryColourEntry
                {
                    Category = chosen.Category.Value,
                    Colour = chosen.Colour.Hex,
                }).ToArray(),
                LeftOut = settings.LeftOut.Query,
            };
        }

        /// <summary>The file's text, none for no file, read as the settings it holds.</summary>
        public static SettingsRead<SharedSettings> Read(Optional<string> text) =>
            SettingsText.Read<SharedSettingsFile, SharedSettings>(text, SharedSettings.None, static file => file.ToDomain());

        /// <summary>The settings it holds, as its <see cref="Version"/> wrote them: entry by entry, skipping and saying why of each that does not read.</summary>
        public SettingsRead<SharedSettings> ToDomain() => Version switch
        {
            1 => ReadVersion1(),
            _ => SettingsText.UnknownVersion<SharedSettings>(Version),
        };

        /// <summary>It as JSON, indented, as it is committed.</summary>
        public string ToJson() => JsonUtility.ToJson(this, prettyPrint: true);

        private SettingsRead<SharedSettings> ReadVersion1()
        {
            var skipped = new List<SettingSkipped>();
            SharedSettings settings = SharedSettings.None;

            SavedFilterEntry?[] filters = Filters ?? Array.Empty<SavedFilterEntry>();
            for (int i = 0; i < filters.Length; i++)
            {
                SavedFilterEntry? entry = filters[i];
                string which = $"Saved filter {(i + 1).ToString(CultureInfo.InvariantCulture)}";
                if (entry is null)
                {
                    skipped.Add(new SettingSkipped(which, "It is empty."));
                    continue;
                }

                which += $", \"{entry.Name}\"";
                Result<SharedSettings> saved = Colour(entry.Colour)
                    .Bind(colour => SavedFilter.Of(entry.Name ?? "", entry.Query ?? "", colour))
                    .Bind(filter => settings.Saving(filter));
                if (saved.IsSuccess(out SharedSettings next))
                    settings = next;
                else
                    skipped.Add(new SettingSkipped(which, SaidLines.Closed(saved.ErrorUnsafe().Message)));
            }

            CategoryColours colours = CategoryColours.None;
            CategoryColourEntry?[] chosen = Colours ?? Array.Empty<CategoryColourEntry>();
            for (int i = 0; i < chosen.Length; i++)
            {
                CategoryColourEntry? entry = chosen[i];
                string which = $"Category colour {(i + 1).ToString(CultureInfo.InvariantCulture)}";
                if (entry is null)
                {
                    skipped.Add(new SettingSkipped(which, "It is empty."));
                    continue;
                }

                which += $", \"{entry.Category}\"";
                Result<CategoryName> category = CategoryName.Of(entry.Category ?? "");
                Result<ChosenColour> colour = ChosenColour.Parse(entry.Colour ?? "");
                if (!category.IsSuccess(out CategoryName name))
                    skipped.Add(new SettingSkipped(which, SaidLines.Closed(category.ErrorUnsafe().Message)));
                else if (!colour.IsSuccess(out ChosenColour read))
                    skipped.Add(new SettingSkipped(which, SaidLines.Closed(colour.ErrorUnsafe().Message)));
                else if (colours.IsChosen(name))
                    skipped.Add(new SettingSkipped(which, $"A colour is chosen for {name} already, by an entry before it."));
                else
                    colours = colours.Choosing(name, read);
            }

            settings = settings.Colouring(colours);
            Result<LeftOut> leftOut = LeftOut is null ? Editor.LeftOut.None : Editor.LeftOut.Parse(LeftOut);
            if (leftOut.IsSuccess(out LeftOut always))
                settings = settings.LeavingOut(always);
            else
                skipped.Add(new SettingSkipped("What is always left out", SaidLines.Closed(leftOut.ErrorUnsafe().Message)));

            return new SettingsRead<SharedSettings>.Read(settings, skipped.Count > 0 ? Array.AsReadOnly(skipped.ToArray()) : SettingsText.NoneSkipped);
        }

        /// <summary>A filter's colour as its entry has it: empty for its name's hue.</summary>
        private static Result<Optional<ChosenColour>> Colour(string? text) =>
            string.IsNullOrWhiteSpace(text)
                ? F.Success<Optional<ChosenColour>>(F.None)
                : ChosenColour.Parse(text!).Map(static colour => (Optional<ChosenColour>)colour);
    }

    /// <summary>A saved filter, as a file holds it.</summary>
    [Serializable]
    internal sealed class SavedFilterEntry
    {
        /// <summary>Its name.</summary>
        public string? Name;

        /// <summary>Its query.</summary>
        public string? Query;

        /// <summary>The colour chosen for it, as <c>#RRGGBB</c>: empty for its name's hue.</summary>
        public string? Colour;
    }

    /// <summary>A colour chosen for a category, as a file holds it.</summary>
    [Serializable]
    internal sealed class CategoryColourEntry
    {
        /// <summary>The category.</summary>
        public string? Category;

        /// <summary>The colour, as <c>#RRGGBB</c>.</summary>
        public string? Colour;
    }

    /// <summary>
    /// <c>UserSettings/TutanLogConsole.json</c>, what one developer keeps of the console, as
    /// <see cref="JsonUtility"/> writes it: a DTO of primitives and a <see cref="Version"/>.
    /// </summary>
    /// <remarks>
    /// Every change of what the file holds is a new <see cref="Current"/>, as for
    /// <see cref="SharedSettingsFile"/>. It matters more here: <c>UserSettings/</c> is not under source
    /// control, so the file stays as it is while a branch checked out changes the package's version.
    /// A file of an older version is made one of the next first, a step for each version, and only the
    /// newest is read into settings.
    /// </remarks>
    [Serializable]
    internal sealed class PersonalSettingsFile
    {
        /// <summary>The version this package writes: 2 added the columns.</summary>
        public const int Current = 2;

        /// <summary>The version that wrote it: 0 for a file that says none.</summary>
        public int Version;

        /// <summary>Whether entering Play Mode clears the console.</summary>
        public bool ClearOnPlay;

        /// <summary>Whether an error that arrives while playing pauses the game.</summary>
        public bool PauseOnError;

        /// <summary>Each column but the one that fills, in the order drawn: from version 2.</summary>
        public ColumnEntry[]? Columns;

        /// <summary>What <paramref name="settings"/> is written as: its columns in the order drawn, so a change reads as a small diff.</summary>
        public static PersonalSettingsFile FromDomain(PersonalSettings settings)
        {
            if (settings is null)
                throw new ArgumentNullException(nameof(settings));

            return new PersonalSettingsFile
            {
                Version = Current,
                ClearOnPlay = settings.ClearOnPlay,
                PauseOnError = settings.PauseOnError,
                Columns = settings.Columns.Sized.Select(static shown => new ColumnEntry
                {
                    Column = ColumnEntry.NameOf(shown.Column),
                    Shown = shown.Shown,
                    Width = shown.Width,
                }).ToArray(),
            };
        }

        /// <summary>The file's text, none for no file, read as the settings it holds.</summary>
        public static SettingsRead<PersonalSettings> Read(Optional<string> text) =>
            SettingsText.Read<PersonalSettingsFile, PersonalSettings>(text, PersonalSettings.Default, static file => file.ToDomain());

        /// <summary>The settings it holds, as its <see cref="Version"/> wrote them: a file of version 1 made one of version 2 first.</summary>
        public SettingsRead<PersonalSettings> ToDomain() => Version switch
        {
            1 => Version2Of1().ToDomain(),
            2 => ReadVersion2(),
            _ => SettingsText.UnknownVersion<PersonalSettings>(Version),
        };

        /// <summary>This file, of version 1, as one of version 2: its toggles as they are, and no columns, which read as they are at first.</summary>
        /// <exception cref="InvalidOperationException">It is not of version 1.</exception>
        public PersonalSettingsFile Version2Of1()
        {
            if (Version != 1)
                throw new InvalidOperationException($"Only a file of version 1 is made one of version 2, and this is of version {Version.ToString(CultureInfo.InvariantCulture)}.");

            return new PersonalSettingsFile { Version = 2, ClearOnPlay = ClearOnPlay, PauseOnError = PauseOnError, Columns = Array.Empty<ColumnEntry>() };
        }

        /// <summary>It as JSON, indented.</summary>
        public string ToJson() => JsonUtility.ToJson(this, prettyPrint: true);

        /// <summary>
        /// The settings of version 2: the columns read from <see cref="ColumnLayout.Default"/>, entry by
        /// entry, each that does not read skipped and said why. Of two entries of one column, the first
        /// kept is read; a width read is held within its column's bounds.
        /// </summary>
        private SettingsRead<PersonalSettings> ReadVersion2()
        {
            var skipped = new List<SettingSkipped>();
            var read = new HashSet<ConsoleColumn>();
            ColumnLayout columns = ColumnLayout.Default;
            ColumnEntry?[] entries = Columns ?? Array.Empty<ColumnEntry>();
            for (int i = 0; i < entries.Length; i++)
            {
                ColumnEntry? entry = entries[i];
                string which = $"Column {(i + 1).ToString(CultureInfo.InvariantCulture)}";
                if (entry is null)
                {
                    skipped.Add(new SettingSkipped(which, "It is empty."));
                    continue;
                }

                which += $", \"{entry.Column}\"";
                if (!ColumnEntry.Named(entry.Column).HasValue(out ConsoleColumn column))
                    skipped.Add(new SettingSkipped(which, "No column of the console is named so."));
                else if (ConsoleColumns.Fills(column))
                    skipped.Add(new SettingSkipped(which, $"The {ConsoleColumns.Title(column)} column is always shown, as wide as the room the others leave."));
                else if (entry.Width < 1)
                    skipped.Add(new SettingSkipped(which, "Its width is under 1 pixel."));
                else if (!read.Add(column))
                    skipped.Add(new SettingSkipped(which, $"An entry before it is of the {ConsoleColumns.Title(column)} column, and that one is read."));
                else
                    columns = columns.Showing(column, entry.Shown).Sizing(column, entry.Width);
            }

            var settings = new PersonalSettings(ClearOnPlay, PauseOnError, columns);
            return new SettingsRead<PersonalSettings>.Read(settings, skipped.Count > 0 ? Array.AsReadOnly(skipped.ToArray()) : SettingsText.NoneSkipped);
        }
    }

    /// <summary>A column of the rows, as the personal file holds it: whether it is shown, and how wide.</summary>
    [Serializable]
    internal sealed class ColumnEntry
    {
        /// <summary>The column's name, as <see cref="NameOf"/> writes it.</summary>
        public string? Column;

        /// <summary>Whether it is shown.</summary>
        public bool Shown;

        /// <summary>How many pixels wide it is.</summary>
        public int Width;

        /// <summary>
        /// The name a file holds <paramref name="column"/> by, as <c>call-site</c>: written once and
        /// never changed, so every file a package wrote reads, whatever the stylesheet calls it.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="column"/> is not a column.</exception>
        public static string NameOf(ConsoleColumn column) => column switch
        {
            ConsoleColumn.Time => "time",
            ConsoleColumn.Frame => "frame",
            ConsoleColumn.Level => "level",
            ConsoleColumn.Category => "category",
            ConsoleColumn.Event => "event",
            ConsoleColumn.Message => "message",
            ConsoleColumn.Duration => "duration",
            ConsoleColumn.CallSite => "call-site",
            ConsoleColumn.Thread => "thread",
            _ => throw ConsoleColumns.Unknown(column),
        };

        /// <summary>The column a file names <paramref name="name"/>, as spelt: none for none given, or a name no column has.</summary>
        public static Optional<ConsoleColumn> Named(string? name)
        {
            if (name is null)
                return F.None;

            foreach (ConsoleColumn column in ConsoleColumns.All)
            {
                if (string.Equals(NameOf(column), name, StringComparison.Ordinal))
                    return column;
            }

            return F.None;
        }
    }
}
