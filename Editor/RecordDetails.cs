using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Tutan.Functional;

namespace Tutan.Logging.Editor
{
    /// <summary>A field of a record as the details show it: its name, its value as text, its kind, and whether it is personal.</summary>
    internal sealed record FieldShown(string Name, string Value, LogValueKind Kind, bool Personal);

    /// <summary>How long a scope took, as the records read say: still open, closed after a time, or not known, its records gone.</summary>
    internal abstract record ScopeTime
    {
        private ScopeTime()
        {
        }

        /// <summary>The scope has not closed.</summary>
        public static ScopeTime Open { get; } = new StillOpen();

        /// <summary>The records that say are gone, or were never read.</summary>
        public static ScopeTime Unknown { get; } = new NotKnown();

        /// <summary>How long <paramref name="scope"/> took, as <paramref name="scopes"/> read it.</summary>
        public static ScopeTime Of(LogScopes scopes, long scope)
        {
            if (scopes is null)
                throw new ArgumentNullException(nameof(scopes));

            if (scopes.IsOpen(scope))
                return Open;

            return scopes.Elapsed(scope).Match(static () => Unknown, static elapsed => new Took(elapsed));
        }

        /// <summary>The scope has not closed.</summary>
        public sealed record StillOpen : ScopeTime;

        /// <summary>The scope closed after <paramref name="Elapsed"/>.</summary>
        public sealed record Took(TimeSpan Elapsed) : ScopeTime;

        /// <summary>Not known: the records that say are gone.</summary>
        public sealed record NotKnown : ScopeTime;
    }

    /// <summary>A scope a record is under: its id, its opening's message, none when its opening is gone, and how long it took.</summary>
    internal sealed record ScopeShown(long Scope, Optional<string> Text, ScopeTime Time)
    {
        /// <summary>The scope as the details list it: its opening's message, or its id when that is gone, then how long it took, when known.</summary>
        public string Line
        {
            get
            {
                string text = Text.HasValue(out string message) ? message : $"scope:{Scope.ToString(CultureInfo.InvariantCulture)}, whose opening is gone";
                string took = RowCells.DurationOf(Time);
                return took.Length > 0 ? $"{text}  ·  {took}" : text;
            }
        }

        /// <summary>
        /// The scopes from <paramref name="scope"/> out, the innermost first, as <paramref name="view"/>
        /// read them: each opening's message read from the session's records as they are now.
        /// </summary>
        /// <param name="view">The view whose scopes and records they are.</param>
        /// <param name="scope">The scope a record is under: 0 for none.</param>
        /// <remarks>
        /// It follows the chain as deep as <see cref="ChainMark"/> does, so its mark sees every change to
        /// what it shows, and lists a scope once though a broken file made the chain a loop.
        /// </remarks>
        public static ReadOnlyCollection<ScopeShown> Chain(LogView view, long scope)
        {
            if (view is null)
                throw new ArgumentNullException(nameof(view));

            var chain = new List<ScopeShown>();
            var seen = new HashSet<long>();
            var opening = new LogBatch(1);
            Span<long> sequence = stackalloc long[1];
            int depth = 0;
            for (long at = scope; at != 0 && depth < ChainMark.MostDepth && seen.Add(at); at = view.Scopes.Parent(at), depth++)
            {
                sequence[0] = at;
                Optional<string> text = view.Source.Read(sequence, opening) == 1 && opening[0].Sequence == at
                    ? F.Some(opening[0].ToString())
                    : F.None;
                chain.Add(new ScopeShown(at, text, ScopeTime.Of(view.Scopes, at)));
            }

            return Array.AsReadOnly(chain.ToArray());
        }
    }

