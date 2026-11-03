using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using Tutan.Functional;

namespace Tutan.Logging
{
    /// <summary>
    /// Every event and scope the process can say, filled as its assemblies load: the code the
    /// generator writes registers each class's specs when Unity loads the domain and again when it
    /// enters Play Mode, so an assembly's events are here before any of them fires. A console builds
    /// its filters from it, and a rule's pattern is checked against it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is static because specs are metadata, as types are, and nothing is written through it.
    /// Records are not: they belong to a <see cref="LogStore"/>, and a process can have several.
    /// </para>
    /// <para>
    /// Registering is idempotent: a spec already registered is passed over. That is what lets the
    /// registration run on every entry to Play Mode, which it has to when domain reload is off, and
    /// still list each event once.
    /// </para>
    /// </remarks>
    public static class LogCatalog
    {
        private static readonly LogRegistry s_registry = new LogRegistry();

        static LogCatalog()
        {
            // The package's own events first, so a store says its own trouble from the first record.
            Register(SelfLog.Events.All);
            Register(UnityLog.Events.All);
        }

        /// <summary>Every spec registered, in the order of their indices.</summary>
        public static IReadOnlyList<LogEventSpec> Events => s_registry.EventList;

        /// <summary>How many specs are registered, which is one more than the highest index.</summary>
        public static int Count => s_registry.EventArray.Length;

        /// <summary>
        /// What two assemblies declared alike, which no compiler could catch: a key twice, or a
        /// category from two classes. Each is also said, as a record, by every store.
        /// </summary>
        public static IReadOnlyList<LogCatalogConflict> Conflicts => s_registry.Conflicts;

        /// <summary>The specs as of the last registration, for the readers that index them by a record's event.</summary>
        internal static LogEventSpec[] EventArray => s_registry.EventArray;

        /// <summary>The spec with <paramref name="key"/>: the first one registered when two classes have it.</summary>
        public static Optional<LogEventSpec> Find(string key) => s_registry.Find(key);

        /// <summary>
        /// Registers <paramref name="specs"/>, giving each the next index. A spec already registered is
        /// passed over, so registering twice is harmless. A key another class has, or a category another
        /// class declares, is a <see cref="Conflicts">conflict</see>: the spec still gets an index, so its
        /// records are written, but <see cref="Find"/> keeps finding the first. A key the same class has,
        /// as a spec made again has, is the new spec's.
        /// </summary>
        /// <remarks>
        /// It takes no assembly, unlike FRL's, since Unity has no unloading to let a spec go on, and so
        /// the code the generator writes into an assembly names no type it would need a reference for.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="specs"/>, or a spec in it, is null; none of them is registered.</exception>
        /// <exception cref="ArgumentException">A spec was read back from a file; none of them is registered.</exception>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void Register(params LogEventSpec[] specs) => s_registry.Register(specs);
    }

    /// <summary>
    /// What <see cref="LogCatalog"/> keeps, as an instance: so a test can register specs of its own,
    /// conflicts and all, in a registry nothing else reads, and leave the process's catalog clean.
    /// </summary>
    internal sealed class LogRegistry
    {
        private readonly object _lock = new object();
        private readonly Dictionary<string, LogEventSpec> _byKey = new Dictionary<string, LogEventSpec>(StringComparer.Ordinal);
        private readonly Dictionary<string, LogSource> _categories = new Dictionary<string, LogSource>(StringComparer.Ordinal);
        private Snapshot _snapshot = new Snapshot(Array.Empty<LogEventSpec>(), Array.Empty<LogCatalogConflict>());

        /// <summary>The specs as of the last registration, in the order of their indices.</summary>
        public LogEventSpec[] EventArray => Volatile.Read(ref _snapshot).Events;

        /// <summary>The specs as of the last registration, read-only.</summary>
        public IReadOnlyList<LogEventSpec> EventList => Volatile.Read(ref _snapshot).EventList;

        /// <summary>The conflicts found so far, in the order they were found, read-only.</summary>
        public IReadOnlyList<LogCatalogConflict> Conflicts => Volatile.Read(ref _snapshot).ConflictList;

        /// <summary>The spec with <paramref name="key"/>: the first one registered when two classes have it.</summary>
        public Optional<LogEventSpec> Find(string key)
        {
            lock (_lock)
                return _byKey.TryGetValue(key, out LogEventSpec? spec) ? F.Some(spec) : F.None;
        }

