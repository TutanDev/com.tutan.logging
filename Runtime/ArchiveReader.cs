using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Tutan.Functional;

namespace Tutan.Logging
{
    /// <summary>
    /// Makes a <see cref="LogSessionFile"/> of a file's lines, in one call to <see cref="Read"/>: the
    /// header first, then each line, of which it keeps what it can read and says why it skipped the
    /// rest. It reads the lines it is handed; <see cref="LogFile.Read(string)"/> opens the file.
    /// </summary>
    internal sealed class ArchiveReader
    {
        /// <summary>
        /// The longest first line read as a header: far more than any a sink writes, so a file of one
        /// long line, which is no log, is said to be none without being read whole.
        /// </summary>
        private const int MostHeader = 64 * 1024;

        /// <summary>The longest a scope is read as open, a century: so its opening's timestamp is one a record can have.</summary>
        private static readonly double s_mostMilliseconds = TimeSpan.FromDays(36_525).TotalMilliseconds;

        private static readonly string[] s_levels = { "Trace", "Debug", "Information", "Warning", "Error", "Critical" };
        private static readonly Dictionary<string, LogValueKind> s_kinds = Kinds();

        private readonly string _file;
        private readonly DateTimeOffset _started;
        private readonly LogArchive _archive;
        private readonly Dictionary<long, Declared> _byId = new Dictionary<long, Declared>();
        private readonly List<LogSkippedLine> _skipped = new List<LogSkippedLine>();
        private long[] _bits = new long[64];
        private object?[] _refs = new object?[16];
        private int _bitsUsed;
        private int _refsUsed;
        private int _lostLine;

        private ArchiveReader(string file, string program, DateTimeOffset started, LogRedaction redacted)
        {
            _file = file;
            _started = started;
            _archive = LogArchive.OfFile(program, new LogClock(started, 0), redacted);
        }

        /// <summary>
        /// The file <paramref name="lines"/> reads, from its header to its last line, and so as far as a
        /// session still writing it has written; or why it is not a log this reads, with a
        /// <see cref="LogFileReadError"/> as the error's code.
        /// </summary>
        public static Result<LogSessionFile> Read(string file, TextReader lines) =>
            Start(file, Header(lines)).Map(reader => reader.Rest(lines));

        /// <summary>
        /// The first line, as <see cref="TextReader.ReadLine"/> reads one, or null when there is none or
        /// it runs past <see cref="MostHeader"/>.
        /// </summary>
        private static string? Header(TextReader lines)
        {
            var header = new StringBuilder();
            for (int c = lines.Read(); c >= 0; c = lines.Read())
            {
                if (c == '\n')
                    return header.ToString();
                if (c == '\r')
                {
                    if (lines.Peek() == '\n')
                        lines.Read();
                    return header.ToString();
                }

                if (header.Length == MostHeader)
                    return null;
                header.Append((char)c);
            }

            return header.Length > 0 ? header.ToString() : null;
        }

        /// <summary>
        /// A reader for the file whose first line is <paramref name="header"/>, or why it is not a log
        /// this reads, with a <see cref="LogFileReadError"/> as the error's code.
        /// </summary>
        private static Result<ArchiveReader> Start(string file, string? header)
        {
            if (JsonValue.Parse(header) is not JsonValue.Members root)
                return NotALog(file);

            // The format before the rest, since a header of another format need not have this one's shape.
            if (!Number(root, "format", out long format))
                return NotALog(file);
            if (format != JsonLinesSink.Format)
            {
                return F.Error($"{file} is a log of format {format.ToString(CultureInfo.InvariantCulture)}, and this reader reads format {JsonLinesSink.Format.ToString(CultureInfo.InvariantCulture)}",
                    (int)LogFileReadError.UnknownFormat);
            }

            if (!Text(root, "program", out string? program) || !Text(root, "started", out string? started) || !Time(started, out DateTimeOffset start))
                return NotALog(file);

            LogRedaction redacted = LogRedaction.None;
            if (root.TryGet("redact", out JsonValue redact) && redact is JsonValue.Items names)
            {
                foreach (JsonValue name in names.All)
                {
                    redacted |= name is JsonValue.Text { Value: "Personal" } ? LogRedaction.Personal
                        : name is JsonValue.Text { Value: "HomeFolder" } ? LogRedaction.HomeFolder
                        : LogRedaction.None;
                }
            }

            return new ArchiveReader(file, program, start, redacted);
        }