    /// <summary>
    /// What the chain of scopes a record is under shows, as far as it can change: how many scopes it
    /// has, how many are open, how many closed, and how many openings are at or after the first record
    /// the session holds, as an opening's message shows while the session holds it. A scope only opens,
    /// closes, or is learnt to be under another, and an opening only goes, so each count only goes one
    /// way, and any change to what the chain shows changes one of them. A store keeps the opening of a
    /// scope still open past what its ring holds, which the mark counts as gone, so over a store it can
    /// change when the chain does not, once for each such opening, and never stays when the chain changes;
    /// the window shows an archive, which keeps no such copy.
    /// </summary>
    /// <param name="Scopes">How many scopes the chain has, to the deepest a chain is followed.</param>
    /// <param name="Open">How many have not closed.</param>
    /// <param name="Closed">How many closed, so how long they took is known.</param>
    /// <param name="Kept">How many openings are at or after the first record the session holds.</param>
    internal readonly record struct ChainMark(int Scopes, int Open, int Closed, int Kept)
    {
        /// <summary>The deepest a chain is followed, as <see cref="LogScopes"/> follows one, which ends a chain a broken file makes a loop of.</summary>
        public const int MostDepth = 256;

        /// <summary>The mark of the chain from <paramref name="scope"/> out, as <paramref name="scopes"/> read it, while <paramref name="first"/> is the first record held.</summary>
        public static ChainMark Of(LogScopes scopes, long scope, long first)
        {
            if (scopes is null)
                throw new ArgumentNullException(nameof(scopes));

            int count = 0;
            int open = 0;
            int closed = 0;
            int kept = 0;
            for (long at = scope; at != 0 && count < MostDepth; at = scopes.Parent(at))
            {
                count++;
                if (scopes.IsOpen(at))
                    open++;
                else if (scopes.Elapsed(at).IsSome)
                    closed++;
                if (at >= first)
                    kept++;
            }

            return new ChainMark(count, open, closed, kept);
        }
    }

    /// <summary>What the details of a row whose record the session holds are of: the record read, and the mark of the chain of scopes it is under.</summary>
    /// <param name="Record">The sequence of the record read: the row's first, or its last once the first is gone.</param>
    /// <param name="Chain">The mark of the scopes it is under.</param>
    internal readonly record struct HeldMark(long Record, ChainMark Chain);

    /// <summary>
    /// What the details of a row are drawn of, as far as they can change: the session, the row, the
    /// late records placed among what was read, and, while the session holds the row's record, which
    /// record it is and the mark of its scopes. The details are made again only when it changed.
    /// </summary>
    /// <param name="Source">The session's records.</param>
    /// <param name="Row">The row, as the view has it.</param>
    /// <param name="Late">The late records the session placed among what the view read.</param>
    /// <param name="Held">What of the row's record is read: none once the session has overwritten it.</param>
    internal readonly record struct DetailsKey(LogRecords Source, LogRow Row, LogLateMark Late, Optional<HeldMark> Held);

    /// <summary>What the details show of the record of the row selected.</summary>
    internal sealed record RecordDetails
    {
        private RecordDetails(long sequence, bool ofFirst, string key, LogLevel level, LogRecordKind kind, DateTimeOffset at, long frame, int thread,
            int records, long suppressed, bool context, string message, ReadOnlyCollection<FieldShown> fields, ReadOnlyCollection<ScopeShown> scopes,
            Optional<SourcePlace> callSite, Optional<SourcePlace> declaration, ReadOnlyCollection<StackFrame> stack, ReadOnlyCollection<StackFrame> thrown)
        {
            Sequence = sequence;
            OfFirst = ofFirst;
            Key = key;
            Level = level;
            Kind = kind;
            At = at;
            Frame = frame;
            Thread = thread;
            Records = records;
            Suppressed = suppressed;
            Context = context;
            Message = message;
            Fields = fields;
            Scopes = scopes;
            CallSite = callSite;
            Declaration = declaration;
            Stack = stack;
            Thrown = thrown;
        }

        /// <summary>The record's sequence: for a scope's opening, the scope's id.</summary>
        public long Sequence { get; }

        /// <summary>
        /// Whether these are the details of the row's first record: a repeat whose first record the
        /// session has overwritten since shows its last.
        /// </summary>
        public bool OfFirst { get; }

