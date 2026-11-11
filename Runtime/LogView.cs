using System;
using System.Collections.Generic;
using System.Globalization;
using Tutan.Functional;

namespace Tutan.Logging
{
    /// <summary>
    /// What a console reads: the records of a store or an archive that a filter keeps, as rows kept up
    /// to date a frame at a time, with nothing about drawing in it. A row is a record, or a run of
    /// records that repeat one another, which it counts rather than shows again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Update"/> reads what arrived since it last did and drops the rows the records no
    /// longer hold, and allocates nothing once its index has grown to them. A new filter, or a scope
    /// folded or unfolded, scans every record held again, a few thousand a frame, into an index of its
    /// own, and the rows stay as they were until the scan has caught up; <see cref="IsScanning"/> says
    /// when one is under way. Until then, what the rows are asked - which records a row stands for,
    /// what a copy of them writes, which scopes are folded - is answered as the filter and folds they
    /// were made with, <see cref="Shown"/>, say.
    /// </para>
    /// <para>
    /// Records an archive was given late, as an error's context is, are placed among the rows: the
    /// rows from the first of them on are read again, and only those.
    /// </para>
    /// <para>
    /// It is read and updated on one thread, as a console draws on one.
    /// </para>
    /// </remarks>
    public sealed class LogView
    {
        /// <summary>How many records a scan reads in one update.</summary>
        private const int ScanBudget = 16_384;

        private readonly int _budget;
        private readonly LogBatch _batch;

        // The folds asked for, which the next scan makes rows of; the rows shown keep their own.
        private readonly HashSet<long> _folded = new HashSet<long>();
        private ViewState _state;
        private ViewState _spare;
        private LogFilter _filter;
        private bool _scanning;
        private long _scanUntil;
        private long _from;
        private LogLateMark _late;
        private long[] _sequences = new long[64];

        /// <summary>A view of the records of <paramref name="source"/> that <paramref name="filter"/> keeps, which the first <see cref="Update"/> reads.</summary>
        public LogView(LogRecords source, LogFilter filter)
            : this(source, filter, ScanBudget)
        {
        }

        internal LogView(LogRecords source, LogFilter filter, int budget)
        {
            if (budget < 1)
                throw new ArgumentOutOfRangeException(nameof(budget), $"A scan reads at least one record an update, and {budget.ToString(CultureInfo.InvariantCulture)} was given.");

            Source = source ?? throw new ArgumentNullException(nameof(source));
            _filter = filter ?? throw new ArgumentNullException(nameof(filter));
            Scopes = new LogScopes(source);
            _budget = budget;
            _batch = new LogBatch(Math.Min(budget, 1024));
            _state = new ViewState(source.Capacity);
            _spare = new ViewState(source.Capacity);
            _late = source.LateMark;
            _state.Reset(source.First, filter, _folded);
            Rescan();
        }

        /// <summary>The records it reads.</summary>
        public LogRecords Source { get; }

        /// <summary>The scopes it has read, which its filter's <c>scope:</c> asks and a console draws a row's chain from.</summary>
        public LogScopes Scopes { get; }

        /// <summary>
        /// Which records it is to show. Setting a filter that is not equal to it scans the records
        /// again, and the rows are its once the scan has caught up; <see cref="Shown"/> is theirs.
        /// Setting an equal one does nothing; <see cref="Retry"/> scans again.
        /// </summary>
        public LogFilter Filter
        {
            get => _filter;
            set
            {
                if (value is null)
                    throw new ArgumentNullException(nameof(value));

                if (value.Equals(_filter))
                    return;

                _filter = value;
                Rescan();
            }
        }

        /// <summary>The filter the rows were made with: <see cref="Filter"/>, but while a scan for a new one is under way.</summary>
        public LogFilter Shown => _state.Filter;

        /// <summary>
        /// The terms of <see cref="Shown"/> whose regular expression took too long over a record, in the
        /// filter's order: each matches nothing in the rows from that record on, which keeps nothing
        /// when it is kept and leaves nothing out when it is left out. A console says so under its
        /// filter line, and <see cref="Retry"/> tries them afresh.
        /// </summary>
        public IReadOnlyList<LogFilterTerm> GaveUp => _state.Pass.GaveUp;

        /// <summary>Whether a scan is under way, for a new filter or fold, whose rows replace these when it catches up.</summary>
        public bool IsScanning => _scanning;