        /// <summary>Registers <paramref name="specs"/>, as <see cref="LogCatalog.Register"/> says.</summary>
        public void Register(LogEventSpec[] specs)
        {
            if (specs is null)
                throw new ArgumentNullException(nameof(specs));

            // Every spec is checked before any is registered, so a registration is all or nothing.
            for (int i = 0; i < specs.Length; i++)
            {
                if (specs[i] is null)
                    throw new ArgumentNullException(nameof(specs), $"The spec at {i} is null.");
                if (specs[i].Origin == LogSpecOrigin.File)
                    throw new ArgumentException($"{specs[i].Key} was read back from a file, and the catalog holds only the events this program declares.", nameof(specs));
            }

            lock (_lock)
            {
                Snapshot before = _snapshot;
                List<LogEventSpec>? events = null;
                List<LogCatalogConflict>? conflicts = null;

                foreach (LogEventSpec spec in specs)
                {
                    // Registered before, or earlier in this call.
                    if (spec.Index >= 0 || (events is not null && events.IndexOf(spec, before.Events.Length) >= 0))
                        continue;

                    events ??= new List<LogEventSpec>(before.Events);
                    conflicts ??= new List<LogCatalogConflict>(before.Conflicts);

                    if (_categories.TryGetValue(spec.Category, out LogSource? owner))
                    {
                        if (!SameClass(owner, spec.Source) && !conflicts.Exists(c => c is LogCatalogConflict.CategoryTwice twice
                                && twice.Category == spec.Category && SameClass(twice.Second, spec.Source)))
                            conflicts.Add(new LogCatalogConflict.CategoryTwice(spec.Category, owner, spec.Source));
                    }
                    else
                    {
                        _categories.Add(spec.Category, spec.Source);
                    }

                    if (_byKey.TryGetValue(spec.Key, out LogEventSpec? taken) && !SameClass(taken.Source, spec.Source))
                        conflicts.Add(new LogCatalogConflict.KeyTwice(spec.Key, taken.Source, spec.Source));
                    else
                        _byKey[spec.Key] = spec;

                    events.Add(spec);
                }

                if (events is null)
                    return;

                Volatile.Write(ref _snapshot, new Snapshot(events.ToArray(), conflicts!.ToArray()));

                // Only now: a writer that sees a spec's index writes a record of it, and a reader
                // names that record from the specs it reads after, which then hold it.
                for (int index = before.Events.Length; index < events.Count; index++)
                    events[index].Register(index);
            }
        }

        private static bool SameClass(LogSource a, LogSource b) => a.Assembly == b.Assembly && a.Type == b.Type;

        /// <summary>The specs and conflicts as of one registration, published together so a reader never sees one without the other.</summary>
        private sealed class Snapshot
        {
            public Snapshot(LogEventSpec[] events, LogCatalogConflict[] conflicts)
            {
                Events = events;
                Conflicts = conflicts;
                EventList = Array.AsReadOnly(events);
                ConflictList = Array.AsReadOnly(conflicts);
            }

            public LogEventSpec[] Events { get; }

            public LogCatalogConflict[] Conflicts { get; }

            /// <summary><see cref="Events"/>, for a reader outside, which cannot change what a batch names its records by.</summary>
            public IReadOnlyList<LogEventSpec> EventList { get; }

            public IReadOnlyList<LogCatalogConflict> ConflictList { get; }
        }
    }

    /// <summary>Something two assemblies declared alike, which the catalog found when the second registered.</summary>
    public abstract record LogCatalogConflict
    {
        private LogCatalogConflict()
        {
        }

        /// <summary>Two events with one key.</summary>
        /// <param name="Key">The key.</param>
        /// <param name="First">Where the one the catalog finds is declared.</param>
        /// <param name="Second">Where the other is.</param>
        public sealed record KeyTwice(string Key, LogSource First, LogSource Second) : LogCatalogConflict
        {
            /// <inheritdoc />
            public override string ToString() =>
                $"{Key} is declared by {First.Type} in {First.Assembly} and again by {Second.Type} in {Second.Assembly}";
        }

        /// <summary>One category declared by two classes.</summary>
        /// <param name="Category">The category.</param>
        /// <param name="First">Where the first class to register it is.</param>
        /// <param name="Second">Where the other is.</param>
        public sealed record CategoryTwice(string Category, LogSource First, LogSource Second) : LogCatalogConflict
        {
            /// <inheritdoc />
            public override string ToString() =>
                $"The category {Category} is declared by {First.Type} in {First.Assembly} and again by {Second.Type} in {Second.Assembly}";
        }
    }
}
