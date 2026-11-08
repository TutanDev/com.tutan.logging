using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Tutan.Logging
{
    /// <summary>
    /// What a program passes to the code that logs: a handle to one <see cref="LogStore"/>, and to
    /// the scope what is written through it is under. Every declared event and scope is written
    /// through one. There is no static logger: a <see cref="Log"/> is a dependency like any other,
    /// handed in by the composition root.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It is a value of two words, so the <see cref="LogScope.Log"/> of every scope a program opens
    /// allocates nothing. A <c>default</c> <see cref="Log"/> was never handed a store, and the first
    /// event written through it throws, saying so, rather than going nowhere; a program or a test
    /// that wants nothing from a log passes <see cref="Discard"/>.
    /// </para>
    /// <para>
    /// A record written through the <see cref="LogScope.Log"/> of a scope that has closed is still
    /// under that scope: the handle says which scope, and closing it cannot reach the copies.
    /// </para>
    /// </remarks>
    public readonly struct Log : IEquatable<Log>
    {
        private readonly LogStore? _store;
        private readonly long _scope;

        internal Log(LogStore store, long scope)
        {
            _store = store;
            _scope = scope;
        }

        /// <summary>A log that captures nothing, for a program or a test that wants nothing from it. Every event is off.</summary>
        public static Log Discard { get; } = new Log(LogStore.Discarding, 0);

        /// <summary>Whether this is a handle to a store, as every log but <c>default</c> is.</summary>
        public bool IsHanded => _store is not null;

        /// <summary>The scope what is written through it is under: the sequence of the record that opened it, or 0 for none.</summary>
        public long Scope => _scope;

        internal LogStore Store => _store ?? throw NotHanded();

        /// <summary>
        /// Whether <paramref name="spec"/> is captured: one array load and one compare. A call site
        /// whose arguments are expensive asks this first, since they are evaluated even when the event
        /// is off. For an event whose level is chosen at the call, whether any level of it is.
        /// </summary>
        /// <exception cref="InvalidOperationException">The log is <c>default</c>: it was never handed a store.</exception>
        public bool IsEnabled(LogEventSpec spec)
        {
            LogStore? store = _store;
            if (store is null)
                ThrowNotHanded();

            byte[] least = store!.Least;
            int index = spec.Index;
            if ((uint)index < (uint)least.Length)
                return spec.Gate >= least[index];

            return store.Refresh(spec, spec.Gate);
        }

        /// <summary>Whether <paramref name="spec"/> is captured at <paramref name="level"/>, for an event whose level is chosen at the call.</summary>
        /// <exception cref="InvalidOperationException">The log is <c>default</c>: it was never handed a store.</exception>
        public bool IsEnabled(LogEventSpec spec, LogLevel level)
        {
            LogStore? store = _store;
            if (store is null)
                ThrowNotHanded();
            if (level >= LogLevel.None)
                return false;

            byte[] least = store!.Least;
            int index = spec.Index;
            if ((uint)index < (uint)least.Length)
                return (byte)level >= least[index];

            return store.Refresh(spec, (byte)level);
        }

        /// <summary>
        /// Starts a record of <paramref name="spec"/>, which the generated code fills and commits. Its
        /// caller has asked <see cref="IsEnabled(LogEventSpec)"/> first.
        /// </summary>
        /// <param name="spec">The event.</param>
        /// <param name="level">The record's level: the spec's, or the one chosen at the call.</param>
        /// <param name="chars">The slots the call's chars fields take beyond their lengths, by <see cref="LogWriter.CharSlots"/>.</param>
        /// <param name="callerFile">The file of the call, which the compiler wrote as a constant.</param>
        /// <param name="callerLine">The line of the call.</param>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public LogWriter Write(LogEventSpec spec, LogLevel level, int chars, string callerFile, int callerLine) =>
            Store.Open(this, spec, level, chars, scope: false, callerFile, callerLine);

        /// <summary>Starts the record that opens a scope of <paramref name="spec"/>, as <see cref="Write"/> does an event's.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public LogWriter BeginScope(LogEventSpec spec, LogLevel level, int chars, string callerFile, int callerLine) =>
            Store.Open(this, spec, level, chars, scope: true, callerFile, callerLine);

        /// <summary>Whether both are handles to one store and one scope.</summary>
        public bool Equals(Log other) => ReferenceEquals(_store, other._store) && _scope == other._scope;

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is Log other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(_store, _scope);

        /// <summary>Whether both are handles to one store and one scope.</summary>
        public static bool operator ==(Log left, Log right) => left.Equals(right);

        /// <summary>Whether they are handles to different stores or scopes.</summary>
        public static bool operator !=(Log left, Log right) => !left.Equals(right);

        /// <summary>The store's program and the scope, for a debugger.</summary>
        public override string ToString() =>
            _store is null ? "Log (default: never handed a store)"
            : ReferenceEquals(_store, LogStore.Discarding) ? "Log.Discard"
            : _scope == 0 ? $"Log of {_store.Program}" : $"Log of {_store.Program} under #{_scope.ToString(CultureInfo.InvariantCulture)}";

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowNotHanded() => throw NotHanded();

        private static InvalidOperationException NotHanded() => new InvalidOperationException(
            "This Log is default: it was never handed a store, so what is written through it would go nowhere. " +
            "Pass the Log of a LogStore from the composition root, or Log.Discard for a log that captures nothing.");
    }
}
