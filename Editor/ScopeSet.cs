using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Tutan.Logging.Editor
{
    /// <summary>
    /// Scopes, each the sequence of the record that opened it, in order and each once: a value, so two
    /// sets of the same scopes are equal, and adding or taking one makes a new set.
    /// </summary>
    internal sealed record ScopeSet
    {
        private readonly long[] _scopes;

        private ScopeSet(long[] scopes)
        {
            _scopes = scopes;
            Scopes = Array.AsReadOnly(scopes);
        }

        /// <summary>No scopes.</summary>
        public static ScopeSet Empty { get; } = new ScopeSet(Array.Empty<long>());

        /// <summary>The scopes, from the oldest.</summary>
        public ReadOnlyCollection<long> Scopes { get; }

        /// <summary>Whether it holds <paramref name="scope"/>.</summary>
        public bool Contains(long scope) => Array.BinarySearch(_scopes, scope) >= 0;

        /// <summary>The set with <paramref name="scope"/> in it: this one, when it is already.</summary>
        public ScopeSet With(long scope)
        {
            int at = Array.BinarySearch(_scopes, scope);
            if (at >= 0)
                return this;

            at = ~at;
            var scopes = new long[_scopes.Length + 1];
            Array.Copy(_scopes, 0, scopes, 0, at);
            scopes[at] = scope;
            Array.Copy(_scopes, at, scopes, at + 1, _scopes.Length - at);
            return new ScopeSet(scopes);
        }

        /// <summary>The set without <paramref name="scope"/>: this one, when it is not in it.</summary>
        public ScopeSet Without(long scope)
        {
            int at = Array.BinarySearch(_scopes, scope);
            if (at < 0)
                return this;
            if (_scopes.Length == 1)
                return Empty;

            var scopes = new long[_scopes.Length - 1];
            Array.Copy(_scopes, 0, scopes, 0, at);
            Array.Copy(_scopes, at + 1, scopes, at, _scopes.Length - at - 1);
            return new ScopeSet(scopes);
        }

        /// <summary>Equal when they hold the same scopes.</summary>
        public bool Equals(ScopeSet? other) => other is not null && _scopes.AsSpan().SequenceEqual(other._scopes);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (long scope in _scopes)
                hash.Add(scope);

            return hash.ToHashCode();
        }

        /// <inheritdoc />
        public override string ToString() => string.Join(" ", (IEnumerable<long>)_scopes);
    }
}