        /// <summary>How many rows it has.</summary>
        public int Count => _state.Count;

        /// <summary>The row at <paramref name="row"/>, from 0, the oldest, to <see cref="Count"/>.</summary>
        public LogRow this[int row] => _state[row];

        /// <summary>How many records the records held match, by level: everything the filter keeps but for its levels, so a level's count is what keeping it would show.</summary>
        public LogLevelCounts Counts => _state.Counts;

        /// <summary>
        /// How many records the records held match of the event at <paramref name="place"/> in the
        /// events of <see cref="Source"/>: everything the filter keeps but for its event terms, so an
        /// event's count is what keeping it would show, and a category's is its events'. None for an
        /// event declared since the view last read.
        /// </summary>
        public long EventCount(int place)
        {
            if (place < 0)
                throw new ArgumentOutOfRangeException(nameof(place), $"An event's place is 0 or more, and {place.ToString(CultureInfo.InvariantCulture)} was given.");

            return _state.EventCount(place);
        }

        /// <summary>How many records were lost before this view read them: overwritten by the store, or lost before a sink kept them.</summary>
        public long Lost => _state.Lost;

        /// <summary>
        /// Scans the records again when a regular expression of <see cref="Filter"/> took too long, so
        /// it is tried afresh, and the rows are the scan's once it has caught up; does nothing when none did.
        /// </summary>
        public void Retry()
        {
            // The pass of the filter set last: the scan's while one is under way.
            if ((_scanning ? _spare : _state).Pass.GaveUp.Count > 0)
                Rescan();
        }

        /// <summary>Reads what arrived since the last update, or goes on with a scan; once a frame.</summary>
        public void Update()
        {
            LogLate late = Source.LateFrom(_late);
            if (late.Mark != _late)
            {
                _late = late.Mark;
                (_scanning ? _spare : _state).Rewind(Math.Max(late.From, _from), Source);
            }

            if (_scanning)
            {
                _state.DropBefore(Source.First);
                Take(_spare, _budget, _scanUntil);
                if (_spare.Next < _scanUntil)
                    return;

                (_state, _spare) = (_spare, _state);
                _scanning = false;
            }

            Take(_state, int.MaxValue, long.MaxValue);
            Scopes.Prune();
        }

        /// <summary>Starts the view at the newest record, as if nothing came before it. The records are all still there; a <see cref="Filter"/> set after reads only what came after.</summary>
        public void Clear()
        {
            _from = Source.Next;
            Rescan();
        }

        /// <summary>Hides what is under <paramref name="scope"/>, and shows its own row; scans again.</summary>
        /// <param name="scope">The scope: the sequence of the record that opened it.</param>
        public void Fold(long scope)
        {
            CheckScope(scope);
            if (_folded.Add(scope))
                Rescan();
        }

        /// <summary>Shows what is under <paramref name="scope"/> again; scans again.</summary>
        /// <param name="scope">The scope: the sequence of the record that opened it.</param>
        public void Unfold(long scope)
        {
            CheckScope(scope);
            if (_folded.Remove(scope))
                Rescan();
        }

        /// <summary>Whether the rows hide what is under <paramref name="scope"/>: so a fold asked for while a scan is under way says so once its rows are shown.</summary>
        public bool IsFolded(long scope) => _state.IsFolded(scope);

        /// <summary>
        /// The row that holds record <paramref name="sequence"/>, or the repeat whose run spans it, if
        /// one does: so a selection held by sequence keeps its row when the rows change.
        /// </summary>
        public Optional<int> Find(long sequence)
        {
            int low = 0;
            int high = _state.Count - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                LogRow row = _state[middle];
                if (sequence < row.Sequence)
                    high = middle - 1;
                else if (sequence > row.Last)
                    low = middle + 1;
                else
                    return middle;
            }

            return F.None;
        }

        /// <summary>
        /// Copies the records <paramref name="count"/> rows from <paramref name="row"/> stand for into
        /// <paramref name="into"/>, one a row, in order, and says how many: a row whose records the
        /// store has overwritten since has none, so a reader matches a record to its row by sequence.
        /// A repeat's record is its first while the store holds it, and its last after.
        /// </summary>
        public int Read(int row, int count, LogBatch into)
        {
            if (into is null)
                throw new ArgumentNullException(nameof(into));
            CheckRows(row, count);

            if (_sequences.Length < count)
                _sequences = new long[ViewState.PowerOfTwo(count)];

            long first = Source.First;
            for (int i = 0; i < count; i++)
            {
                LogRow at = _state[row + i];
                _sequences[i] = at.Sequence >= first ? at.Sequence : at.Last;
            }

            return Source.Read(_sequences.AsSpan(0, count), into);
        }