        /// <summary>The event's key, as <c>game.save/Saved</c>.</summary>
        public string Key { get; }

        /// <summary>The record's level.</summary>
        public LogLevel Level { get; }

        /// <summary>An event's record or a scope's opening.</summary>
        public LogRecordKind Kind { get; }

        /// <summary>When it was written, on the wall clock.</summary>
        public DateTimeOffset At { get; }

        /// <summary>The frame it was written in, or 0 when the store had none.</summary>
        public long Frame { get; }

        /// <summary>The managed thread it was written on.</summary>
        public int Thread { get; }

        /// <summary>How many records the row stands for: 1, or a repeat's count.</summary>
        public int Records { get; }

        /// <summary>How many records of its event a limit dropped, for the row.</summary>
        public long Suppressed { get; }

        /// <summary>Whether it was written as an error's context.</summary>
        public bool Context { get; }

        /// <summary>Its message, with nothing left out.</summary>
        public string Message { get; }

        /// <summary>Its fields, in the order declared: a stack Unity handed over is <see cref="Stack"/>, not one of them.</summary>
        public ReadOnlyCollection<FieldShown> Fields { get; }

        /// <summary>The scopes it is under, the innermost first.</summary>
        public ReadOnlyCollection<ScopeShown> Scopes { get; }

        /// <summary>Where it was written, when the call said.</summary>
        public Optional<SourcePlace> CallSite { get; }

        /// <summary>Where its event was declared, when its spec says.</summary>
        public Optional<SourcePlace> Declaration { get; }

        /// <summary>
        /// The stack the record was written from: the one the store captured, from the method that made
        /// the call outward, or the one Unity's log handed over with its message. Empty for neither, as
        /// for a record below its event's stack level.
        /// </summary>
        public ReadOnlyCollection<StackFrame> Stack { get; }

        /// <summary>
        /// The frames of the first of the record's exceptions that has any: empty for none, as for an
        /// exception made and never thrown.
        /// </summary>
        public ReadOnlyCollection<StackFrame> Thrown { get; }

        /// <summary>When it was written, the frame and the thread, as the line under its key: <c>2026-11-04 14:03:12.481  ·  frame 1200  ·  thread 1</c>.</summary>
        public string Stamp
        {
            get
            {
                string at = At.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                string frame = Frame != 0 ? $"  ·  frame {Frame.ToString(CultureInfo.InvariantCulture)}" : "";
                string kind = Kind == LogRecordKind.ScopeBegin ? "  ·  a scope's opening" : "";
                return $"{at}{frame}  ·  thread {Thread.ToString(CultureInfo.InvariantCulture)}{kind}";
            }
        }

        /// <summary>
        /// What the details say of the record beside its fields: that the row repeats it, and which of its
        /// records these are, that a limit dropped records of its event before it, and that it was
        /// written as an error's context. Empty for none of them.
        /// </summary>
        public ReadOnlyCollection<string> Notes
        {
            get
            {
                var notes = new List<string>(3);
                if (Records > 1)
                {
                    string which = OfFirst
                        ? "these are the first record's details."
                        : "these are the last record's details, the first being overwritten since.";
                    notes.Add($"Repeated {Records.ToString("N0", CultureInfo.InvariantCulture)} times: {which}");
                }

                if (Suppressed > 0)
                    notes.Add(RowCells.Limited(Suppressed));
                if (Context)
                    notes.Add("Written as an error's context: below the sink's level, and kept because an error after it shares its scope.");

                return notes.AsReadOnly();
            }
        }

        /// <summary>
        /// What opening the row opens: where it was written, or else the first frame in the project of
        /// its stack, or else of its exception, or else where its event was declared; none when nothing says.
        /// </summary>
        public Optional<SourcePlace> Opens
        {
            get
            {
                if (CallSite.IsSome)
                    return CallSite;

                Optional<SourcePlace> written = StackFrames.FirstInProject(Stack);
                if (written.IsSome)
                    return written;

                Optional<SourcePlace> thrown = StackFrames.FirstInProject(Thrown);
                return thrown.IsSome ? thrown : Declaration;
            }
        }

