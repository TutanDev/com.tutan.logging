using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using Tutan.Functional;

namespace Tutan.Logging
{
    /// <summary>
    /// Everything a program knows about one event or scope before it is ever written: its key, level,
    /// template and fields, and where it is declared. A spec is immutable, made once by the code the
    /// generator writes, and registered in <see cref="LogCatalog"/> when its assembly loads. A record
    /// stores only the spec's index, and a reader goes back to the spec for the rest. A file read back
    /// declares specs of its own, which are its archive's alone: the catalog never holds one, and a
    /// store never writes one.
    /// </summary>
    public sealed class LogEventSpec
    {
        private readonly LogField[] _fields;
        private int _index = -1;

        private LogEventSpec(LogSpecOrigin origin, string category, string name, LogEventKind kind, Optional<LogLevel> level,
            LogTemplate template, LogField[] fields, int perSecond, LogSource source)
        {
            Origin = origin;
            Category = category;
            Name = name;
            Key = category + "/" + name;
            Kind = kind;
            Level = level;
            Template = template;
            _fields = fields;
            Fields = Array.AsReadOnly(fields);
            PerSecond = perSecond;
            Source = source;

            // What IsEnabled compares without a level: the event's own, or, for one whose level is
            // chosen at the call, the highest, which asks whether any level of it is on.
            Gate = (byte)level.Or(LogLevel.Critical);

            foreach (LogField field in fields)
            {
                FixedSlots += field.FixedSlots;
                if (field.IsRef)
                    Refs++;
            }
        }

        /// <summary>The category: <c>game.save</c>.</summary>
        public string Category { get; }

        /// <summary>The name in the key: <c>Saved</c>.</summary>
        public string Name { get; }

        /// <summary>
        /// <c>category/Name</c>: what rules, filters and files name the event by, the same in every
        /// run and through a reworded message.
        /// </summary>
        public string Key { get; }

        /// <summary>An event or a scope.</summary>
        public LogEventKind Kind { get; }

        /// <summary>The level every record of it has, or none when each call chooses.</summary>
        public Optional<LogLevel> Level { get; }

        /// <summary>The message, taken apart.</summary>
        public LogTemplate Template { get; }

        /// <summary>The fields, in the order the call writes them.</summary>
        public IReadOnlyList<LogField> Fields { get; }

        /// <summary>The limit it declares, in records a second, or 0 for none. A rule overrides it.</summary>
        public int PerSecond { get; }

        /// <summary>Where it is declared.</summary>
        public LogSource Source { get; }

        /// <summary>
        /// Its place in <see cref="LogCatalog"/>, which is what a record stores: -1 until it is
        /// registered, and always for a file's. It is dense and fixed for the process, and different in
        /// the next one, so it is never written out.
        /// </summary>
        public int Index => Volatile.Read(ref _index);

        /// <summary>Who declared it: this program, whose specs the catalog holds, or a file read back.</summary>
        internal LogSpecOrigin Origin { get; }

        /// <summary>The fields, for the readers that walk them on every record.</summary>
        internal LogField[] FieldArray => _fields;

        /// <summary>The slots every record of it takes, before the chars its chars fields hold.</summary>
        internal int FixedSlots { get; }

        /// <summary>The refs every record of it takes.</summary>
        internal int Refs { get; }

        internal byte Gate { get; }

        // After the catalog published the specs that hold it, and seen by writers that read no lock.
        internal void Register(int index) => Volatile.Write(ref _index, index);