        /// <summary>
        /// Writes to <paramref name="sink"/> every record that <paramref name="count"/> rows from
        /// <paramref name="row"/> stand for, a repeat's every record, as a drain would, from opening it
        /// to flushing it: so a console copies rows as text or as JSON lines, or exports what it shows.
        /// A scope whose opening it writes has its closing written too while the records hold it, though
        /// it lies after the last row. Where the records say some were lost among them, or the store has
        /// overwritten some since, the sink is told how many.
        /// </summary>
        /// <param name="sink">A sink that has not been opened.</param>
        /// <param name="row">The first row.</param>
        /// <param name="count">How many rows.</param>
        /// <param name="options">What it writes with: every level, and nothing left out, when none are given.</param>
        public void Write(ILogSink sink, int row, int count, LogSinkOptions? options = null)
        {
            if (sink is null)
                throw new ArgumentNullException(nameof(sink));
            CheckRows(row, count);

            Writing writing = Open(sink, options);
            if (count > 0)
            {
                long until = WriteRun(writing, row, count);
                WriteClosings(writing, until, Source.Next);
            }

            sink.Flush();
        }

        /// <summary>
        /// Writes to <paramref name="sink"/> the records of each of <paramref name="rows"/>, as the
        /// overload that takes a run of rows does, through one opening of the sink: so a console copies
        /// a selection that is not one run, and JSON lines have one header.
        /// </summary>
        /// <param name="sink">A sink that has not been opened.</param>
        /// <param name="rows">The rows, in order, each once.</param>
        /// <param name="options">What it writes with: every level, and nothing left out, when none are given.</param>
        public void Write(ILogSink sink, IReadOnlyList<int> rows, LogSinkOptions? options = null)
        {
            if (sink is null)
                throw new ArgumentNullException(nameof(sink));
            if (rows is null)
                throw new ArgumentNullException(nameof(rows));

            for (int i = 0; i < rows.Count; i++)
            {
                CheckRows(rows[i], 1);
                if (i > 0 && rows[i] <= rows[i - 1])
                    throw new ArgumentException($"The rows are in order, each once, and {rows[i].ToString(CultureInfo.InvariantCulture)} follows {rows[i - 1].ToString(CultureInfo.InvariantCulture)}.", nameof(rows));
            }

            Writing writing = Open(sink, options);
            long written = -1;
            for (int first = 0; first < rows.Count;)
            {
                int end = first + 1;
                while (end < rows.Count && rows[end] == rows[end - 1] + 1)
                    end++;

                if (written >= 0)
                    WriteClosings(writing, written, _state[rows[first]].Sequence);
                written = WriteRun(writing, rows[first], end - first);
                first = end;
            }

            if (written >= 0)
                WriteClosings(writing, written, Source.Next);
            sink.Flush();
        }

        private Writing Open(ILogSink sink, LogSinkOptions? options)
        {
            // The rows' pass as it stands, so the copy leaves out what the rows do; a regular
            // expression that gives up over the copy gives up over it alone.
            var scopes = new ScopeTable();
            var context = new LogContext(Source, options ?? new LogSinkOptions(LogLevel.Trace), scopes);
            var writing = new Writing(sink, _state.Pass.Copy(), scopes, context);
            sink.Open(context);
            return writing;
        }