        private static Error NotALog(string file) =>
            F.Error($"{file} is not a log as JsonLinesSink writes one: its first line does not say its format, program and start",
                (int)LogFileReadError.NotALog);

        private static Dictionary<string, LogValueKind> Kinds()
        {
            var kinds = new Dictionary<string, LogValueKind>(StringComparer.Ordinal);
            foreach (LogValueKind kind in (LogValueKind[])Enum.GetValues(typeof(LogValueKind)))
                kinds[kind.ToString()] = kind;

            return kinds;
        }

        /// <summary>Reads the lines after the header, the second first, and makes the file of them.</summary>
        private LogSessionFile Rest(TextReader lines)
        {
            int number = 1;
            for (string? line = lines.ReadLine(); line is not null; line = lines.ReadLine())
                Line(line, ++number);

            return Build();
        }

        /// <summary>Reads line <paramref name="number"/>: a declaration, a record or a loss.</summary>
        private void Line(string line, int number)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            JsonValue? parsed = JsonValue.Parse(line);
            if (parsed is null)
                Skip(number, new LogSkipReason.NotJson());
            else if (parsed is not JsonValue.Members root)
                Skip(number, new LogSkipReason.NotALine());
            else if (root.TryGet("event", out _))
                Declaration(root, number);
            else if (root.TryGet("seq", out _))
                Record(root, number);
            else if (Number(root, "lost", out long lost) && lost > 0)
                Loss(lost, number);
            else
                Skip(number, new LogSkipReason.NotALine());
        }

        /// <summary>
        /// The file of every line read, after the last: its archive, sealed, holds what the lines held
        /// and no more. A loss no record follows is said among the lines skipped, where its line is,
        /// and not kept.
        /// </summary>
        private LogSessionFile Build()
        {
            long lost = _archive.PendingLost;
            if (lost > 0)
            {
                int at = 0;
                while (at < _skipped.Count && _skipped[at].Line < _lostLine)
                    at++;
                _skipped.Insert(at, new LogSkippedLine(_lostLine, new LogSkipReason.LostAtEnd(lost)));
            }

            _archive.Seal();
            return new LogSessionFile(_file, _archive, _skipped.AsReadOnly());
        }

        private void Declaration(JsonValue root, int number)
        {
            if (!Text(root, "event", out string? key) || !Number(root, "id", out long id) || id < 1)
            {
                Skip(number, new LogSkipReason.NotALine());
                return;
            }

            if (_byId.ContainsKey(id))
            {
                Skip(number, new LogSkipReason.DeclaredTwice(key, id));
                return;
            }

            Result<LogEventSpec> spec = Spec(root, key, _archive.Redacted);
            if (!spec.IsSuccess(out LogEventSpec made))
            {
                Skip(number, Refused(key, spec.ErrorUnsafe()));
                return;
            }

            _byId.Add(id, new Declared(_archive.Declare(made), made));
        }

        /// <summary>Why a declaration of <paramref name="key"/> is skipped, as the <see cref="DeclarationError"/> of <paramref name="why"/> says.</summary>
        private static LogSkipReason Refused(string key, Error why) => (DeclarationError)why.Code switch
        {
            DeclarationError.NotADeclaration => new LogSkipReason.NotADeclaration(key, why.Message),
            DeclarationError.NotAnEvent => new LogSkipReason.NotAnEvent(key, why.Message),
            _ => throw new InvalidOperationException($"A declaration was refused with the code {why.Code.ToString(CultureInfo.InvariantCulture)}, which is no {nameof(DeclarationError)}."),
        };

