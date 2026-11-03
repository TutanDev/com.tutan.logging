using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Tutan.Functional;

namespace Tutan.Logging
{
    /// <summary>
    /// Which records a <see cref="LogView"/> shows: a range of levels, a range of frames, the terms a
    /// record must meet, and the terms it must not. A console's filter line writes it as a query,
    /// which <see cref="Parse"/> reads and <see cref="ToString"/> writes back:
    /// <code>
    /// level>=warning  game.*  -game.input/Moved  Slot:3  frame:1200..1300  scope:4182  "no image"  /fail(ed|ure)/
    /// </code>
    /// </summary>
    /// <remarks>
    /// <para>
    /// A record is kept when its level and frame are in the ranges, it is of one of the kept events,
    /// if any are kept, it meets every other kept term, and it meets no term left out. So
    /// <c>game.save game.load</c> is either category, and <c>game.* Slot:3</c> both.
    /// </para>
    /// <para>
    /// A query is words split by spaces, each a term, or a term left out when it starts with
    /// <c>-</c>:
    /// </para>
    /// <list type="bullet">
    /// <item><c>level>=warning</c>, with <c>></c>, <c>&lt;=</c>, <c>&lt;</c>, <c>=</c> or <c>:</c> too, and a level as rules name it.</item>
    /// <item><c>frame:1200..1300</c>, or <c>frame:1200..</c>, <c>frame:..1300</c>, or <c>frame:1240</c>.</item>
    /// <item><c>scope:4182</c>: the scope and everything under it, on every thread it reached.</item>
    /// <item><c>thread:7</c>: what one managed thread wrote.</item>
    /// <item><c>File:*.png</c>: a field by its name, ignoring case, whose formatted value the pattern matches, ignoring case, with <c>*</c> for any text and <c>?</c> for any char.</item>
    /// <item><c>File:/\.(png|jpg)$/</c>: a field whose formatted value a regular expression matches, ignoring case.</item>
    /// <item><c>@level:high</c>: a field whose name is <c>level</c>, <c>frame</c>, <c>scope</c> or <c>thread</c>, which without the <c>@</c> are the terms above.</item>
    /// <item><c>"no image"</c>: text in the message, ignoring case.</item>
    /// <item><c>/fail(ed|ure)/</c>: a regular expression that matches the message, ignoring case.</item>
    /// <item>anything else: an event, as a rule names it: <c>*</c>, a category, a category and everything under it, or a key.</item>
    /// </list>
    /// <para>
    /// A value with a space in it is in quotes, in which <c>\"</c> is a quote and <c>\\</c> a
    /// backslash. A regular expression is between slashes, in which <c>\/</c> is a slash and every
    /// other backslash is the expression's own.
    /// </para>
    /// </remarks>
    public sealed record LogFilter
    {
        private static readonly LogFilterTerm[] s_none = Array.Empty<LogFilterTerm>();
        private static readonly ReadOnlyCollection<LogFilterTerm> s_noneList = Array.AsReadOnly(s_none);

        // Arrays, which a record is matched against without allocating, handed out read-only.
        private readonly LogFilterTerm[] _keep = s_none;
        private readonly LogFilterTerm[] _leaveOut = s_none;
        private readonly ReadOnlyCollection<LogFilterTerm> _keepList = s_noneList;
        private readonly ReadOnlyCollection<LogFilterTerm> _leaveOutList = s_noneList;

        /// <summary>A filter of the levels and frames given, and the terms kept and left out.</summary>
        /// <exception cref="ArgumentNullException">A list, or a term in one, is null.</exception>
        public LogFilter(LogLevelRange levels, LogFrameRange frames, IReadOnlyList<LogFilterTerm> keep, IReadOnlyList<LogFilterTerm> leaveOut)
        {
            Levels = levels;
            Frames = frames;
            Keep = keep;
            LeaveOut = leaveOut;
        }

        /// <summary>The filter that keeps every record.</summary>
        public static LogFilter All { get; } = new LogFilter(LogLevelRange.All, LogFrameRange.All, s_none, s_none);

        /// <summary>The levels kept.</summary>
        public LogLevelRange Levels { get; init; }

        /// <summary>The frames kept.</summary>
        public LogFrameRange Frames { get; init; }

        /// <summary>The terms a record must meet: one of the events, when any is named, and every other term.</summary>
        /// <exception cref="ArgumentNullException">The list, or a term in it, is null.</exception>
        public IReadOnlyList<LogFilterTerm> Keep
        {
            get => _keepList;
            init
            {
                _keep = Copy(value, nameof(Keep));
                _keepList = ReadOnly(_keep);
            }
        }

        /// <summary>The terms a record must not meet.</summary>
        /// <exception cref="ArgumentNullException">The list, or a term in it, is null.</exception>
        public IReadOnlyList<LogFilterTerm> LeaveOut
        {
            get => _leaveOutList;
            init
            {
                _leaveOut = Copy(value, nameof(LeaveOut));
                _leaveOutList = ReadOnly(_leaveOut);
            }
        }

        /// <summary>How many terms it has, kept and left out: a term's place counts <see cref="Keep"/> first, then <see cref="LeaveOut"/>.</summary>
        internal int TermCount => _keep.Length + _leaveOut.Length;

        /// <summary>
        /// Reads a query, or says the first word of it that is not a term, with a
        /// <see cref="LogFilterError"/> as the error's code.
        /// </summary>
        public static Result<LogFilter> Parse(string query)
        {
            if (query is null)
                throw new ArgumentNullException(nameof(query));

            var reader = new QueryReader(query);
            LogLevelRange levels = LogLevelRange.All;
            LogFrameRange frames = LogFrameRange.All;
            var keep = new List<LogFilterTerm>();
            var leaveOut = new List<LogFilterTerm>();
            while (reader.More())
            {
                Result<Word> next = reader.Next();
                if (!next.IsSuccess(out Word word))
                    return F.Fail<LogFilter>(next.ErrorUnsafe());

                Result<Read> read = Term(word);
                if (!read.IsSuccess(out Read term))
                    return F.Fail<LogFilter>(read.ErrorUnsafe());

                switch (term)
                {
                    case Read.Levels when word.LeftOut:
                    case Read.Frames when word.LeftOut:
                        return F.Fail<LogFilter>(Refused(LogFilterError.CannotLeaveOut,
                            $"{word.Text} cannot be left out: say the levels or frames to keep, as level<warning or frame:..1200"));
                    case Read.Levels range:
                        levels = levels.Intersect(range.Range);
                        break;
                    case Read.Frames range:
                        frames = frames.Intersect(range.Range);
                        break;
                    case Read.Term kept:
                        (word.LeftOut ? leaveOut : keep).Add(kept.Value);
                        break;
                }
            }

            return F.Success(new LogFilter(levels, frames, keep, leaveOut));
        }