        /// <summary>Writes the records of <paramref name="count"/> rows from <paramref name="row"/>, which lie together; says the sequence after the last.</summary>
        private long WriteRun(Writing writing, int row, int count)
        {
            ILogSink sink = writing.Sink;
            LogContext context = writing.Context;
            LogBatch batch = writing.Batch;
            long until = _state[row + count - 1].Last + 1;
            for (long from = _state[row].Sequence; from < until;)
            {
                LogRead read = Source.Read(from, batch);
                if (read.Lost > 0)
                    sink.Lost(read.Lost, context);

                for (int i = 0; i < batch.Count && batch[i].Sequence < until; i++)
                {
                    LogRecord record = batch[i];
                    if (record.Kind == LogRecordKind.ScopeBegin)
                        writing.Scopes.Open(batch, i);

                    if (record.Kind == LogRecordKind.ScopeEnd)
                    {
                        // The closing of an opening written, whatever the filter, as the opening's row says how long it took.
                        if (writing.Written.Remove(record.Begin))
                            sink.Write(record, record.IsBacklog, context);

                        writing.Scopes.Close(record.Begin);
                    }
                    else if (record.Level >= context.Options.Least && writing.Pass.Matches(record, Scopes) && !UnderFolded(_state, record.Scope))
                    {
                        Opened(writing.Scopes, record.Scope, writing.Openings);
                        sink.Write(record, record.IsBacklog, context);
                        if (record.Kind == LogRecordKind.ScopeBegin)
                            writing.Written.Add(record.Sequence);
                    }
                }

                if (read.Count == 0)
                    break;
                from = read.Next;
            }

            return until;
        }

        /// <summary>
        /// Writes the closings of the openings written that lie from <paramref name="from"/> to
        /// <paramref name="until"/>, where no row is written: so a scope that closed between the rows,
        /// or after the last, says how long it took in the copy as its row does. Nothing else there is
        /// written, and no loss there is told, as neither is among the rows.
        /// </summary>
        private void WriteClosings(Writing writing, long from, long until)
        {
            LogBatch batch = writing.Batch;
            while (writing.Written.Count > 0 && from < until)
            {
                LogRead read = Source.Read(from, batch);
                for (int i = 0; i < batch.Count && batch[i].Sequence < until; i++)
                {
                    LogRecord record = batch[i];
                    if (record.Kind != LogRecordKind.ScopeEnd)
                        continue;

                    if (writing.Written.Remove(record.Begin))
                        writing.Sink.Write(record, record.IsBacklog, writing.Context);
                    writing.Scopes.Close(record.Begin);
                }

                if (read.Count == 0)
                    break;
                from = read.Next;
            }
        }

        private static void CheckScope(long scope)
        {
            if (scope < 1)
                throw new ArgumentOutOfRangeException(nameof(scope), $"A scope is the sequence of the record that opened it, which counts from 1, and {scope.ToString(CultureInfo.InvariantCulture)} was given.");
        }

        // The openings of the scopes a record is under that the records written so far did not open,
        // read by their sequences while the records still hold them: a row is written under a scope
        // opened before the first row, or a filter left its opening out, and the sink says what the
        // row is under rather than a number. Only what the sink looks up; none of it is written.
        private void Opened(ScopeTable scopes, long scope, LogBatch openings)
        {
            Span<long> one = stackalloc long[1];
            while (scope != 0 && !scopes.TryGet(scope, out _))
            {
                one[0] = scope;
                if (Source.Read(one, openings) != 1)
                    return;

                scopes.Open(openings, 0);
                scope = openings[0].Scope;
            }
        }

        private void CheckRows(int row, int count)
        {
            if (row < 0)
                throw new ArgumentOutOfRangeException(nameof(row), $"A row is 0 or more, and {row.ToString(CultureInfo.InvariantCulture)} was given.");
            if (count < 0)
                throw new ArgumentOutOfRangeException(nameof(count), $"A count of rows is 0 or more, and {count.ToString(CultureInfo.InvariantCulture)} was given.");
            if ((long)row + count > _state.Count)
                throw new ArgumentOutOfRangeException(nameof(count), $"The view has {_state.Count.ToString(CultureInfo.InvariantCulture)} rows, and {count.ToString(CultureInfo.InvariantCulture)} from {row.ToString(CultureInfo.InvariantCulture)} were asked for.");
        }

        private void Rescan()
        {
            _spare.Reset(Math.Max(_from, Source.First), _filter, _folded);
            _scanUntil = Source.Next;
            _scanning = true;
        }

        /// <summary>Reads into <paramref name="state"/> until it has read <paramref name="budget"/> records, or up to <paramref name="until"/>, or all there is.</summary>
        private void Take(ViewState state, int budget, long until)
        {
            state.DropBefore(Source.First);
            for (int taken = 0; taken < budget && state.Next < Source.Next && state.Next < until;)
            {
                LogRead read = Source.Read(state.Next, _batch);
                state.Lose(read.First, read.Lost);
                for (int i = 0; i < _batch.Count; i++)
                    Take(state, i);

                state.Next = read.Next;
                taken += read.Count;
                if (read.Count == 0)
                    break;
            }
        }