        /// <summary>
        /// The spec a declaration of <paramref name="key"/> declares, in a file whose sink left
        /// <paramref name="redacted"/> out, or why it does not, with a <see cref="DeclarationError"/>
        /// as the error's code: it is not as the format writes one, or what it declares is not an event.
        /// </summary>
        private static Result<LogEventSpec> Spec(JsonValue root, string key, LogRedaction redacted)
        {
            int slash = key.IndexOf('/');
            if (slash < 0)
                return Malformed("its key has no '/' between its category and its name");
            if (!Text(root, "kind", out string? kindName) || kindName is not ("Event" or "Scope"))
                return Malformed("its kind is not Event or Scope");

            Optional<LogLevel> level = F.None;
            if (root.TryGet("lvl", out _))
            {
                if (!Text(root, "lvl", out string? levelName) || !TryLevel(levelName, out LogLevel declared))
                    return Malformed("its level is not a level");
                level = declared;
            }

            if (!Text(root, "template", out string? template))
                return Malformed("it has no template");

            long perSecond = 0;
            if (root.TryGet("perSecond", out _) && (!Number(root, "perSecond", out perSecond) || perSecond is < 0 or > int.MaxValue))
                return Malformed("its limit is not a whole number of records a second");

            if (!root.TryGet("fields", out JsonValue fieldsJson) || fieldsJson is not JsonValue.Items declaredFields)
                return Malformed("it has no fields");

            var fields = new LogField[declaredFields.All.Count];
            for (int i = 0; i < fields.Length; i++)
            {
                Result<LogField> field = Field(declaredFields.All[i], redacted);
                if (!field.IsSuccess(out LogField made))
                    return field.ErrorUnsafe();
                fields[i] = made;
            }

            LogEventKind kind = kindName == "Scope" ? LogEventKind.Scope : LogEventKind.Event;
            return LogEventSpec.OfFile(key.Substring(0, slash), key.Substring(slash + 1), kind, level, template, fields, (int)perSecond, Source(root))
                .Match(
                    why => F.Fail<LogEventSpec>(NotAnEvent(why.Message)),
                    spec => F.Success(spec));
        }

        // Not as the format writes a declaration.
        private static Error Malformed(string what) => F.Error(what, (int)DeclarationError.NotADeclaration);

        // Written as the format writes one, of what a program's declaration would have been refused for.
        private static Error NotAnEvent(string why) => F.Error(why, (int)DeclarationError.NotAnEvent);

        /// <summary>
        /// A field as the file declares it, held as this reader can hold what the file holds of it:
        /// chars, a value of the program's own type, and a field whose value was left out, as strings.
        /// Or why it is not a field, as the end of a sentence, with a <see cref="DeclarationError"/> as
        /// the error's code.
        /// </summary>
        private static Result<LogField> Field(JsonValue declared, LogRedaction redacted)
        {
            if (!Text(declared, "name", out string? name) || !Text(declared, "kind", out string? kindName) || !Text(declared, "type", out string? type))
                return Malformed("a field is not an object with a name, a kind and a type");

            if (!LogNames.IsEventName(name))
                return NotAnEvent($"the field '{name}' is not named as a field is, with a letter or '_' followed by letters, digits and '_'");

            if (!s_kinds.TryGetValue(kindName, out LogValueKind kind))
                return Malformed($"the field {name} is of kind {kindName}, which is not a kind of value");

            bool personal = Flag(declared, "personal");
            bool optional = Flag(declared, "optional");
            string? tag = Text(declared, "tag", out string? given) ? given : null;

            if (personal && (redacted & LogRedaction.Personal) != 0)
                return LogField.Ref(name, LogValueKind.String, type, personal: true, tag, optional);

            if (kind == LogValueKind.Enum)
                return EnumField(declared, name, type, personal, tag, optional);

            return kind switch
            {
                LogValueKind.Boolean => LogField.Value<bool>(name, kind, type, personal, tag, optional),
                LogValueKind.Char => LogField.Value<char>(name, kind, type, personal, tag, optional),
                LogValueKind.SByte => LogField.Value<sbyte>(name, kind, type, personal, tag, optional),
                LogValueKind.Byte => LogField.Value<byte>(name, kind, type, personal, tag, optional),
                LogValueKind.Int16 => LogField.Value<short>(name, kind, type, personal, tag, optional),
                LogValueKind.UInt16 => LogField.Value<ushort>(name, kind, type, personal, tag, optional),
                LogValueKind.Int32 => LogField.Value<int>(name, kind, type, personal, tag, optional),
                LogValueKind.UInt32 => LogField.Value<uint>(name, kind, type, personal, tag, optional),
                LogValueKind.Int64 => LogField.Value<long>(name, kind, type, personal, tag, optional),
                LogValueKind.UInt64 => LogField.Value<ulong>(name, kind, type, personal, tag, optional),
                LogValueKind.Single => LogField.Value<float>(name, kind, type, personal, tag, optional),
                LogValueKind.Double => LogField.Value<double>(name, kind, type, personal, tag, optional),
                LogValueKind.Decimal => LogField.Value<decimal>(name, kind, type, personal, tag, optional),
                LogValueKind.Guid => LogField.Value<Guid>(name, kind, type, personal, tag, optional),
                LogValueKind.DateTime => LogField.Value<DateTime>(name, kind, type, personal, tag, optional),
                LogValueKind.DateTimeOffset => LogField.Value<DateTimeOffset>(name, kind, type, personal, tag, optional),
                LogValueKind.TimeSpan => LogField.Value<TimeSpan>(name, kind, type, personal, tag, optional),
                LogValueKind.String or LogValueKind.Exception => LogField.Ref(name, kind, type, personal, tag, optional),

                // Chars and a value of the program's own type: the file holds their text alone.
                _ => LogField.Ref(name, LogValueKind.String, type, personal, tag, optional),
            };
        }