        /// <summary>
        /// Whether <paramref name="record"/> is kept, with <paramref name="scopes"/> saying what is under
        /// what. A regular expression that takes too long over it does not match it, this once.
        /// </summary>
        public bool Matches(LogRecord record, LogScopes scopes) =>
            Levels.Contains(record.Level) && MatchesTerms(record, scopes, Span<long>.Empty, out _);

        /// <summary>
        /// Whether <paramref name="record"/> is kept but for its level, which is what a level's badge
        /// counts; and whether a term's regular expression took too long over it.
        /// </summary>
        /// <param name="record">The record.</param>
        /// <param name="scopes">What is under what.</param>
        /// <param name="gaveUpFrom">
        /// By a term's place, the sequence from which it matches nothing: <see cref="long.MaxValue"/>
        /// while it has not given up. Each term that takes too long over the record gives up from it.
        /// Empty for a filter that remembers nothing, so it tries every record afresh.
        /// </param>
        /// <param name="tookTooLong">Whether a term took too long over the record.</param>
        internal bool MatchesTerms(LogRecord record, LogScopes scopes, Span<long> gaveUpFrom, out bool tookTooLong)
        {
            tookTooLong = false;
            if (!Frames.Contains(record.Frame))
                return false;

            bool anyEvent = false;
            bool ofAnEvent = false;
            for (int place = 0; place < _keep.Length; place++)
            {
                LogFilterTerm term = _keep[place];
                if (term is LogFilterTerm.Event)
                {
                    // An event's pattern is no regular expression, and never takes too long.
                    anyEvent = true;
                    ofAnEvent = ofAnEvent || term.Matches(record, scopes) == TermMatch.Yes;
                    continue;
                }

                TermMatch met = Met(term, place, record, scopes, gaveUpFrom);
                if (met == TermMatch.TookTooLong)
                {
                    GiveUp(gaveUpFrom, place, record.Sequence);
                    tookTooLong = true;
                }

                if (met != TermMatch.Yes)
                    return false;
            }

            if (anyEvent && !ofAnEvent)
                return false;

            for (int i = 0; i < _leaveOut.Length; i++)
            {
                int place = _keep.Length + i;
                TermMatch met = Met(_leaveOut[i], place, record, scopes, gaveUpFrom);
                if (met == TermMatch.Yes)
                    return false;

                // Each one left out that took too long gives up, so none takes as long over the next record.
                if (met == TermMatch.TookTooLong)
                {
                    GiveUp(gaveUpFrom, place, record.Sequence);
                    tookTooLong = true;
                }
            }

            return true;
        }

        /// <summary>The term at <paramref name="place"/>, counting <see cref="Keep"/> first, then <see cref="LeaveOut"/>.</summary>
        internal LogFilterTerm Term(int place) => place < _keep.Length ? _keep[place] : _leaveOut[place - _keep.Length];

        /// <summary>
        /// The events and fields named that no event of <paramref name="events"/> is or has, kept or
        /// left out: what a console says under its filter line, since a word that is a category no
        /// event is of is more often text meant to be in quotes.
        /// </summary>
        public IReadOnlyList<LogFilterTerm> MatchingNothing(IReadOnlyList<LogEventSpec> events)
        {
            if (events is null)
                throw new ArgumentNullException(nameof(events));

            var nothing = new List<LogFilterTerm>();
            foreach (LogFilterTerm term in _keep)
            {
                if (!Named(term, events))
                    nothing.Add(term);
            }

            foreach (LogFilterTerm term in _leaveOut)
            {
                if (!Named(term, events))
                    nothing.Add(term);
            }

            return nothing;
        }

        /// <summary>The query that reads back as this filter: its levels, its frames, the terms kept, then those left out.</summary>
        public override string ToString()
        {
            var query = new StringBuilder();
            foreach (string level in Levels.Words())
                Add(query, level);
            if (!Frames.IsAll)
                Add(query, Frames.ToString());
            foreach (LogFilterTerm term in _keep)
                Add(query, term.ToString());
            foreach (LogFilterTerm term in _leaveOut)
                Add(query, "-" + term);

            return query.ToString();
        }

        /// <summary>Equal when the levels, the frames, and the terms kept and left out, in order, are.</summary>
        public bool Equals(LogFilter? other) =>
            other is not null && Levels == other.Levels && Frames == other.Frames
            && SameTerms(_keep, other._keep) && SameTerms(_leaveOut, other._leaveOut);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Levels);
            hash.Add(Frames);
            foreach (LogFilterTerm term in _keep)
                hash.Add(term);
            hash.Add(-1);
            foreach (LogFilterTerm term in _leaveOut)
                hash.Add(term);