        /// <summary>
        /// A spec the generator declares, checked: every argument is held to what the generator
        /// refused at compile time, so one that is not as it says is a bug, and throws.
        /// </summary>
        /// <param name="category">The category, lowercase dotted words: <c>game.save</c>.</param>
        /// <param name="name">The name in the key: <c>Saved</c>.</param>
        /// <param name="kind">An event or a scope.</param>
        /// <param name="level">The level, or null for an event whose level is chosen at each call. A scope always has one.</param>
        /// <param name="template">The message, whose holes name fields.</param>
        /// <param name="fields">The fields, in the order the call writes them.</param>
        /// <param name="perSecond">How many records a second a store keeps before it counts the rest instead; 0 for no limit.</param>
        /// <param name="source">Where it is declared.</param>
        /// <exception cref="ArgumentException">Any of them is malformed, or the template names a field there is not.</exception>
        /// <remarks>
        /// It takes a nullable level rather than an <see cref="Optional{T}"/>, so the code the
        /// generator writes into an assembly names no type of Tutan.Functional, and an assembly that
        /// declares events needs no reference to it.
        /// </remarks>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static LogEventSpec Declare(string category, string name, LogEventKind kind, LogLevel? level, string template,
            LogField[] fields, int perSecond, LogSource source)
        {
            if (category is null)
                throw new ArgumentNullException(nameof(category));
            if (name is null)
                throw new ArgumentNullException(nameof(name));
            if (template is null)
                throw new ArgumentNullException(nameof(template));
            if (fields is null)
                throw new ArgumentNullException(nameof(fields));
            if (source is null)
                throw new ArgumentNullException(nameof(source));

            Optional<LogLevel> optional = level.HasValue ? F.Some(level.Value) : F.None;
            return Make(LogSpecOrigin.Program, category, name, kind, optional, template, fields, perSecond, source).Match(
                error => throw new ArgumentException($"{category}/{name} is not an event: {error.Message}."),
                spec => spec);
        }

        /// <summary>
        /// A spec a file declares, held to what <see cref="Declare"/> holds one to, or, as the end of a
        /// sentence, why it is not an event: for a reader of a file, whose declarations no compiler
        /// checked. It is the file's own, so the catalog never holds it and a store never writes it.
        /// </summary>
        internal static Result<LogEventSpec> OfFile(string category, string name, LogEventKind kind, Optional<LogLevel> level,
            string template, LogField[] fields, int perSecond, LogSource source) =>
            Make(LogSpecOrigin.File, category, name, kind, level, template, fields, perSecond, source);

        private static Result<LogEventSpec> Make(LogSpecOrigin origin, string category, string name, LogEventKind kind,
            Optional<LogLevel> level, string template, LogField[] fields, int perSecond, LogSource source)
        {
            if (Check(category, name, kind, level, fields, perSecond) is { } wrong)
                return F.Error(wrong);

            return LogTemplate.Create(template, fields).Map(made =>
                new LogEventSpec(origin, category, name, kind, level, made, (LogField[])fields.Clone(), perSecond, source));
        }

        /// <summary>The key.</summary>
        public override string ToString() => Key;