        private static Result<LogField> EnumField(JsonValue declared, string name, string type, bool personal, string? tag, bool optional)
        {
            if (!declared.TryGet("names", out JsonValue table) || table is not JsonValue.Members members)
                return Malformed($"the enum field {name} has no names");

            var names = new string[members.ByName.Count];
            var values = new long[names.Length];
            int at = 0;
            foreach (KeyValuePair<string, JsonValue> member in members.ByName)
            {
                if (member.Value is not JsonValue.Number number || !number.TryInt64(out long value))
                    return Malformed($"the enum field {name} names {member.Key} with no whole number");

                names[at] = member.Key;
                values[at++] = value;
            }

            return LogField.Enum(name, type, names, values, Flag(declared, "flags"), personal, tag, optional);
        }

        private static LogSource Source(JsonValue root)
        {
            if (!root.TryGet("source", out JsonValue source) || source is not JsonValue.Members)
                return new LogSource("", "", "", 0);

            return new LogSource(
                Text(source, "assembly", out string? assembly) ? assembly : "",
                Text(source, "type", out string? type) ? type : "",
                Text(source, "file", out string? file) ? file : "",
                Number(source, "line", out long line) ? (int)Math.Max(0, Math.Min(line, int.MaxValue)) : 0);
        }

        private void Record(JsonValue root, int number)
        {
            if (!Number(root, "seq", out long sequence) || !Text(root, "key", out string? key) || !Number(root, "id", out long id)
                || !Text(root, "t", out string? time) || !Time(time, out DateTimeOffset at)
                || !Text(root, "lvl", out string? levelName) || !TryLevel(levelName, out LogLevel level))
            {
                Skip(number, new LogSkipReason.NotALine());
                return;
            }

            if (!_byId.TryGetValue(id, out Declared declared))
            {
                Skip(number, new LogSkipReason.Undeclared(key, id));
                return;
            }

            LogEventSpec spec = declared.Spec;
            if (!string.Equals(spec.Key, key, StringComparison.Ordinal))
            {
                Skip(number, new LogSkipReason.NotItsDeclaration(key, $"the declaration it names, event {id.ToString(CultureInfo.InvariantCulture)}, declares {spec.Key}"));
                return;
            }

            LogRecordKind kind = root.TryGet("begin", out _) ? LogRecordKind.ScopeBegin
                : root.TryGet("end", out _) ? LogRecordKind.ScopeEnd
                : LogRecordKind.Event;
            if ((kind == LogRecordKind.Event) != (spec.Kind == LogEventKind.Event))
            {
                Skip(number, new LogSkipReason.NotItsDeclaration(key, spec.Kind == LogEventKind.Scope
                    ? "it is a scope's, and the record neither opens nor closes it"
                    : "it is an event's, and the record opens or closes a scope"));
                return;
            }

            // A store writes every record of an event of a fixed level, and a scope's closing, at that level.
            if (spec.Level.HasValue(out LogLevel always) && level != always)
            {
                Skip(number, new LogSkipReason.NotItsDeclaration(key,
                    $"it is at {levelName}, and its declaration says every record of it is at {s_levels[(int)always]}"));
                return;
            }

            if (Stamps(root, sequence, kind) is { } stamp)
            {
                Skip(number, new LogSkipReason.NotAStamp(stamp));
                return;
            }

            if (_archive.Holds(sequence))
            {
                Skip(number, new LogSkipReason.SequenceTwice(sequence));
                return;
            }

            long timestamp = Timestamp(at - _started);
            _bitsUsed = 0;
            _refsUsed = 0;
            if (Values(root, spec, kind, timestamp) is { } why)
            {
                Skip(number, new LogSkipReason.NotItsDeclaration(key, why));
                return;
            }

            // Each stamp a line leaves out is 0, and Stamps held the ones it has apart.
            Stamp(root, "frame", 0, long.MaxValue, out long frame);
            Stamp(root, "scope", 1, sequence - 1, out long scope);
            Stamp(root, "thread", 0, int.MaxValue, out long thread);
            Stamp(root, "suppressed", 0, int.MaxValue, out long suppressed);
            Stamp(root, "line", 1, int.MaxValue, out long line);
            string? file = Text(root, "file", out string? site) ? site : null;

            var entry = new LogEntry
            {
                Sequence = sequence,
                Timestamp = timestamp,
                Frame = frame,
                Scope = scope,
                CallerFile = file,
                CallerLine = file is null ? 0 : (int)line,
                Event = declared.Index,
                Thread = (int)thread,
                Suppressed = (int)suppressed,
                Bits = (ushort)_bitsUsed,
                Refs = (byte)_refsUsed,
                Level = level,
                Kind = kind,
                Backlog = Flag(root, "context"),
            };

            _archive.Add(entry, _bits.AsSpan(0, _bitsUsed), _refs.AsSpan(0, _refsUsed));
        }