            return hash.ToHashCode();
        }

        /// <summary>Whether <paramref name="name"/> is a word a term of its own starts with, as <c>level:</c> does, so a field of that name is written after an <c>@</c>.</summary>
        internal static bool IsTermWord(string name) =>
            name.Equals("level", StringComparison.OrdinalIgnoreCase) || name.Equals("frame", StringComparison.OrdinalIgnoreCase)
            || name.Equals("scope", StringComparison.OrdinalIgnoreCase) || name.Equals("thread", StringComparison.OrdinalIgnoreCase);

        private static void Add(StringBuilder query, string word)
        {
            if (query.Length > 0)
                query.Append(' ');
            query.Append(word);
        }

        private static LogFilterTerm[] Copy(IReadOnlyList<LogFilterTerm> terms, string name)
        {
            if (terms is null)
                throw new ArgumentNullException(name);
            if (terms.Count == 0)
                return s_none;

            var copy = new LogFilterTerm[terms.Count];
            for (int i = 0; i < copy.Length; i++)
                copy[i] = terms[i] ?? throw new ArgumentNullException(name, $"The term at {i} is null.");

            return copy;
        }

        private static ReadOnlyCollection<LogFilterTerm> ReadOnly(LogFilterTerm[] terms) =>
            terms.Length == 0 ? s_noneList : Array.AsReadOnly(terms);

        private static TermMatch Met(LogFilterTerm term, int place, LogRecord record, LogScopes scopes, ReadOnlySpan<long> gaveUpFrom) =>
            place < gaveUpFrom.Length && record.Sequence >= gaveUpFrom[place] ? TermMatch.No : term.Matches(record, scopes);

        // A term is asked only of a record before the sequence it gave up from, so this is never later.
        private static void GiveUp(Span<long> gaveUpFrom, int place, long sequence)
        {
            if (place < gaveUpFrom.Length)
                gaveUpFrom[place] = sequence;
        }

        private static bool SameTerms(LogFilterTerm[] a, LogFilterTerm[] b)
        {
            if (a.Length != b.Length)
                return false;

            for (int i = 0; i < a.Length; i++)
            {
                if (!a[i].Equals(b[i]))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Whether one of <paramref name="events"/> is the event, or has the field, that
        /// <paramref name="term"/> names: a scope, a thread, text and a regular expression over the
        /// message name neither, and so name nothing that is not there.
        /// </summary>
        private static bool Named(LogFilterTerm term, IReadOnlyList<LogEventSpec> events) => term switch
        {
            LogFilterTerm.Event e => AnyOf(events, e.Pattern),
            LogFilterTerm.Field f => AnyHas(events, f.Name),
            LogFilterTerm.FieldRegex f => AnyHas(events, f.Name),
            LogFilterTerm.Scope or LogFilterTerm.Thread or LogFilterTerm.Text or LogFilterTerm.TextRegex => true,
            _ => throw new InvalidOperationException($"{term.GetType().Name} is a term {nameof(MatchingNothing)} does not know."),
        };

        private static bool AnyOf(IReadOnlyList<LogEventSpec> events, string pattern)
        {
            foreach (LogEventSpec spec in events)
            {
                if (LogPattern.Specificity(pattern, spec) >= 0)
                    return true;
            }

            return false;
        }

        private static bool AnyHas(IReadOnlyList<LogEventSpec> events, string field)
        {
            foreach (LogEventSpec spec in events)
            {
                foreach (LogField declared in spec.FieldArray)
                {
                    if (string.Equals(declared.Name, field, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        private static Result<Read> Term(Word word)
        {
            if (word.Name is null)
            {
                return word.Delimiter switch
                {
                    Delimiter.Quotes => word.Value.Length > 0
                        ? Kept(new LogFilterTerm.Text(word.Value))
                        : Refused(LogFilterError.NoText, $"{word.Text} has nothing between its quotes, and every message holds that: put the text to find in them"),
                    Delimiter.Slashes => Compiled(word).Map(regex => (Read)new Read.Term(new LogFilterTerm.TextRegex(regex))),
                    _ => LogPattern.IsValid(word.Value) ? Kept(new LogFilterTerm.Event(word.Value)) : NotATerm(word.Text),
                };
            }

            string name = word.Name;
            if (name.Length > 0 && name[0] == '@')
            {
                string field = name.Substring(1);
                return word.Operator == ":" && LogNames.IsEventName(field) ? FieldTerm(word, field) : NotATerm(word.Text);
            }

            if (name.Equals("level", StringComparison.OrdinalIgnoreCase))
            {
                if (word.Delimiter != Delimiter.None || !LogRules.TryLevel(word.Value, out LogLevel level) || level == LogLevel.None)
                {
                    return Refused(LogFilterError.NotALevel,
                        $"{word.Text} does not name a level, as level>=warning does: trace, debug, information, warning, error or critical");
                }

                return F.Success<Read>(new Read.Levels(word.Operator switch
                {
                    ">=" => LogLevelRange.Between(level, LogLevel.Critical),
                    ">" => LogLevelRange.Between(level + 1, LogLevel.Critical),
                    "<=" => LogLevelRange.Between(LogLevel.Trace, level),
                    "<" => level == LogLevel.Trace ? LogLevelRange.Empty : LogLevelRange.Between(LogLevel.Trace, level - 1),
                    _ => LogLevelRange.Only(level),
                }));
            }

            if (word.Operator != ":")
                return NotATerm(word.Text);

            bool bare = word.Delimiter == Delimiter.None;
            if (name.Equals("frame", StringComparison.OrdinalIgnoreCase))
            {
                return LogFrameRange.TryParse(bare ? word.Value : "", out LogFrameRange frames)
                    ? F.Success<Read>(new Read.Frames(frames))
                    : Refused(LogFilterError.NotAFrame, $"{word.Text} does not name frames: frame:1200..1300, frame:1200.., frame:..1300 or frame:1240");
            }

            if (name.Equals("scope", StringComparison.OrdinalIgnoreCase))
            {
                return bare && Whole(word.Value, out long scope) && scope > 0
                    ? Kept(new LogFilterTerm.Scope(scope))
                    : Refused(LogFilterError.NotAScope, $"{word.Text} does not name a scope, which is the sequence of the record that opened it: scope:4182");
            }

            if (name.Equals("thread", StringComparison.OrdinalIgnoreCase))
            {
                return bare && Whole(word.Value, out long thread) && thread <= int.MaxValue
                    ? Kept(new LogFilterTerm.Thread((int)thread))
                    : Refused(LogFilterError.NotAThread, $"{word.Text} does not name a thread by its managed id: thread:7");
            }

            return LogNames.IsEventName(name) ? FieldTerm(word, name) : NotATerm(word.Text);
        }

        // A field and the pattern after its colon, which is never nothing: an empty value is "" in
        // quotes. A level, frames, a scope or a thread with nothing after it says what it wanted instead.
        private static Result<Read> FieldTerm(Word word, string name)
        {
            if (word.Delimiter == Delimiter.Slashes)
                return Compiled(word).Map(regex => (Read)new Read.Term(new LogFilterTerm.FieldRegex(name, regex)));

            return word.Value.Length > 0 || word.Delimiter == Delimiter.Quotes
                ? Kept(new LogFilterTerm.Field(name, word.Value))
                : Refused(LogFilterError.NoValue, $"{word.Text} has nothing after it to match: a field that is empty is \"\" in quotes");
        }

        /// <summary>The word's value compiled as a regular expression, or why it is not one.</summary>
        private static Result<Regex> Compiled(Word word)
        {
            if (word.Value.Length == 0)
            {
                return F.Fail<Regex>(Refused(LogFilterError.NotARegex,
                    $"{word.Text} has nothing between its slashes, which matches everything: put the expression to match in them"));
            }

            Result<Regex> compiled = LogFilterTerm.Compile(word.Value);
            return compiled.IsSuccess(out Regex regex)
                ? F.Success(regex)
                : F.Fail<Regex>(Refused(LogFilterError.NotARegex, $"{word.Text} is not a regular expression: {compiled.ErrorUnsafe().Message}"));
        }

        private static Result<Read> Kept(LogFilterTerm term) => F.Success<Read>(new Read.Term(term));

        private static Result<Read> NotATerm(string word) =>
            Refused(LogFilterError.NotATerm,
                $"{word} is not an event, a level, frames, a scope, a thread or a field: text to find in messages goes in quotes, \"{word.Replace("\"", "")}\"");

        private static Error Refused(LogFilterError why, string message) => F.Error(message, (int)why);

        private static bool Whole(string text, out long value) =>
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

        /// <summary>What a word of a query reads as.</summary>
        private abstract record Read
        {
            private Read()
            {
            }

            public sealed record Levels(LogLevelRange Range) : Read;

            public sealed record Frames(LogFrameRange Range) : Read;

            public sealed record Term(LogFilterTerm Value) : Read;
        }

        /// <summary>How a word's value was written: as it is, in quotes, or between slashes.</summary>
        private enum Delimiter
        {
            None,
            Quotes,
            Slashes,
        }

        /// <summary>
        /// A word of a query: as it was written, whether it is left out, and its name and operator
        /// when it has them, and its value, read out of its quotes or slashes.
        /// </summary>
        private sealed record Word(string Text, bool LeftOut, string? Name, string Operator, string Value, Delimiter Delimiter);

        /// <summary>Splits a query into words, a quoted value or a regular expression with its spaces as one.</summary>
        private sealed class QueryReader
        {
            private readonly string _query;
            private int _at;

            public QueryReader(string query) => _query = query;

            private bool AtEnd => _at == _query.Length || char.IsWhiteSpace(_query[_at]);

            /// <summary>Moves to the next word, and says whether there is one.</summary>
            public bool More()
            {
                while (_at < _query.Length && char.IsWhiteSpace(_query[_at]))
                    _at++;
                return _at < _query.Length;
            }

            /// <summary>Reads the word <see cref="More"/> moved to.</summary>
            public Result<Word> Next()
            {
                int start = _at;
                bool leftOut = _query[_at] == '-';
                if (leftOut)
                    _at++;

                if (_at < _query.Length && _query[_at] is '"' or '/')
                    return Delimited(start, leftOut, null, "");

                int nameStart = _at;
                while (_at < _query.Length && !char.IsWhiteSpace(_query[_at]) && _query[_at] is not (':' or '<' or '>' or '=' or '"'))
                    _at++;
                string name = _query.Substring(nameStart, _at - nameStart);
                if (AtEnd)
                {
                    return name.Length == 0
                        ? NotAWord(_query.Substring(start, _at - start))
                        : F.Success(new Word(_query.Substring(start, _at - start), leftOut, null, "", name, Delimiter.None));
                }

                if (_query[_at] == '"' || name.Length == 0)
                    return NotAWord(SkipWord(start));

                string op = _query[_at] is '>' or '<' && _at + 1 < _query.Length && _query[_at + 1] == '='
                    ? _query.Substring(_at, 2)
                    : _query[_at].ToString();
                _at += op.Length;

                if (_at < _query.Length && _query[_at] is '"' or '/')
                    return Delimited(start, leftOut, name, op);

                int valueStart = _at;
                while (!AtEnd)
                {
                    if (_query[_at] == '"')
                        return NotAWord(SkipWord(start));
                    _at++;
                }

                return F.Success(new Word(_query.Substring(start, _at - start), leftOut, name, op, _query.Substring(valueStart, _at - valueStart), Delimiter.None));
            }

            /// <summary>Reads a value in quotes or slashes from the one at the reader, to the end of its word.</summary>
            private Result<Word> Delimited(int start, bool leftOut, string? name, string op)
            {
                bool quoted = _query[_at] == '"';
                string rest = _query.Substring(start);
                if (!Run(quoted ? '"' : '/', out string value))
                {
                    return quoted
                        ? F.Fail<Word>(Refused(LogFilterError.QuoteNotClosed, $"The quote in {rest} is not closed"))
                        : F.Fail<Word>(Refused(LogFilterError.RegexNotClosed,
                            $"The regular expression in {rest} has no slash to end it: /pattern/, with \\/ for a slash in it"));
                }

                if (!AtEnd)
                {
                    string word = SkipWord(start);
                    return quoted
                        ? NotAWord(word)
                        : F.Fail<Word>(Refused(LogFilterError.NotARegex,
                            $"The regular expression in {word} ends before the word does: a slash in it is \\/, and a value that starts with a slash but is not one goes in quotes"));
                }

                return F.Success(new Word(_query.Substring(start, _at - start), leftOut, name, op, value, quoted ? Delimiter.Quotes : Delimiter.Slashes));
            }

            /// <summary>
            /// Reads from the delimiter at the reader to the one that closes it, or says there is none.
            /// In quotes, <c>\"</c> is a quote and <c>\\</c> a backslash; between slashes, <c>\/</c> is a
            /// slash, and every other backslash is kept with the char after it, as the expression's own.
            /// </summary>
            private bool Run(char delimiter, out string text)
            {
                var value = new StringBuilder();
                for (_at++; _at < _query.Length; _at++)
                {
                    char c = _query[_at];
                    if (c == delimiter)
                    {
                        _at++;
                        text = value.ToString();
                        return true;
                    }

                    if (c == '\\' && _at + 1 < _query.Length)
                    {
                        char after = _query[_at + 1];
                        if (after == delimiter || (delimiter == '"' && after == '\\'))
                        {
                            c = after;
                            _at++;
                        }
                        else if (delimiter == '/')
                        {
                            value.Append(c);
                            c = after;
                            _at++;
                        }
                    }

                    value.Append(c);
                }

                text = "";
                return false;
            }

            /// <summary>Moves past the rest of the word, quotes and all, and says the whole of it.</summary>
            private string SkipWord(int start)
            {
                bool quoted = false;
                for (; _at < _query.Length && (quoted || !char.IsWhiteSpace(_query[_at])); _at++)
                {
                    if (_query[_at] == '"')
                        quoted = !quoted;
                }

                return _query.Substring(start, _at - start);
            }

            private static Result<Word> NotAWord(string word) => F.Fail<Word>(NotATerm(word).ErrorUnsafe());
        }
    }

    /// <summary>
    /// A filter as one pass over the records applies it: a regular expression that takes too long
    /// over a record matches nothing from that record on, rather than take as long over every record
    /// left, and the pass says which did. A view's rows are one pass; a new one tries afresh.
    /// </summary>
    /// <remarks>
    /// A term gives up from a sequence, not from a moment: a record before it that the pass reads
    /// again, as a view does when a record is placed late, is matched as it was the first time.
    /// </remarks>
    internal sealed class FilterPass
    {
        // By a term's place, the sequence from which it matches nothing: long.MaxValue while it has not given up.
        private readonly long[] _gaveUpFrom;

        public FilterPass(LogFilter filter)
            : this(filter, NoneGaveUp(filter.TermCount))
        {
        }

        private FilterPass(LogFilter filter, long[] gaveUpFrom)
        {
            Filter = filter;
            _gaveUpFrom = gaveUpFrom;
            GaveUp = Listed(filter, gaveUpFrom);
        }

        /// <summary>The filter it applies.</summary>
        public LogFilter Filter { get; }

        /// <summary>The terms whose regular expression took too long over a record, in the filter's order, which match nothing from it on.</summary>
        public IReadOnlyList<LogFilterTerm> GaveUp { get; private set; }

        /// <summary>A pass that applies the filter as this one has so far, and gives up apart from it after: for a copy of the rows.</summary>
        public FilterPass Copy() => new FilterPass(Filter, (long[])_gaveUpFrom.Clone());

        /// <summary>Whether <paramref name="record"/> is kept.</summary>
        public bool Matches(LogRecord record, LogScopes scopes) =>
            Filter.Levels.Contains(record.Level) && MatchesTerms(record, scopes);

        /// <summary>Whether <paramref name="record"/> is kept but for its level.</summary>
        public bool MatchesTerms(LogRecord record, LogScopes scopes)
        {
            bool kept = Filter.MatchesTerms(record, scopes, _gaveUpFrom, out bool tookTooLong);
            if (tookTooLong)
                GaveUp = Listed(Filter, _gaveUpFrom);

            return kept;
        }

        private static long[] NoneGaveUp(int terms)
        {
            var gaveUpFrom = new long[terms];
            gaveUpFrom.AsSpan().Fill(long.MaxValue);
            return gaveUpFrom;
        }

        // A new list, made only when a term took too long, which took far longer than this does.
        private static IReadOnlyList<LogFilterTerm> Listed(LogFilter filter, long[] gaveUpFrom)
        {
            int count = 0;
            for (int place = 0; place < gaveUpFrom.Length; place++)
            {
                if (gaveUpFrom[place] != long.MaxValue)
                    count++;
            }

            if (count == 0)
                return Array.Empty<LogFilterTerm>();

            var terms = new LogFilterTerm[count];
            int listed = 0;
            for (int place = 0; place < gaveUpFrom.Length; place++)
            {
                if (gaveUpFrom[place] != long.MaxValue)
                    terms[listed++] = filter.Term(place);
            }

            return Array.AsReadOnly(terms);
        }
    }

    /// <summary>Whether a record meets a term: it does, it does not, or a regular expression took too long to say.</summary>
    internal enum TermMatch : byte
    {
        No,
        Yes,
        TookTooLong,
    }

    /// <summary>A term of a <see cref="LogFilter"/>: what a record must meet when it is kept, and must not when it is left out.</summary>
    /// <remarks>A term is a value: two equal terms match the same records.</remarks>
    public abstract record LogFilterTerm
    {
        /// <summary>
        /// How long a regular expression may take over one record before it gives up, which a pattern
        /// that backtracks without end would otherwise never do.
        /// </summary>
        private static readonly TimeSpan s_matchTimeout = TimeSpan.FromMilliseconds(20);

        private LogFilterTerm()
        {
        }

        /// <summary>Whether <paramref name="record"/> meets it, with <paramref name="scopes"/> saying what is under what.</summary>
        internal abstract TermMatch Matches(LogRecord record, LogScopes scopes);

        /// <summary>
        /// <paramref name="pattern"/> as a regular expression that ignores case and culture, or what the
        /// expression's parser says is wrong with it. It is held as a query reads it back, with
        /// <c>\/</c> as <c>/</c>, which is the same expression, so a term and its query are equal.
        /// </summary>
        internal static Result<Regex> Compile(string pattern)
        {
            try
            {
                return new Regex(Unslashed(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, s_matchTimeout);
            }
            catch (ArgumentException exception)
            {
                return F.Error(exception.Message);
            }
        }

        private static TermMatch Met(bool met) => met ? TermMatch.Yes : TermMatch.No;

        /// <summary>Whether <paramref name="regex"/> matches <paramref name="text"/>, or took too long to say.</summary>
        private static TermMatch Run(Regex regex, string text)
        {
            try
            {
                return Met(regex.IsMatch(text));
            }
            catch (RegexMatchTimeoutException)
            {
                return TermMatch.TookTooLong;
            }
        }

        /// <summary><paramref name="pattern"/> with each <c>\/</c> in it a <c>/</c>, and every other escape as it is.</summary>
        private static string Unslashed(string pattern)
        {
            if (pattern.IndexOf("\\/", StringComparison.Ordinal) < 0)
                return pattern;

            var text = new StringBuilder(pattern.Length);
            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                if (c == '\\' && i + 1 < pattern.Length)
                {
                    char after = pattern[++i];
                    if (after != '/')
                        text.Append(c);
                    text.Append(after);
                    continue;
                }

                text.Append(c);
            }

            return text.ToString();
        }

        /// <summary>
        /// A value as a query writes it: in quotes, escaped, when it has to be, which is when it is
        /// empty, has a space or a quote, or starts with a slash, as a regular expression does.
        /// </summary>
        private static string Quote(string value, bool always = false)
        {
            bool plain = !always && value.Length > 0 && value[0] != '/';
            foreach (char c in value)
                plain = plain && !char.IsWhiteSpace(c) && c != '"';
            if (plain)
                return value;

            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        /// <summary>A regular expression as a query writes it: between slashes, each slash of it as <c>\/</c>.</summary>
        private static string Slashed(string pattern)
        {
            var text = new StringBuilder(pattern.Length + 2).Append('/');
            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                if (c == '\\' && i + 1 < pattern.Length)
                {
                    text.Append(c).Append(pattern[++i]);
                    continue;
                }

                text.Append(c == '/' ? "\\/" : c.ToString());
            }

            return text.Append('/').ToString();
        }

        /// <summary>The message of <paramref name="record"/>, in a buffer of the shared pool the caller returns.</summary>
        private static char[] Message(LogRecord record, out int written)
        {
            char[] buffer = ArrayPool<char>.Shared.Rent(512);
            while (!record.TryFormat(buffer, out written))
            {
                int length = buffer.Length * 2;
                ArrayPool<char>.Shared.Return(buffer);
                buffer = ArrayPool<char>.Shared.Rent(length);
            }

            return buffer;
        }

        /// <summary><paramref name="value"/> formatted, in a buffer of the shared pool the caller returns.</summary>
        private static char[] Formatted(LogValue value, out int written)
        {
            char[] buffer = ArrayPool<char>.Shared.Rent(512);
            while (!value.TryFormat(buffer, out written))
            {
                int length = buffer.Length * 2;
                ArrayPool<char>.Shared.Return(buffer);
                buffer = ArrayPool<char>.Shared.Rent(length);
            }

            return buffer;
        }

        /// <summary>Records of the events a pattern names, as a rule names them.</summary>
        public sealed record Event : LogFilterTerm
        {
            /// <summary>The events <paramref name="pattern"/> names.</summary>
            /// <param name="pattern"><c>*</c>, a category, a category and everything under it (<c>game.*</c>), or a key.</param>
            /// <exception cref="ArgumentException">The pattern is not one of the four kinds.</exception>
            public Event(string pattern)
            {
                if (pattern is null || !LogPattern.IsValid(pattern))
                    throw new ArgumentException($"\"{pattern}\" is not *, a category, a category and everything under it, or a key.", nameof(pattern));
                Pattern = pattern;
            }

            /// <summary>The pattern.</summary>
            public string Pattern { get; }

            internal override TermMatch Matches(LogRecord record, LogScopes scopes) => Met(LogPattern.Specificity(Pattern, record.Event) >= 0);

            /// <inheritdoc />
            public override string ToString() => Pattern;
        }

        /// <summary>A scope's own records, and every record under it, on every thread it reached.</summary>
        public sealed record Scope : LogFilterTerm
        {
            /// <summary>The records of the scope <paramref name="id"/> and under it.</summary>
            /// <param name="id">The scope: the sequence of the record that opened it.</param>
            /// <exception cref="ArgumentOutOfRangeException">The id is not a sequence, which counts from 1.</exception>
            public Scope(long id)
            {
                if (id < 1)
                    throw new ArgumentOutOfRangeException(nameof(id), $"A scope is the sequence of the record that opened it, which counts from 1, and {id} was given.");
                Id = id;
            }

            /// <summary>The scope: the sequence of the record that opened it.</summary>
            public long Id { get; }

            internal override TermMatch Matches(LogRecord record, LogScopes scopes) =>
                Met(record.Begin == Id || scopes.IsUnder(record.Scope, Id));

            /// <inheritdoc />
            public override string ToString() => $"scope:{Id.ToString(CultureInfo.InvariantCulture)}";
        }

        /// <summary>What one managed thread wrote.</summary>
        public sealed record Thread : LogFilterTerm
        {
            /// <summary>What the thread <paramref name="id"/> wrote.</summary>
            /// <param name="id">The thread's managed id, or 0 for the records of a file that does not say.</param>
            /// <exception cref="ArgumentOutOfRangeException">The id is below 0, which no thread's is.</exception>
            public Thread(int id)
            {
                if (id < 0)
                    throw new ArgumentOutOfRangeException(nameof(id), $"A thread's managed id is 0 or more, and {id} was given.");
                Id = id;
            }

            /// <summary>The thread's managed id.</summary>
            public int Id { get; }

            internal override TermMatch Matches(LogRecord record, LogScopes scopes) => Met(record.Thread == Id);

            /// <inheritdoc />
            public override string ToString() => $"thread:{Id.ToString(CultureInfo.InvariantCulture)}";
        }

        /// <summary>Records with a field of a name, ignoring case, whose formatted value a pattern matches.</summary>
        public sealed record Field : LogFilterTerm
        {
            /// <summary>A field named <paramref name="name"/> whose value <paramref name="pattern"/> matches.</summary>
            /// <exception cref="ArgumentException">The name is not a field's.</exception>
            public Field(string name, string pattern)
            {
                // A field's name is spelt as an event's is, as a C# identifier.
                if (name is null || !LogNames.IsEventName(name))
                    throw new ArgumentException($"\"{name}\" is not a field's name.", nameof(name));
                Name = name;
                Pattern = pattern ?? throw new ArgumentNullException(nameof(pattern));
            }

            /// <summary>The field's name.</summary>
            public string Name { get; }

            /// <summary>The value's pattern, ignoring case: <c>*</c> is any text, <c>?</c> any char, and every other char itself.</summary>
            public string Pattern { get; }

            internal override TermMatch Matches(LogRecord record, LogScopes scopes)
            {
                if (!record.TryField(Name, out LogValue value))
                    return TermMatch.No;

                char[] text = Formatted(value, out int written);
                try
                {
                    return Met(Wildcard.Matches(Pattern.AsSpan(), text.AsSpan(0, written)));
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(text);
                }
            }

            /// <summary>As a query writes it: <c>File:*.png</c>, and <c>@level:high</c> for a field named as a term is.</summary>
            public override string ToString() => $"{(LogFilter.IsTermWord(Name) ? "@" : "")}{Name}:{Quote(Pattern)}";
        }

        /// <summary>Records whose message holds some text, ignoring case.</summary>
        public sealed record Text : LogFilterTerm
        {
            /// <summary>Records whose message holds <paramref name="value"/>.</summary>
            /// <exception cref="ArgumentException">The text is empty, which every message holds.</exception>
            public Text(string value)
            {
                if (string.IsNullOrEmpty(value))
                    throw new ArgumentException("Every message holds empty text, so a text term has some.", nameof(value));
                Value = value;
            }

            /// <summary>The text.</summary>
            public string Value { get; }

            internal override TermMatch Matches(LogRecord record, LogScopes scopes)
            {
                char[] message = Message(record, out int written);
                try
                {
                    return Met(new ReadOnlySpan<char>(message, 0, written).IndexOf(Value.AsSpan(), StringComparison.OrdinalIgnoreCase) >= 0);
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(message);
                }
            }

            /// <inheritdoc />
            public override string ToString() => Quote(Value, always: true);
        }

        /// <summary>
        /// Records whose message a regular expression matches, ignoring case. A match that takes too
        /// long gives up, and the record is not matched; a view's rows go on without the term from
        /// then on, as <see cref="LogView.GaveUp"/> says.
        /// </summary>
        public sealed record TextRegex : LogFilterTerm
        {
            private readonly Regex _regex;

            /// <summary>Records whose message <paramref name="pattern"/> matches.</summary>
            /// <exception cref="ArgumentException">The pattern is empty, which every message matches, or is not a regular expression.</exception>
            public TextRegex(string pattern)
                : this(Expression(pattern))
            {
            }

            internal TextRegex(Regex regex) => _regex = regex;

            /// <summary>The regular expression, as a query reads it back: with <c>/</c> for each <c>\/</c> it was written with.</summary>
            public string Pattern => _regex.ToString();

            internal override TermMatch Matches(LogRecord record, LogScopes scopes)
            {
                char[] message = Message(record, out int written);
                try
                {
                    return Run(_regex, new string(message, 0, written));
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(message);
                }
            }

            /// <summary>Equal when the patterns are, whatever objects they compiled to.</summary>
            public bool Equals(TextRegex? other) => other is not null && string.Equals(Pattern, other.Pattern, StringComparison.Ordinal);

            /// <inheritdoc />
            public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Pattern);

            /// <summary>As a query writes it: <c>/fail(ed|ure)/</c>.</summary>
            public override string ToString() => Slashed(Pattern);
        }

        /// <summary>
        /// Records with a field of a name, ignoring case, whose formatted value a regular expression
        /// matches, ignoring case. A match that takes too long gives up, as <see cref="TextRegex"/>'s does.
        /// </summary>
        public sealed record FieldRegex : LogFilterTerm
        {
            private readonly Regex _regex;

            /// <summary>A field named <paramref name="name"/> whose value <paramref name="pattern"/> matches.</summary>
            /// <exception cref="ArgumentException">The name is not a field's, or the pattern is empty or not a regular expression.</exception>
            public FieldRegex(string name, string pattern)
                : this(name, Expression(pattern))
            {
            }

            internal FieldRegex(string name, Regex regex)
            {
                if (name is null || !LogNames.IsEventName(name))
                    throw new ArgumentException($"\"{name}\" is not a field's name.", nameof(name));
                Name = name;
                _regex = regex;
            }

            /// <summary>The field's name.</summary>
            public string Name { get; }

            /// <summary>The regular expression, as a query reads it back: with <c>/</c> for each <c>\/</c> it was written with.</summary>
            public string Pattern => _regex.ToString();

            internal override TermMatch Matches(LogRecord record, LogScopes scopes)
            {
                if (!record.TryField(Name, out LogValue value))
                    return TermMatch.No;

                char[] text = Formatted(value, out int written);
                try
                {
                    return Run(_regex, new string(text, 0, written));
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(text);
                }
            }

            /// <summary>Equal when the names and the patterns are, whatever objects the patterns compiled to.</summary>
            public bool Equals(FieldRegex? other) =>
                other is not null && string.Equals(Name, other.Name, StringComparison.Ordinal) && string.Equals(Pattern, other.Pattern, StringComparison.Ordinal);

            /// <inheritdoc />
            public override int GetHashCode() => HashCode.Combine(StringComparer.Ordinal.GetHashCode(Name), StringComparer.Ordinal.GetHashCode(Pattern));

            /// <summary>As a query writes it: <c>File:/\.(png|jpg)$/</c>, and <c>@level:/high/</c> for a field named as a term is.</summary>
            public override string ToString() => $"{(LogFilter.IsTermWord(Name) ? "@" : "")}{Name}:{Slashed(Pattern)}";
        }

        private static Regex Expression(string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
                throw new ArgumentException("An empty regular expression matches everything, so a regular expression term has one.", nameof(pattern));

            return Compile(pattern).Match(
                why => throw new ArgumentException($"/{pattern}/ is not a regular expression: {why.Message}", nameof(pattern)),
                regex => regex);
        }
    }

    /// <summary>The levels a filter keeps, from <see cref="Least"/> to <see cref="Most"/>, both kept, or none.</summary>
    /// <remarks>
    /// <see cref="Between"/> makes one, and holds every range that keeps no level as <see cref="Empty"/>,
    /// so two ranges that keep the same levels are equal and write the same query. Its
    /// <c>default</c> keeps every level, as <see cref="All"/> does.
    /// </remarks>
    public readonly record struct LogLevelRange
    {
        // Held as how far each end is in from the levels' own, so that the default is every level.
        private readonly byte _aboveTrace;
        private readonly byte _belowCritical;

        private LogLevelRange(LogLevel least, LogLevel most)
        {
            _aboveTrace = (byte)(least - LogLevel.Trace);
            _belowCritical = (byte)(LogLevel.Critical - most);
        }

        /// <summary>Every level.</summary>
        public static LogLevelRange All => default;

        /// <summary>No level.</summary>
        public static LogLevelRange Empty => new LogLevelRange(LogLevel.Critical, LogLevel.Trace);

        /// <summary>The least level kept: above <see cref="Most"/> when none is.</summary>
        public LogLevel Least => LogLevel.Trace + _aboveTrace;

        /// <summary>The most level kept: below <see cref="Least"/> when none is.</summary>
        public LogLevel Most => LogLevel.Critical - _belowCritical;

        /// <summary>Whether it keeps no level.</summary>
        public bool IsEmpty => Least > Most;

        /// <summary>Whether it keeps every level.</summary>
        public bool IsAll => _aboveTrace == 0 && _belowCritical == 0;

        /// <summary>One level; none for <see cref="LogLevel.None"/>, which no record has.</summary>
        public static LogLevelRange Only(LogLevel level) => Between(level, level);

        /// <summary>
        /// The levels from <paramref name="least"/> to <paramref name="most"/>, both kept: none when the
        /// least is above the most, and none above <see cref="LogLevel.Critical"/>, since no record has
        /// <see cref="LogLevel.None"/>.
        /// </summary>
        public static LogLevelRange Between(LogLevel least, LogLevel most)
        {
            LogLevel to = most > LogLevel.Critical ? LogLevel.Critical : most;
            return least > to ? Empty : new LogLevelRange(least, to);
        }

        /// <summary>Whether it keeps <paramref name="level"/>.</summary>
        public bool Contains(LogLevel level) => level >= Least && level <= Most;

        /// <summary>The levels both keep.</summary>
        public LogLevelRange Intersect(LogLevelRange other) =>
            Between(Least > other.Least ? Least : other.Least, Most < other.Most ? Most : other.Most);

        /// <summary>As a query writes it: <c>level>=warning</c>, nothing for every level.</summary>
        public override string ToString() => string.Join(" ", Words());

        /// <summary>The words a query writes it as, the fewest that say it.</summary>
        internal IEnumerable<string> Words()
        {
            if (IsAll)
                yield break;

            if (IsEmpty)
            {
                yield return "level>critical";
            }
            else if (Least == Most)
            {
                yield return $"level={LogRules.Name(Least)}";
            }
            else
            {
                if (Least > LogLevel.Trace)
                    yield return $"level>={LogRules.Name(Least)}";
                if (Most < LogLevel.Critical)
                    yield return $"level<={LogRules.Name(Most)}";
            }
        }
    }

    /// <summary>The frames a filter keeps, from <see cref="First"/> to <see cref="Last"/>, both kept, or none.</summary>
    /// <remarks>
    /// <see cref="Between"/> makes one, and holds every range that keeps no frame as <see cref="Empty"/>,
    /// so two ranges that keep the same frames are equal and write the same query. Its
    /// <c>default</c> keeps every frame, as <see cref="All"/> does.
    /// </remarks>
    public readonly record struct LogFrameRange
    {
        // Held as the first frame and how far the last is in from the last there is, so that the
        // default is every frame.
        private readonly long _first;
        private readonly long _beforeEnd;

        private LogFrameRange(long first, long last)
        {
            _first = first;
            _beforeEnd = long.MaxValue - last;
        }

        /// <summary>Every frame.</summary>
        public static LogFrameRange All => default;

        /// <summary>No frame.</summary>
        public static LogFrameRange Empty => new LogFrameRange(1, 0);

        /// <summary>The first frame kept: after <see cref="Last"/> when none is.</summary>
        public long First => _first;

        /// <summary>The last frame kept: before <see cref="First"/> when none is.</summary>
        public long Last => long.MaxValue - _beforeEnd;

        /// <summary>Whether it keeps no frame.</summary>
        public bool IsEmpty => First > Last;

        /// <summary>Whether it keeps every frame.</summary>
        public bool IsAll => _first == 0 && _beforeEnd == 0;

        /// <summary>One frame; none below 0, which no record's is.</summary>
        public static LogFrameRange Only(long frame) => Between(frame, frame);

        /// <summary>
        /// The frames from <paramref name="first"/> to <paramref name="last"/>, both kept: from 0 when
        /// the first is below it, as frames count from 0, and none when the first is after the last.
        /// </summary>
        public static LogFrameRange Between(long first, long last)
        {
            long from = Math.Max(first, 0);
            return from > last ? Empty : new LogFrameRange(from, last);
        }

        /// <summary>Whether it keeps <paramref name="frame"/>.</summary>
        public bool Contains(long frame) => frame >= First && frame <= Last;

        /// <summary>The frames both keep.</summary>
        public LogFrameRange Intersect(LogFrameRange other) =>
            Between(Math.Max(First, other.First), Math.Min(Last, other.Last));

        /// <summary>As a query writes it: <c>frame:1200..1300</c>, <c>frame:1200..</c>, <c>frame:..1300</c> or <c>frame:1240</c>.</summary>
        public override string ToString()
        {
            if (IsEmpty)
                return "frame:1..0";

            CultureInfo invariant = CultureInfo.InvariantCulture;
            if (First == Last)
                return $"frame:{First.ToString(invariant)}";

            string first = First == 0 ? "" : First.ToString(invariant);
            string last = Last == long.MaxValue ? "" : Last.ToString(invariant);
            return $"frame:{first}..{last}";
        }

        /// <summary>Reads what follows <c>frame:</c>: <c>a..b</c>, <c>a..</c>, <c>..b</c> or <c>a</c>, of whole numbers.</summary>
        internal static bool TryParse(string text, out LogFrameRange range)
        {
            range = All;
            int dots = text.IndexOf("..", StringComparison.Ordinal);
            if (dots < 0)
            {
                if (!Whole(text, out long frame))
                    return false;
                range = Only(frame);
                return true;
            }

            string first = text.Substring(0, dots);
            string last = text.Substring(dots + 2);
            long from = 0;
            long to = long.MaxValue;
            if ((first.Length == 0 && last.Length == 0) || (first.Length > 0 && !Whole(first, out from)) || (last.Length > 0 && !Whole(last, out to)))
                return false;

            range = Between(from, to);
            return true;
        }

        private static bool Whole(string text, out long value) =>
            long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Why a query is not a filter: the <see cref="Error.Code"/> of the error <see cref="LogFilter.Parse"/> fails with, whose message names the word.</summary>
    public enum LogFilterError
    {
        /// <summary>A word that is none of the terms.</summary>
        NotATerm = 1,

        /// <summary>A level term whose level is not one.</summary>
        NotALevel,

        /// <summary>A frame term whose frames are not whole numbers as <c>a..b</c>, <c>a..</c>, <c>..b</c> or <c>a</c>.</summary>
        NotAFrame,

        /// <summary>A scope term whose scope is not a sequence.</summary>
        NotAScope,

        /// <summary>A thread term whose thread is not a managed thread's id.</summary>
        NotAThread,

        /// <summary>Quotes with nothing between them: <c>""</c>, the text every message holds.</summary>
        NoText,

        /// <summary>A field's name and nothing after it: <c>File:</c>.</summary>
        NoValue,

        /// <summary>A quote with no quote to close it.</summary>
        QuoteNotClosed,

        /// <summary>A level or frames left out, which a filter keeps rather than leaves out.</summary>
        CannotLeaveOut,

        /// <summary>A slash that starts a regular expression with no slash to end it.</summary>
        RegexNotClosed,

        /// <summary>Slashes with nothing between them, or with what is not a regular expression.</summary>
        NotARegex,
    }

    /// <summary>Matches text against a pattern of <c>*</c> for any text and <c>?</c> for any char, ignoring case, without allocating.</summary>
    internal static class Wildcard
    {
        public static bool Matches(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
        {
            int p = 0;
            int t = 0;
            int star = -1;
            int resume = 0;
            while (t < text.Length)
            {
                if (p < pattern.Length && (pattern[p] == '?' || Same(pattern[p], text[t])))
                {
                    p++;
                    t++;
                }
                else if (p < pattern.Length && pattern[p] == '*')
                {
                    star = p++;
                    resume = t;
                }
                else if (star >= 0)
                {
                    p = star + 1;
                    t = ++resume;
                }
                else
                {
                    return false;
                }
            }

            while (p < pattern.Length && pattern[p] == '*')
                p++;

            return p == pattern.Length;
        }

        private static bool Same(char a, char b) => a == b || char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
    }
}