        private void Take(ViewState state, int index)
        {
            LogRecord record = _batch[index];
            Scopes.Learn(record);
            if (record.Kind == LogRecordKind.ScopeEnd)
                return;

            // A level's badge counts what the filter keeps but for its levels, and an event's count
            // what it keeps but for its event terms, so a record that meets neither counts for nothing.
            bool ofEvent = state.Filter.KeepsEvent(record.Event);
            bool ofLevel = state.Filter.Levels.Contains(record.Level);
            if ((!ofEvent && !ofLevel) || !state.Pass.MeetsOthers(record, Scopes))
                return;

            state.Tally(record.Sequence, record.Level, _batch.Entry(index).Event, byLevel: ofEvent, byEvent: ofLevel);
            if (!ofEvent || !ofLevel || UnderFolded(state, record.Scope))
                return;

            if (!state.Repeat(_batch, index))
                state.Add(_batch, index, Depth(state, record.Scope));
        }

        private bool UnderFolded(ViewState state, long scope)
        {
            if (scope == 0)
                return false;

            foreach (long folded in state.Folded)
            {
                if (Scopes.IsUnder(scope, folded))
                    return true;
            }

            return false;
        }

        /// <summary>How many of the scopes a record is under have rows of their own, which is how far in its row is drawn.</summary>
        private int Depth(ViewState state, long scope)
        {
            int depth = 0;
            for (int steps = 0; scope != 0 && steps < 256; steps++)
            {
                if (state.HasScopeRow(scope))
                    depth++;
                scope = Scopes.Parent(scope);
            }

            return depth;
        }

        /// <summary>
        /// The rows of one filter and fold, which it holds, and what it counted: a ring of rows that
        /// drops the front as the records it shows are overwritten, and a ring of what it counted,
        /// which it uncounts the same way. Both give back their tails when records are placed among
        /// what they read.
        /// </summary>
        private sealed class ViewState
        {
            /// <summary>The most rows a ring grows to, past which it drops its oldest.</summary>
            private const int MostRows = 1 << 30;

            private readonly int _most;
            private readonly HashSet<long> _folded = new HashSet<long>();
            private readonly long[] _counts = new long[6];
            private long[] _eventCounts = new long[64];
            private readonly LogBatch _last = new LogBatch(1);
            private readonly HashSet<long> _scopeRows = new HashSet<long>();

            // The losses the reads told of that a row dropped from the front has not yet carried away,
            // in the order told, so a rewind takes back the ones it reads again.
            private readonly List<Loss> _losses = new List<Loss>();
            private LogRow[] _rows = new LogRow[256];
            private int _head;
            private int _count;
            private Counted[] _counted = new Counted[256];
            private int _countedHead;
            private int _countedCount;
            private long _pendingLost;
            private long _droppedThrough = -1;

            public ViewState(int capacity) => _most = PowerOfTwo(Math.Min(Math.Max(capacity, 256), MostRows));

            public int Count => _count;

            public long Next { get; set; }

            public long Lost { get; private set; }

            /// <summary>The filter the rows are of, as their pass over the records applies it.</summary>
            public FilterPass Pass { get; private set; } = new FilterPass(LogFilter.All);

            public LogFilter Filter => Pass.Filter;

            public HashSet<long> Folded => _folded;

            public LogRow this[int row]
            {
                get
                {
                    if ((uint)row >= (uint)_count)
                        throw new ArgumentOutOfRangeException(nameof(row), $"The view has {_count.ToString(CultureInfo.InvariantCulture)} rows, and {row.ToString(CultureInfo.InvariantCulture)} was asked for.");

                    return _rows[(_head + row) & (_rows.Length - 1)];
                }
            }

            public LogLevelCounts Counts => new LogLevelCounts(_counts[0], _counts[1], _counts[2], _counts[3], _counts[4], _counts[5]);

            public long EventCount(int place) => place < _eventCounts.Length ? _eventCounts[place] : 0;

            private int Tail => (_head + _count - 1) & (_rows.Length - 1);

            public bool HasScopeRow(long scope) => _scopeRows.Contains(scope);

