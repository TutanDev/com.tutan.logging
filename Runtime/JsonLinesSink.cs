using System;
using System.Collections.Generic;
using System.IO;
using Tutan.Functional;

namespace Tutan.Logging
{
    /// <summary>
    /// Writes records as JSON lines, one object a line, after a first line that says the format's
    /// version, the program, when the store started, and what the sink left out. Before the first
    /// record of each event, a line declares it, as its spec does, so the file says what its records
    /// are without the program that wrote them. A declaration numbers its event, from 1 in each file,
    /// and each record names the declaration it is read with by that <c>id</c>, as well as its event by
    /// its key: two assemblies can declare one key, and a reader of the file can tell their records apart.
    /// <code>
    /// {"format":1,"program":"Sample Game","started":"2026-11-01T10:24:09.0000000+01:00","redact":["Personal"]}
    /// {"event":"game.save/Saved","id":1,"kind":"Event","lvl":"Information","template":"Saved slot {Slot} in {Elapsed}","fields":[{"name":"Slot","kind":"Int32","type":"int"},{"name":"Elapsed","kind":"TimeSpan","type":"System.TimeSpan"}],"source":{"assembly":"Assembly-CSharp","type":"Game.SaveLog","file":"C:\\Projects\\MyGame\\Assets\\Game\\SaveLog.cs","line":12}}
    /// {"seq":4190,"scope":4182,"t":"2026-11-01T10:24:11.3510000+01:00","frame":1240,"thread":1,"lvl":"Information","key":"game.save/Saved","id":1,"msg":"Saved slot 3 in 00:00:01.2000000","file":"C:\\Projects\\MyGame\\Assets\\Game\\SaveManager.cs","line":42,"fields":{"Slot":3,"Elapsed":"00:00:01.2000000"}}
    /// </code>
    /// A scope's opening has <c>begin</c>, its own sequence, and its closing has <c>end</c>, the
    /// opening's, and <c>ms</c>, how long it was open. A record written as an error's context has
    /// <c>"context":true</c>. A record with a call site has <c>file</c> and <c>line</c>, the file as the
    /// compiler wrote it, and a record with a stack ends with <c>stack</c>, its frames from the call
    /// outward, as <c>{"method":"Game.SaveManager.Save(int)","file":"...","line":42}</c>, a frame with
    /// no symbols without <c>file</c> and <c>line</c>. Numbers and bools are JSON's own, a number that is not finite is a string, an
    /// exception is an object of its type, message and text, and every other value is its formatted
    /// text: a value of the program's own type formatted as the first hole that names it formats it,
    /// since a reader has only its text.
    /// </summary>
    /// <remarks>Its buffers are reused for each line, so writing allocates nothing once they have grown, but for an exception's text.</remarks>
    public sealed class JsonLinesSink : ILogSink, IDisposable
    {
        /// <summary>The version of the format, on the first line.</summary>
        public const int Format = 1;

        private static readonly string[] s_levels = { "Trace", "Debug", "Information", "Warning", "Error", "Critical", "None" };
        private static readonly string[] s_kinds = Enum.GetNames(typeof(LogValueKind));

        private readonly Stream _stream;
        private readonly bool _leaveOpen;
        private readonly JsonLine _json = new JsonLine();

        // The number each event this file declares goes by, in the order it was declared.
        private readonly Dictionary<LogEventSpec, int> _ids = new Dictionary<LogEventSpec, int>();
        private char[] _chars = new char[512];
        private bool _disposed;

        /// <summary>A sink that writes to <paramref name="stream"/>.</summary>
        /// <param name="stream">Where the lines go.</param>
        /// <param name="leaveOpen">Whether disposing the sink leaves the stream open.</param>
        /// <param name="name">What the log calls the sink: a file's path, for one.</param>
        public JsonLinesSink(Stream stream, bool leaveOpen = false, string name = "json")
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
            _leaveOpen = leaveOpen;
            Name = name;
        }

        /// <inheritdoc />
        public string Name { get; }

        /// <summary>Writes the first line.</summary>
        public void Open(LogContext context)
        {
            _json.Begin();
            _json.Name("format");
            _json.Number(Format);
            _json.Name("program");
            _json.String(context.Program);
            _json.Name("started");
            _json.Time(context.Clock.Started);
            if (context.Redact != LogRedaction.None)
            {
                _json.Name("redact");
                _json.StartArray();
                if ((context.Redact & LogRedaction.Personal) != 0)
                    _json.String("Personal");
                if ((context.Redact & LogRedaction.HomeFolder) != 0)
                    _json.String("HomeFolder");
                _json.EndArray();
            }

            _json.End(_stream);
        }

