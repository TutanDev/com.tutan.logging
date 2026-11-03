using System;
using System.Collections.Generic;
using Tutan.Functional;

namespace Tutan.Logging
{
    /// <summary>
    /// The tree of scopes a reader has seen: which scope each was opened in, whether it is still open,
    /// and how long it was. A <see cref="LogView"/> teaches it every record it reads, and a filter's
    /// <c>scope:</c> asks it what is under what, on every thread the scope reached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A scope whose opening the reader never read, because the store overwrote it first or the sink
    /// that kept the records was above its level, is looked up in the records when something under it
    /// is learnt, and is unknown when it is not there either: under no scope, and neither open nor
    /// closed. It keeps a scope while the records hold anything of it or of a scope under it, and
    /// forgets it after, as <see cref="Prune"/> finds; an open scope is kept until it closes.
    /// </para>
    /// <para>
    /// Only its view teaches it, and asking it changes nothing: what is under what is what the records
    /// learnt so far say, so a filter asks the same question twice and gets the same answer.
    /// </para>
    /// </remarks>
    public sealed class LogScopes
    {
        /// <summary>The deepest a chain of scopes is followed, which ends a chain a broken file makes a loop of.</summary>
        private const int MostDepth = 256;

        /// <summary>The most scopes looked up in the records for one record whose scope is unknown.</summary>
        private const int MostLookedUp = 64;

        private readonly LogRecords _source;
        private readonly Dictionary<long, Node> _nodes = new Dictionary<long, Node>();
        private readonly List<long> _forgotten = new List<long>();
        private readonly Stack<Node> _free = new Stack<Node>();
        private readonly LogBatch _one = new LogBatch(1);
        private long _prunedAt;

        /// <summary>The scopes of <paramref name="source"/>'s records, none known yet.</summary>
        internal LogScopes(LogRecords source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _prunedAt = source.First;
        }

        /// <summary>How many scopes it knows.</summary>
        public int Count => _nodes.Count;

        /// <summary>Learns what <paramref name="record"/> says: a scope opened or closed, or something under one.</summary>
        internal void Learn(LogRecord record)
        {
            if (record.Kind == LogRecordKind.ScopeBegin)
            {
                Node node = NodeOf(record.Sequence, out _);
                node.Parent = record.Scope;
                if (node.State == ScopeState.Unknown)
                    node.State = ScopeState.Open;
                node.Last = Math.Max(node.Last, record.Sequence);
            }
            else if (record.Kind == LogRecordKind.ScopeEnd)
            {
                // A scope's closing is under the scope it was opened in, so it says where an opening
                // the reader never read belongs.
                Node node = NodeOf(record.Begin, out bool known);
                if (!known || node.State == ScopeState.Unknown)
                    node.Parent = record.Scope;
                node.State = ScopeState.Closed;
                node.Elapsed = record.Elapsed;
                node.Last = Math.Max(node.Last, record.Sequence);
            }

            if (record.Scope == 0)
                return;

            if (!_nodes.ContainsKey(record.Scope))
                LookUp(record.Scope);

            if (_nodes.TryGetValue(record.Scope, out Node? under))
                under.Last = Math.Max(under.Last, record.Sequence);
        }

        /// <summary>
        /// Whether <paramref name="scope"/> is <paramref name="ancestor"/> or under it, however deep:
        /// so a record whose scope this is is under the ancestor too.
        /// </summary>
        public bool IsUnder(long scope, long ancestor)
        {
            if (ancestor == 0)
                return false;

            for (int depth = 0; scope != 0 && depth < MostDepth; depth++)
            {
                if (scope == ancestor)
                    return true;
                scope = Parent(scope);
            }

            return false;
        }

        /// <summary>The scope <paramref name="scope"/> was opened in, or 0 when it was opened in none or is unknown.</summary>
        public long Parent(long scope) => _nodes.TryGetValue(scope, out Node? node) ? node.Parent : 0;

        /// <summary>How long <paramref name="scope"/> was open, once it has closed.</summary>
        public Optional<TimeSpan> Elapsed(long scope) =>
            _nodes.TryGetValue(scope, out Node? node) && node.State == ScopeState.Closed ? F.Some(node.Elapsed) : F.None;

        /// <summary>Whether <paramref name="scope"/> was opened and has not closed, as far as the records read say.</summary>
        public bool IsOpen(long scope) => _nodes.TryGetValue(scope, out Node? node) && node.State == ScopeState.Open;

        /// <summary>
        /// Forgets the scopes the records no longer hold anything of, once the records have moved on
        /// by a quarter of what they hold since it last did: a closed or unknown scope with nothing
        /// under it, whose last record is gone.
        /// </summary>
        internal void Prune()
        {
            long first = _source.First;
            if (first - _prunedAt < Math.Max(1, _source.Capacity / 4))
                return;

            _prunedAt = first;
            foreach (Node node in _nodes.Values)
                node.Children = 0;

            foreach (Node node in _nodes.Values)
            {
                if (node.Parent != 0 && _nodes.TryGetValue(node.Parent, out Node? parent))
                    parent.Children++;
            }

            // Gathered first and removed after: Mono's dictionary refuses a removal while it is enumerated.
            // A forgotten scope's node is kept for the next scope learnt, so learning allocates nothing
            // once the scopes known at once are as many as they get.
            foreach (KeyValuePair<long, Node> pair in _nodes)
            {
                Node node = pair.Value;
                if (node.State != ScopeState.Open && node.Last < first && node.Children == 0)
                {
                    _forgotten.Add(pair.Key);
                    _free.Push(node);
                }
            }

            foreach (long scope in _forgotten)
                _nodes.Remove(scope);
            _forgotten.Clear();
        }

        private Node NodeOf(long scope, out bool known)
        {
            known = _nodes.TryGetValue(scope, out Node? node);
            if (node is null)
            {
                node = Fresh();
                _nodes.Add(scope, node);
            }

            return node;
        }

        /// <summary>A node that knows nothing yet: one a forgotten scope left, or a new one when none did.</summary>
        private Node Fresh()
        {
            if (_free.Count == 0)
                return new Node();

            Node node = _free.Pop();
            node.Forget();
            return node;
        }

        /// <summary>Looks <paramref name="scope"/> up in the records, and the scopes it was opened in, until one is known.</summary>
        private void LookUp(long scope)
        {
            Span<long> one = stackalloc long[1];
            for (int looked = 0; scope != 0 && looked < MostLookedUp && !_nodes.ContainsKey(scope); looked++)
            {
                one[0] = scope;
                Node node = Fresh();
                node.Last = scope;
                if (_source.Read(one, _one) == 1 && _one[0].Kind == LogRecordKind.ScopeBegin)
                {
                    node.Parent = _one[0].Scope;
                    node.State = ScopeState.Open;
                }

                _nodes.Add(scope, node);
                scope = node.Parent;
            }

            _one.Clear();
        }

        private enum ScopeState : byte
        {
            Unknown,
            Open,
            Closed,
        }

        /// <summary>What is known of one scope, changed in place as its records are learnt.</summary>
        private sealed class Node
        {
            public long Parent;
            public long Last;
            public TimeSpan Elapsed;
            public int Children;
            public ScopeState State;

            /// <summary>Knows nothing again, for a scope learnt after the one it was of was forgotten.</summary>
            public void Forget()
            {
                Parent = 0;
                Last = 0;
                Elapsed = TimeSpan.Zero;
                Children = 0;
                State = ScopeState.Unknown;
            }
        }
    }
}