        /// <summary>
        /// The first stamp of a record's line that no record can have, or null: a sequence from 1, a
        /// frame, thread and count from 0, a scope it is under and one it closes that came before it,
        /// an opening's own sequence, how long a scope was open, up to a century, and a call site of a
        /// file and a line from 1.
        /// </summary>
        private static string? Stamps(JsonValue root, long sequence, LogRecordKind kind)
        {
            if (sequence < 1)
                return "seq";
            if (!Stamp(root, "frame", 0, long.MaxValue, out _))
                return "frame";
            if (!Stamp(root, "scope", 1, sequence - 1, out _))
                return "scope";
            if (!Stamp(root, "thread", 0, int.MaxValue, out _))
                return "thread";
            if (!Stamp(root, "suppressed", 0, int.MaxValue, out _))
                return "suppressed";
            if (root.TryGet("file", out JsonValue file) && file is not JsonValue.Text)
                return "file";
            if (!Stamp(root, "line", 1, int.MaxValue, out _) || root.TryGet("line", out _) != root.TryGet("file", out _))
                return "line";
            if (kind == LogRecordKind.ScopeBegin && !Stamp(root, "begin", sequence, sequence, out _))
                return "begin";
            if (kind == LogRecordKind.ScopeEnd && !Stamp(root, "end", 1, sequence - 1, out _))
                return "end";
            if (kind == LogRecordKind.ScopeEnd && !Milliseconds(root, out _))
                return "ms";

            return null;
        }

        /// <summary>Puts a record's values in the bits and refs as a store holds them, or says what is not as its declaration says.</summary>
        private string? Values(JsonValue root, LogEventSpec spec, LogRecordKind kind, long timestamp)
        {
            if (kind == LogRecordKind.ScopeEnd)
            {
                // Stamps held both apart: the opening's sequence, and how long it was open.
                Number(root, "end", out long begin);
                Milliseconds(root, out double elapsed);
                PutSlot(begin);
                PutSlot(timestamp - (long)Math.Round(elapsed * Stopwatch.Frequency / 1000.0));
                return null;
            }

            if (spec.FieldArray.Length == 0)
                return null;
            if (!root.TryGet("fields", out JsonValue fields) || fields is not JsonValue.Members)
                return "it has no fields";

            foreach (LogField field in spec.FieldArray)
            {
                if (!fields.TryGet(field.Name, out JsonValue value))
                    return $"it has no field {field.Name}";
                if (!Value(field, value))
                    return $"its field {field.Name} is not of type {field.Type}";
            }

            return null;
        }