        /// <inheritdoc />
        public void Write(LogRecord record, bool backlog, LogContext context)
        {
            LogEventSpec spec = record.Event;
            if (!_ids.TryGetValue(spec, out int id))
            {
                id = _ids.Count + 1;
                _ids.Add(spec, id);
                Declare(spec, id, context.Redact);
            }

            _json.Begin();
            _json.Name("seq");
            _json.Number(record.Sequence);
            if (record.Kind == LogRecordKind.ScopeBegin)
            {
                _json.Name("begin");
                _json.Number(record.Begin);
            }

            if (record.Kind == LogRecordKind.ScopeEnd)
            {
                _json.Name("end");
                _json.Number(record.Begin);
            }

            if (record.Scope != 0)
            {
                _json.Name("scope");
                _json.Number(record.Scope);
            }

            _json.Name("t");
            _json.Time(context.Clock.At(record.Timestamp));
            if (record.Frame != 0)
            {
                _json.Name("frame");
                _json.Number(record.Frame);
            }

            _json.Name("thread");
            _json.Number(record.Thread);
            _json.Name("lvl");
            _json.String(Level(record.Level));
            _json.Name("key");
            _json.String(spec.Key);
            _json.Name("id");
            _json.Number(id);

            if (record.Kind == LogRecordKind.ScopeEnd)
            {
                _json.Name("ms");
                _json.Number(Math.Round(record.Elapsed.TotalMilliseconds, 3));
            }
            else
            {
                int length;
                while (!record.TryFormat(_chars, out length, context.Redact))
                    Grow();
                _json.Name("msg");
                _json.String(_chars.AsSpan(0, length));
            }

            if (record.Suppressed > 0)
            {
                _json.Name("suppressed");
                _json.Number(record.Suppressed);
            }

            if (backlog)
            {
                _json.Name("context");
                _json.Bool(true);
            }

            LogCallSite site = record.CallSite;
            if (site.IsKnown)
            {
                WriteText("file", site.File!, context.Redact);
                _json.Name("line");
                _json.Number(site.Line);
            }

            if (record.FieldCount > 0)
            {
                _json.Name("fields");
                _json.StartObject();
                for (int i = 0; i < record.FieldCount; i++)
                    WriteField(record.Field(i), spec.Template.FormatOf(i), context.Redact);
                _json.EndObject();
            }

            if (record.Stack.HasValue(out LogStack stack))
                WriteStack(stack, context.Redact);

            _json.End(_stream);
        }

        /// <inheritdoc />
        public void Lost(long count, LogContext context)
        {
            _json.Begin();
            _json.Name("lost");
            _json.Number(count);
            _json.End(_stream);
        }

        /// <inheritdoc />
        public void Flush() => _stream.Flush();