        /// <summary>
        /// The scope that showing only its scope shows: a scope's opening's own, or else the innermost
        /// the record is under; none for a record under no scope.
        /// </summary>
        public Optional<long> ScopeToShow =>
            Kind == LogRecordKind.ScopeBegin ? F.Some(Sequence)
            : Scopes.Count > 0 ? F.Some(Scopes[0].Scope)
            : F.None;

        /// <summary>Equal when they show the same: so a window builds them again only when what they show changed.</summary>
        public bool Equals(RecordDetails? other) =>
            other is not null && Sequence == other.Sequence && OfFirst == other.OfFirst && Key == other.Key && Level == other.Level && Kind == other.Kind && At == other.At && Frame == other.Frame
            && Thread == other.Thread && Records == other.Records && Suppressed == other.Suppressed && Context == other.Context
            && Message == other.Message && CallSite == other.CallSite && Declaration == other.Declaration
            && Fields.SequenceEqual(other.Fields) && Scopes.SequenceEqual(other.Scopes) && Stack.SequenceEqual(other.Stack) && Thrown.SequenceEqual(other.Thrown);

        /// <inheritdoc />
        public override int GetHashCode() => HashCode.Combine(Key, At, Records, Message);

        /// <summary>The details of <paramref name="record"/>, the record of <paramref name="row"/>, under the scopes of <paramref name="scopes"/>.</summary>
        /// <param name="record">The row's record, as <see cref="LogView.Read"/> read it.</param>
        /// <param name="row">The row.</param>
        /// <param name="clock">The clock of the session's records.</param>
        /// <param name="scopes">The scopes the record is under, the innermost first.</param>
        /// <param name="paths">How a file is mapped to the path Unity opens.</param>
        /// <exception cref="ArgumentException"><paramref name="record"/> is not the record of <paramref name="row"/>.</exception>
        public static RecordDetails Of(LogRecord record, LogRow row, LogClock clock, ReadOnlyCollection<ScopeShown> scopes, UnityPaths paths)
        {
            if (scopes is null)
                throw new ArgumentNullException(nameof(scopes));
            if (paths is null)
                throw new ArgumentNullException(nameof(paths));
            RowCells.CheckRecord(record, row);

            LogEventSpec spec = record.Event;
            bool logged = StackFrames.HoldsUnityStack(spec);
            var fields = new List<FieldShown>(record.FieldCount);
            ReadOnlyCollection<StackFrame> stack = record.Stack.HasValue(out LogStack captured)
                ? StackFrames.Of(captured, paths)
                : StackFrames.None;
            ReadOnlyCollection<StackFrame> thrown = StackFrames.None;
            for (int i = 0; i < record.FieldCount; i++)
            {
                LogValue value = record.Field(i);
                LogField field = value.Field;
                if (logged && StackFrames.IsUnityStack(value))
                {
                    if (!value.IsNone)
                        stack = StackFrames.OfUnity(value.ToString(), paths);
                    continue;
                }

                // As a sink writes it, with nothing left out: a personal field is shown and marked so.
                string text = value.ToText(LogRedaction.None);
                if (thrown.Count == 0 && value.Kind == LogValueKind.Exception && !value.IsNone)
                    thrown = StackFrames.OfException(text, paths);

                fields.Add(new FieldShown(field.Name, text, field.Kind, field.Personal));
            }

            LogCallSite site = record.CallSite;
            return new RecordDetails(
                record.Sequence, record.Sequence == row.Sequence, spec.Key, record.Level, record.Kind, clock.At(record.Timestamp), record.Frame, record.Thread, row.Records,
                row.Suppressed, record.IsBacklog, record.ToString(), Array.AsReadOnly(fields.ToArray()), scopes,
                SourcePlace.Of(site.File, site.Line, paths), SourcePlace.Of(spec.Source.File, spec.Source.Line, paths), stack, thrown);
        }
    }
}