            public bool IsFolded(long scope) => _folded.Contains(scope);

            public void Reset(long next, LogFilter filter, HashSet<long> folded)
            {
                Pass = new FilterPass(filter);
                _folded.Clear();
                _folded.UnionWith(folded);
                _head = 0;
                _count = 0;
                _countedHead = 0;
                _countedCount = 0;
                Array.Clear(_counts, 0, _counts.Length);
                Array.Clear(_eventCounts, 0, _eventCounts.Length);
                _last.Clear();
                _scopeRows.Clear();
                _losses.Clear();
                _pendingLost = 0;
                _droppedThrough = -1;
                Lost = 0;
                Next = next;
            }

            /// <summary>Counts <paramref name="count"/> records lost before <paramref name="sequence"/>, which the next row carries.</summary>
            public void Lose(long sequence, long count)
            {
                if (count == 0)
                    return;

                Lost += count;
                _pendingLost += count;
                _losses.Add(new Loss(sequence, count));
            }

            /// <summary>Drops the rows and the counts of records before <paramref name="first"/>, which the records no longer hold.</summary>
            public void DropBefore(long first)
            {
                while (_count > 0 && _rows[_head].Last < first)
                    DropRow();

                while (_countedCount > 0 && _counted[_countedHead].Sequence < first)
                    DropCounted();

                // A loss told at or before a dropped row was carried by it, or by a row before it.
                int carried = 0;
                while (carried < _losses.Count && _losses[carried].Sequence <= _droppedThrough)
                    carried++;
                if (carried > 0)
                    _losses.RemoveRange(0, carried);
            }

            /// <summary>
            /// Gives back the rows, counts and losses of the records from <paramref name="from"/> on, a
            /// repeat whose run reaches it whole, so they are read again with what was placed among them.
            /// </summary>
            public void Rewind(long from, LogRecords source)
            {
                if (from >= Next)
                    return;

                while (_count > 0 && _rows[Tail].Last >= from)
                {
                    from = Math.Min(from, _rows[Tail].Sequence);
                    DropTail();
                }

                while (_countedCount > 0 && _counted[(_countedHead + _countedCount - 1) & (_counted.Length - 1)].Sequence >= from)
                    DropCountedTail();

                while (_losses.Count > 0 && _losses[_losses.Count - 1].Sequence >= from)
                {
                    Lost -= _losses[_losses.Count - 1].Count;
                    _losses.RemoveAt(_losses.Count - 1);
                }

                // What the rows left carried, they carry still; a loss told after the last of them
                // is carried by the next row again.
                long carried = _count > 0 ? _rows[Tail].Sequence : _droppedThrough;
                _pendingLost = 0;
                foreach (Loss loss in _losses)
                {
                    if (loss.Sequence > carried)
                        _pendingLost += loss.Count;
                }

                // The record the last row repeats, read again, so a repeat read after it is counted in.
                _last.Clear();
                if (_count > 0)
                {
                    LogRow tail = _rows[Tail];
                    Span<long> one = stackalloc long[1];
                    one[0] = tail.Sequence >= source.First ? tail.Sequence : tail.Last;
                    source.Read(one, _last);
                }

                // Not before the records held: what is before them was read whole, and told of no loss.
                Next = Math.Max(from, source.First);
            }

            /// <summary>Counts a record: by its level when <paramref name="byLevel"/>, and by its event when <paramref name="byEvent"/>.</summary>
            public void Tally(long sequence, LogLevel level, int @event, bool byLevel, bool byEvent)
            {
                if (_countedCount == _counted.Length)
                {
                    if (_counted.Length < _most)
                    {
                        _counted = Grow(_counted, _countedHead, _countedCount);
                        _countedHead = 0;
                    }
                    else
                    {
                        DropCounted();
                    }
                }

                if (byLevel && (uint)level < (uint)_counts.Length)
                    _counts[(int)level]++;
                if (byEvent)
                {
                    // Grown only when an event is declared, which is rare once a program has started.
                    if (@event >= _eventCounts.Length)
                        Array.Resize(ref _eventCounts, PowerOfTwo(@event + 1));
                    _eventCounts[@event]++;
                }

                CountedBy by = (byLevel ? CountedBy.Level : 0) | (byEvent ? CountedBy.Event : 0);
                _counted[(_countedHead + _countedCount++) & (_counted.Length - 1)] = new Counted(sequence, level, @event, by);
            }