        /// <summary>What is wrong with everything but the template, as the end of a sentence, or null when nothing is.</summary>
        private static string? Check(string category, string name, LogEventKind kind, Optional<LogLevel> level,
            LogField[] fields, int perSecond)
        {
            if (!LogNames.IsCategory(category))
                return $"'{category}' is not a category, which is lowercase words of letters and digits joined by dots, as game.save";
            if (!LogNames.IsEventName(name))
                return $"'{name}' is not an event's name, which is a letter or '_' followed by letters, digits and '_'";
            if (level.HasValue(out LogLevel declared) && declared >= LogLevel.None)
                return "it is declared at None, which is a threshold and not a level a record has";
            if (kind == LogEventKind.Scope && level.IsNone)
                return "it is a scope with no level, and a scope's level is its own, never chosen at the call";
            if (fields.Length > LogNames.MaxFields)
                return $"it has {fields.Length} fields, and an event has at most {LogNames.MaxFields}";
            if (perSecond < 0)
                return $"it is limited to {perSecond} records a second, which is less than none";

            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i] is null)
                    return $"its field {i} is null";

                for (int j = 0; j < i; j++)
                {
                    if (string.Equals(fields[i].Name, fields[j].Name, StringComparison.OrdinalIgnoreCase))
                        return $"it has two fields named {fields[i].Name}";
                }
            }

            return null;
        }
    }

    /// <summary>Where an event is declared: the assembly, the class, and the file and line of the method.</summary>
    /// <param name="Assembly">The assembly's name.</param>
    /// <param name="Type">The class's full name.</param>
    /// <param name="File">The file, as the compiler was given it.</param>
    /// <param name="Line">The method's line, from 1.</param>
    public sealed record LogSource(string Assembly, string Type, string File, int Line)
    {
        /// <summary><c>File:Line</c>.</summary>
        public override string ToString() => $"{File}:{Line}";
    }

    /// <summary>
    /// A message template taken apart: the text between its holes, and the field each hole writes.
    /// There is always one more literal than there are holes.
    /// </summary>
    public sealed class LogTemplate
    {
        private readonly string[] _literals;
        private readonly LogHole[] _holes;
        private readonly string?[] _formats;

        private LogTemplate(string text, string[] literals, LogHole[] holes, int fields)
        {
            Text = text;
            _literals = literals;
            _holes = holes;
            Literals = Array.AsReadOnly(literals);
            Holes = Array.AsReadOnly(holes);

            // Backwards, so the first hole that names a field is the one whose format it keeps.
            _formats = new string?[fields];
            for (int i = holes.Length - 1; i >= 0; i--)
                _formats[holes[i].Field] = holes[i].Format;
        }

        /// <summary>The template as it was declared.</summary>
        public string Text { get; }

        /// <summary>The text around the holes, with <c>{{</c> and <c>}}</c> made single.</summary>
        public IReadOnlyList<string> Literals { get; }

        /// <summary>The holes, in the order they appear.</summary>
        public IReadOnlyList<LogHole> Holes { get; }

        internal string[] LiteralArray => _literals;

        internal LogHole[] HoleArray => _holes;

        /// <summary>The format of the first hole that names field <paramref name="field"/>, or null when none names it or that hole has none.</summary>
        internal string? FormatOf(int field) => _formats[field];

        /// <summary>The template as it was declared.</summary>
        public override string ToString() => Text;

        /// <summary><paramref name="text"/> taken apart, each hole matched to the field of its name, ignoring case, or why it cannot be, as the end of a sentence.</summary>
        internal static Result<LogTemplate> Create(string text, LogField[] fields)
        {
            ParsedTemplate? parsed = TemplateParser.Parse(text, out string? error);
            if (parsed is null)
                return F.Error("its template does not parse: " + error);

            var holes = new LogHole[parsed.Holes.Length];
            for (int h = 0; h < holes.Length; h++)
            {
                TemplateHole hole = parsed.Holes[h];
                int field = -1;
                for (int i = 0; i < fields.Length && field < 0; i++)
                {
                    if (string.Equals(fields[i].Name, hole.Name, StringComparison.OrdinalIgnoreCase))
                        field = i;
                }

                if (field < 0)
                    return F.Error($"its template has a hole {{{hole.Name}}}, and it has no field of that name");

                holes[h] = new LogHole(field, hole.Alignment, hole.Format.Length == 0 ? null : hole.Format);
            }

            return new LogTemplate(text, parsed.Literals, holes, fields.Length);
        }
    }

    /// <summary>One hole of a template.</summary>
    /// <param name="Field">The index of the field it writes.</param>
    /// <param name="Alignment">The width it pads to: on the left when positive, on the right when negative, none when 0.</param>
    /// <param name="Format">What follows its colon, handed to the value's formatting, or null.</param>
    public readonly record struct LogHole(int Field, int Alignment, string? Format);

    /// <summary>Who declared a spec: the program running, or a file a reader read back.</summary>
    internal enum LogSpecOrigin : byte
    {
        /// <summary>This program's code declared it, and the catalog holds it once registered.</summary>
        Program,

        /// <summary>A file declared it, and only the archive read from that file holds it.</summary>
        File,
    }
}