        private bool Value(LogField field, JsonValue value)
        {
            if (field.IsRef)
                return Ref(field, value);

            if (field.IsOptional)
            {
                bool some = value is not JsonValue.Null;
                PutSlot(some ? 1 : 0);
                if (!some)
                {
                    for (int i = 0; i < field.Slots; i++)
                        PutSlot(0);
                    return true;
                }
            }

            var number = value as JsonValue.Number;
            string? text = (value as JsonValue.Text)?.Value;
            CultureInfo invariant = CultureInfo.InvariantCulture;
            return field.Kind switch
            {
                LogValueKind.Boolean => value is JsonValue.Bool flag && Put(flag.Value),
                LogValueKind.Char => text is { Length: 1 } && Put(text[0]),
                LogValueKind.SByte => Whole(value, sbyte.MinValue, sbyte.MaxValue, out long s8) && Put((sbyte)s8),
                LogValueKind.Byte => Whole(value, byte.MinValue, byte.MaxValue, out long u8) && Put((byte)u8),
                LogValueKind.Int16 => Whole(value, short.MinValue, short.MaxValue, out long s16) && Put((short)s16),
                LogValueKind.UInt16 => Whole(value, ushort.MinValue, ushort.MaxValue, out long u16) && Put((ushort)u16),
                LogValueKind.Int32 => Whole(value, int.MinValue, int.MaxValue, out long s32) && Put((int)s32),
                LogValueKind.UInt32 => Whole(value, uint.MinValue, uint.MaxValue, out long u32) && Put((uint)u32),
                LogValueKind.Int64 => Whole(value, long.MinValue, long.MaxValue, out long s64) && Put(s64),
                LogValueKind.UInt64 => number is not null && number.TryUInt64(out ulong u64) && Put(u64),
                LogValueKind.Single => ReadSingle(value, out float f32) && Put(f32),
                LogValueKind.Double => ReadDouble(value, out double f64) && Put(f64),
                LogValueKind.Decimal => number is not null && number.TryDecimal(out decimal money) && Put(money),
                LogValueKind.Guid => Guid.TryParse(text, out Guid id) && Put(id),
                LogValueKind.DateTime => DateTime.TryParseExact(text, "O", invariant, DateTimeStyles.RoundtripKind, out DateTime when) && Put(when),
                LogValueKind.DateTimeOffset => DateTimeOffset.TryParseExact(text, "O", invariant, DateTimeStyles.None, out DateTimeOffset on) && Put(on),
                LogValueKind.TimeSpan => TimeSpan.TryParseExact(text, "c", invariant, out TimeSpan took) && Put(took),
                LogValueKind.Enum => text is not null && field.Names!.TryParse(text, out long member) && Put(member),
                _ => false,
            };
        }