            /// <summary>Folds record <paramref name="index"/> of <paramref name="batch"/> into the last row, if it repeats it: an event of the same event, level, scope, values and stack.</summary>
            public bool Repeat(LogBatch batch, int index)
            {
                if (_count == 0 || _pendingLost > 0 || _last.Count == 0)
                    return false;

                ref readonly LogEntry entry = ref batch.Entry(index);
                ref readonly LogEntry last = ref _last.Entry(0);
                if (entry.Kind != LogRecordKind.Event || last.Kind != LogRecordKind.Event || entry.Event != last.Event
                    || entry.Level != last.Level || entry.Scope != last.Scope || entry.Backlog != last.Backlog
                    || !batch.Bits(index).SequenceEqual(_last.Bits(0)) || !SameRefs(batch.Refs(index), _last.Refs(0)))
                    return false;

                ref LogRow row = ref _rows[Tail];
                row = row with { Last = entry.Sequence, Records = row.Records + 1, Suppressed = row.Suppressed + entry.Suppressed };
                return true;
            }

            public void Add(LogBatch batch, int index, int depth)
            {
                if (_count == _rows.Length)
                {
                    if (_rows.Length < _most)
                    {
                        _rows = Grow(_rows, _head, _count);
                        _head = 0;
                    }
                    else
                    {
                        DropRow();
                    }
                }

                ref readonly LogEntry entry = ref batch.Entry(index);
                LogRowKind kind = entry.Kind == LogRecordKind.ScopeBegin ? LogRowKind.Scope : LogRowKind.Event;
                _rows[(_head + _count++) & (_rows.Length - 1)] = new LogRow(entry.Sequence, entry.Sequence, 1, entry.Suppressed,
                    entry.Level, kind, depth, _pendingLost);
                _pendingLost = 0;
                if (entry.Kind == LogRecordKind.ScopeBegin)
                    _scopeRows.Add(entry.Sequence);

                _last.CopyFrom(batch, index);
            }

            /// <summary>The least power of two that is <paramref name="value"/> or more, for a value of at most 2^30.</summary>
            public static int PowerOfTwo(int value)
            {
                int power = 1;
                while (power < value)
                    power <<= 1;
                return power;
            }

            private void DropRow()
            {
                LogRow row = _rows[_head];
                if (row.Kind == LogRowKind.Scope)
                    _scopeRows.Remove(row.Sequence);

                _droppedThrough = row.Sequence;
                _head = (_head + 1) & (_rows.Length - 1);
                if (--_count == 0)
                    _last.Clear();
            }

            private void DropTail()
            {
                LogRow row = _rows[Tail];
                if (row.Kind == LogRowKind.Scope)
                    _scopeRows.Remove(row.Sequence);

                _count--;
            }

            private void DropCounted()
            {
                Uncount(_counted[_countedHead]);
                _countedHead = (_countedHead + 1) & (_counted.Length - 1);
                _countedCount--;
            }

            private void DropCountedTail()
            {
                Uncount(_counted[(_countedHead + _countedCount - 1) & (_counted.Length - 1)]);
                _countedCount--;
            }

            private void Uncount(Counted counted)
            {
                if ((counted.By & CountedBy.Level) != 0 && (uint)counted.Level < (uint)_counts.Length)
                    _counts[(int)counted.Level]--;
                if ((counted.By & CountedBy.Event) != 0)
                    _eventCounts[counted.Event]--;
            }

            private static bool SameRefs(ReadOnlySpan<object?> a, ReadOnlySpan<object?> b)
            {
                if (a.Length != b.Length)
                    return false;

                for (int i = 0; i < a.Length; i++)
                {
                    if (!SameRef(a[i], b[i]))
                        return false;
                }

                return true;
            }

            /// <summary>
            /// Whether two refs are one: a string and a stack by value, so a repeat is the same message
            /// reached the same way, and an exception only as itself.
            /// </summary>
            private static bool SameRef(object? a, object? b) =>
                ReferenceEquals(a, b) || a switch
                {
                    string x => b is string y && x == y,
                    LogStack x => x.Equals(b as LogStack),
                    _ => false,
                };