        /// <summary>Flushes, and disposes the stream unless it was to be left open.</summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _stream.Flush();
            if (!_leaveOpen)
                _stream.Dispose();
        }

        private static string Level(LogLevel level) => (uint)level < (uint)s_levels.Length ? s_levels[(int)level] : "None";

        private static string Kind(LogValueKind kind) => (uint)kind < (uint)s_kinds.Length ? s_kinds[(int)kind] : kind.ToString();

        /// <summary>Writes the line that declares <paramref name="spec"/>: what a reader needs to read its records back as values.</summary>
        private void Declare(LogEventSpec spec, int id, LogRedaction redact)
        {
            _json.Begin();
            _json.Name("event");
            _json.String(spec.Key);
            _json.Name("id");
            _json.Number(id);
            _json.Name("kind");
            _json.String(spec.Kind == LogEventKind.Scope ? "Scope" : "Event");
            if (spec.Level.HasValue(out LogLevel level))
            {
                _json.Name("lvl");
                _json.String(Level(level));
            }

            _json.Name("template");
            _json.String(spec.Template.Text);
            if (spec.PerSecond > 0)
            {
                _json.Name("perSecond");
                _json.Number(spec.PerSecond);
            }

            _json.Name("fields");
            _json.StartArray();
            foreach (LogField field in spec.FieldArray)
            {
                _json.StartObject();
                _json.Name("name");
                _json.String(field.Name);
                _json.Name("kind");
                _json.String(Kind(field.Kind));
                _json.Name("type");
                _json.String(field.Type);
                if (field.Personal)
                {
                    _json.Name("personal");
                    _json.Bool(true);
                }

                if (field.Tag.HasValue(out string tag))
                {
                    _json.Name("tag");
                    _json.String(tag);
                }

                if (field.IsOptional)
                {
                    _json.Name("optional");
                    _json.Bool(true);
                }

                if (field.Names is { } names)
                {
                    _json.Name("names");
                    _json.StartObject();
                    for (int i = 0; i < names.Names.Length; i++)
                    {
                        _json.Name(names.Names[i]);
                        _json.Number(names.Values[i]);
                    }

                    _json.EndObject();
                    if (names.Flags)
                    {
                        _json.Name("flags");
                        _json.Bool(true);
                    }
                }

                _json.EndObject();
            }

            _json.EndArray();

            _json.Name("source");
            _json.StartObject();
            _json.Name("assembly");
            _json.String(spec.Source.Assembly);
            _json.Name("type");
            _json.String(spec.Source.Type);
            WriteText("file", spec.Source.File, redact);
            _json.Name("line");
            _json.Number(spec.Source.Line);
            _json.EndObject();
            _json.End(_stream);
        }

        private void WriteField(LogValue value, string? format, LogRedaction redact)
        {
            _json.Name(value.Field.Name);
            if (value.Field.Personal && (redact & LogRedaction.Personal) != 0)
            {
                _json.String(LogText.PersonalText);
                return;
            }

            if (value.IsNone)
            {
                _json.Null();
                return;
            }

            switch (value.Kind)
            {
                case LogValueKind.Boolean:
                    _json.Bool(value.Read<bool>());
                    return;
                case LogValueKind.SByte or LogValueKind.Byte or LogValueKind.Int16 or LogValueKind.UInt16
                    or LogValueKind.Int32 or LogValueKind.UInt32 or LogValueKind.Int64:
                    _json.Number(value.AsInt64());
                    return;
                case LogValueKind.UInt64:
                    _json.Number(value.Read<ulong>());
                    return;
                case LogValueKind.Single:
                    _json.Number(value.Read<float>());
                    return;
                case LogValueKind.Double:
                    _json.Number(value.Read<double>());
                    return;
                case LogValueKind.Decimal:
                    _json.Number(value.Read<decimal>());
                    return;
                case LogValueKind.Exception:
                    var exception = (Exception)value.AsRef()!;
                    _json.StartObject();
                    _json.Name("type");
                    _json.String(LogText.TypeName(exception, full: true));
                    WriteText("message", exception.Message, redact);
                    WriteText("text", exception.ToString(), redact);
                    _json.EndObject();
                    return;
                default:
                    // Only the program's own type is written as a hole formats it: a reader has its
                    // text alone, where every other kind is read back as a value its holes format.
                    int length;
                    while (!LogText.TryFormatRedacted(value, _chars, out length, value.Kind == LogValueKind.Formattable ? format : null, redact))
                        Grow();
                    _json.String(_chars.AsSpan(0, length));
                    return;
            }
        }

        /// <summary>The stack a record was written from: its frames, each its method, and its file and line when known.</summary>
        private void WriteStack(LogStack stack, LogRedaction redact)
        {
            _json.Name("stack");
            _json.StartArray();
            IReadOnlyList<LogStackFrame> frames = stack.Frames;
            for (int i = 0; i < frames.Count; i++)
            {
                LogStackFrame frame = frames[i];
                _json.StartObject();
                _json.Name("method");
                _json.String(frame.Method);
                if (frame.At.IsKnown)
                {
                    WriteText("file", frame.At.File!, redact);
                    _json.Name("line");
                    _json.Number(frame.At.Line);
                }

                _json.EndObject();
            }

            _json.EndArray();
        }

        private void WriteText(string name, string text, LogRedaction redact)
        {
            _json.Name(name);
            if ((redact & LogRedaction.HomeFolder) == 0)
            {
                _json.String(text);
                return;
            }

            while (_chars.Length < text.Length)
                Grow();
            text.AsSpan().CopyTo(_chars);
            _json.String(_chars.AsSpan(0, LogText.WithoutHome(_chars.AsSpan(0, text.Length))));
        }

        private void Grow() => _chars = new char[_chars.Length * 2];
    }
}