        private bool Ref(LogField field, JsonValue value)
        {
            switch (value)
            {
                case JsonValue.Null:
                    PutRef(null);
                    return true;
                case JsonValue.Text text when field.Kind == LogValueKind.String:
                    PutRef(text.Value);
                    return true;
                case JsonValue.Members when field.Kind == LogValueKind.Exception
                        && Text(value, "type", out string? type) && Text(value, "message", out string? message) && Text(value, "text", out string? text):
                    PutRef(new ArchivedException(type, message, text));
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Copies a value into slots, as a store's writer does, the last zeroed past it.</summary>
        private bool Put<T>(T value) where T : unmanaged
        {
            Span<long> slots = stackalloc long[(SizeOf<T>.Bytes + 7) >> 3];
            slots.Clear();
            MemoryMarshal.Write(MemoryMarshal.AsBytes(slots), ref value);
            foreach (long slot in slots)
                PutSlot(slot);
            return true;
        }

        private void PutSlot(long slot)
        {
            if (_bitsUsed == _bits.Length)
                Array.Resize(ref _bits, _bits.Length * 2);
            _bits[_bitsUsed++] = slot;
        }

        private void PutRef(object? reference)
        {
            if (_refsUsed == _refs.Length)
                Array.Resize(ref _refs, _refs.Length * 2);
            _refs[_refsUsed++] = reference;
        }

        private void Loss(long count, int number)
        {
            _archive.Lose(count);
            _lostLine = number;
        }

        private void Skip(int number, LogSkipReason reason) => _skipped.Add(new LogSkippedLine(number, reason));

        /// <summary>
        /// How long after the store started, in <see cref="Stopwatch"/> ticks, which is what a record's
        /// timestamp is: as far as a timestamp goes, for a time thousands of years off.
        /// </summary>
        private static long Timestamp(TimeSpan since)
        {
            decimal stamp = decimal.Truncate((decimal)since.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond);
            return stamp >= long.MaxValue ? long.MaxValue : stamp <= long.MinValue ? long.MinValue : (long)stamp;
        }

        private static bool Time(string text, out DateTimeOffset time) =>
            DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

        /// <summary>A level as a file names it, from Trace to Critical: a record has no other.</summary>
        private static bool TryLevel(string name, out LogLevel level)
        {
            int index = Array.IndexOf(s_levels, name);
            level = (LogLevel)Math.Max(index, 0);
            return index >= 0;
        }

        private static bool Text(JsonValue element, string name, [NotNullWhen(true)] out string? value)
        {
            value = element.TryGet(name, out JsonValue found) && found is JsonValue.Text text ? text.Value : null;
            return value is not null;
        }

        private static bool Number(JsonValue element, string name, out long value)
        {
            value = 0;
            return element.TryGet(name, out JsonValue found) && found is JsonValue.Number number && number.TryInt64(out value);
        }

        /// <summary>A whole number from <paramref name="least"/> to <paramref name="most"/> a line may leave out, which is then 0: false when it is there and is not one.</summary>
        private static bool Stamp(JsonValue element, string name, long least, long most, out long value)
        {
            value = 0;
            return !element.TryGet(name, out JsonValue found) || Whole(found, least, most, out value);
        }

        private static bool Whole(JsonValue value, long least, long most, out long whole)
        {
            whole = 0;
            return value is JsonValue.Number number && number.TryInt64(out whole) && whole >= least && whole <= most;
        }

        /// <summary>A float as the format writes one: a number, or a string for what JSON has no number for.</summary>
        private static bool ReadSingle(JsonValue value, out float single)
        {
            if (value is JsonValue.Number number)
                return number.TrySingle(out single);

            bool special = Special(value, out double floating);
            single = (float)floating;
            return special;
        }

        /// <summary>A double as the format writes one: a number, or a string for what JSON has no number for.</summary>
        private static bool ReadDouble(JsonValue value, out double floating) =>
            value is JsonValue.Number number ? number.TryDouble(out floating) : Special(value, out floating);

        /// <summary>The string <c>NaN</c>, <c>Infinity</c> or <c>-Infinity</c>, which the format writes for a value JSON has no number for.</summary>
        private static bool Special(JsonValue value, out double floating)
        {
            switch (value)
            {
                case JsonValue.Text { Value: "NaN" }:
                    floating = double.NaN;
                    return true;
                case JsonValue.Text { Value: "Infinity" }:
                    floating = double.PositiveInfinity;
                    return true;
                case JsonValue.Text { Value: "-Infinity" }:
                    floating = double.NegativeInfinity;
                    return true;
                default:
                    floating = 0;
                    return false;
            }
        }

        /// <summary>How long a closed scope was open, in milliseconds, from 0 to a century: false when its line does not say so.</summary>
        private static bool Milliseconds(JsonValue root, out double milliseconds)
        {
            milliseconds = 0;
            return root.TryGet("ms", out JsonValue found) && found is JsonValue.Number number
                && number.TryDouble(out milliseconds) && milliseconds >= 0 && milliseconds <= s_mostMilliseconds;
        }

        private static bool Flag(JsonValue element, string name) =>
            element.TryGet(name, out JsonValue found) && found is JsonValue.Bool { Value: true };

        /// <summary>
        /// Why a declaration is skipped, as the code of the error <see cref="Spec"/> fails with: it is
        /// not as the format writes one, or what it declares is not an event.
        /// </summary>
        private enum DeclarationError
        {
            NotADeclaration = 1,
            NotAnEvent,
        }

        /// <summary>An event a file declares: the index its archive gave it, and its spec.</summary>
        private readonly struct Declared
        {
            public Declared(int index, LogEventSpec spec)
            {
                Index = index;
                Spec = spec;
            }

            public int Index { get; }

            public LogEventSpec Spec { get; }
        }
    }
}