            /// <summary>A ring twice the size, its items moved to the front in order.</summary>
            private static T[] Grow<T>(T[] ring, int head, int count)
            {
                var grown = new T[ring.Length * 2];
                for (int i = 0; i < count; i++)
                    grown[i] = ring[(head + i) & (ring.Length - 1)];

                return grown;
            }

            /// <summary>Which counts a record is in: its level's, its event's, or both.</summary>
            [Flags]
            private enum CountedBy : byte
            {
                Level = 1,
                Event = 2,
            }

            private readonly struct Counted
            {
                public Counted(long sequence, LogLevel level, int @event, CountedBy by)
                {
                    Sequence = sequence;
                    Event = @event;
                    Level = level;
                    By = by;
                }

                public long Sequence { get; }

                public int Event { get; }

                public LogLevel Level { get; }

                public CountedBy By { get; }
            }

            private readonly struct Loss
            {
                public Loss(long sequence, long count)
                {
                    Sequence = sequence;
                    Count = count;
                }

                public long Sequence { get; }

                public long Count { get; }
            }
        }
    }

    /// <summary>What one write of rows to a sink reads with and remembers, from opening the sink to flushing it.</summary>
    internal sealed class Writing
    {
        public Writing(ILogSink sink, FilterPass pass, ScopeTable scopes, LogContext context)
        {
            Sink = sink;
            Pass = pass;
            Scopes = scopes;
            Context = context;
        }

        public ILogSink Sink { get; }

        public FilterPass Pass { get; }

        public ScopeTable Scopes { get; }

        public LogContext Context { get; }

        public LogBatch Batch { get; } = new LogBatch();

        public LogBatch Openings { get; } = new LogBatch(1);

        /// <summary>The scopes whose opening was written and whose closing has not been.</summary>
        public HashSet<long> Written { get; } = new HashSet<long>();
    }

    /// <summary>
    /// A row of a <see cref="LogView"/>: a record, or a run of records of one event with the same
    /// values, each after the last with only records the filter hides between them.
    /// </summary>
    /// <param name="Sequence">The first record's sequence; a scope's row's is the scope's id.</param>
    /// <param name="Last">The last record's sequence: <paramref name="Sequence"/> but for a repeat.</param>
    /// <param name="Records">How many records it stands for: 1, or a repeat's count.</param>
    /// <param name="Suppressed">How many records of its event a limit dropped between them and before the first.</param>
    /// <param name="Level">Its records' level.</param>
    /// <param name="Kind">An event's record, or a scope's opening.</param>
    /// <param name="Depth">How many of the scopes it is under have rows of their own: how far in it is drawn.</param>
    /// <param name="LostBefore">How many records were lost before the view read them, just before it.</param>
    public readonly record struct LogRow(long Sequence, long Last, int Records, long Suppressed, LogLevel Level, LogRowKind Kind,
        int Depth, long LostBefore);

    /// <summary>What a row of a <see cref="LogView"/> is: a scope's closing never has one, since its opening's row says how long it was.</summary>
    public enum LogRowKind : byte
    {
        /// <summary>An event's record, or a run of them.</summary>
        Event,

        /// <summary>A scope's opening, which what is under it is drawn under.</summary>
        Scope,
    }

    /// <summary>How many records of each level a view counted.</summary>
    /// <param name="Trace">At <see cref="LogLevel.Trace"/>.</param>
    /// <param name="Debug">At <see cref="LogLevel.Debug"/>.</param>
    /// <param name="Information">At <see cref="LogLevel.Information"/>.</param>
    /// <param name="Warning">At <see cref="LogLevel.Warning"/>.</param>
    /// <param name="Error">At <see cref="LogLevel.Error"/>.</param>
    /// <param name="Critical">At <see cref="LogLevel.Critical"/>.</param>
    public readonly record struct LogLevelCounts(long Trace, long Debug, long Information, long Warning, long Error, long Critical)
    {
        /// <summary>How many at <paramref name="level"/>; none at <see cref="LogLevel.None"/>, which no record has.</summary>
        public long this[LogLevel level] => level switch
        {
            LogLevel.Trace => Trace,
            LogLevel.Debug => Debug,
            LogLevel.Information => Information,
            LogLevel.Warning => Warning,
            LogLevel.Error => Error,
            LogLevel.Critical => Critical,
            _ => 0,
        };

        /// <summary>How many at every level.</summary>
        public long Total => Trace + Debug + Information + Warning + Error + Critical;
    }
}
