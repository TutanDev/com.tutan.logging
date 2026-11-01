using System;
using System.Globalization;
using UnityEngine;

namespace Tutan.Logging
{
    /// <summary>
    /// Writes records to Unity's Console, and in a player to its log file, as one row each:
    /// <code>
    /// game.save  Saved slot 3 in 00:00:01.2000000  &lt; Saving slot 3
    /// SaveManager.cs:42  frame 1240
    /// </code>
    /// The category and the message, how many a limit dropped before it, what it is under, and on the
    /// second line the call site, which in the editor is a link that opens it. A record written as an
    /// error's context starts with <c>(context)</c>, an exception follows its record whole, and a
    /// scope's closing says how long it was open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A row is written without a stack trace, since the stack Unity would capture is the drain's,
    /// not the call's. In the editor the row also opens the call site on a double-click, through an
    /// internal Unity method; if a Unity version does not have it, the link in the message still
    /// opens it.
    /// </para>
    /// <para>
    /// It skips what <see cref="UnityLogBridge"/> captured from Unity's own log, which the Console
    /// already shows, and the bridge skips what this sink writes, so neither feeds the other. A
    /// row is a string, which Unity's log needs, so this sink allocates for every record it writes:
    /// what it writes is held to the sink's level, and everything below it costs nothing here.
    /// </para>
    /// </remarks>
    public sealed class UnityConsoleSink : ILogSink
    {
        private readonly bool _openOnDoubleClick;
        private readonly object[] _args = new object[1];
        private char[] _chars = new char[512];
        private UnityPaths? _paths;
#if UNITY_EDITOR
        private ConsoleRowLocation? _rows;
#endif

        /// <summary>A sink that writes to Unity's Console.</summary>
        /// <param name="openOnDoubleClick">
        /// Whether a row opens its call site when it is double-clicked, which, in the editor, writes it
        /// through an internal Unity method; false writes every row with the public API, and leaves the
        /// link in the message.
        /// </param>
        /// <param name="name">What the log calls the sink.</param>
        public UnityConsoleSink(bool openOnDoubleClick = true, string name = "unity console")
        {
            _openOnDoubleClick = openOnDoubleClick;
            Name = name;
        }

        /// <inheritdoc />
        public string Name { get; }

        /// <summary>Learns where the project and its packages are, which Unity says only on its main thread.</summary>
        public void Open(LogContext context)
        {
            _paths = UnityPaths.ForThisProject();
#if UNITY_EDITOR
            _rows = _openOnDoubleClick ? ConsoleRowLocation.TryBind() : null;
#endif
        }

        /// <inheritdoc />
        public void Write(LogRecord record, bool backlog, LogContext context)
        {
            if (ReferenceEquals(record.Event, UnityLog.Events.Logged))
                return;

            int length;
            while (!TryRow(record, backlog, context, _chars, out length))
                _chars = new char[_chars.Length * 2];

            string row = new string(_chars, 0, length);
            for (int i = 0; i < record.FieldCount; i++)
            {
                LogValue value = record.Field(i);
                if (value.Kind == LogValueKind.Exception && !value.IsNone)
                    row += "\n" + Redacted(value.AsRef()!.ToString(), value.Field, context.Redact);
            }

            LogType type = TypeOf(record.Level);
            LogCallSite site = record.CallSite;
            if (!site.IsKnown)
            {
                Send(type, row);
                return;
            }

            string file = _paths!.Map(site.File!);
            string line = site.Line.ToString(CultureInfo.InvariantCulture);
            string frame = record.Frame != 0 ? "  frame " + record.Frame.ToString(CultureInfo.InvariantCulture) : "";
#if UNITY_EDITOR
            row += $"\n<a href=\"{file}\" line=\"{line}\">{UnityPaths.FileName(file)}:{line}</a>{frame}";
            using (UnityConsoleEcho.Mark())
            {
                if (_rows is not null && _rows.TryWrite(type, row, file, site.Line))
                    return;
            }
#else
            row += $"\n(at {file}:{line}){frame}";
#endif
            Send(type, row);
        }

        /// <inheritdoc />
        public void Lost(long count, LogContext context) =>
            Send(LogType.Warning, $"{count.ToString("N0", CultureInfo.InvariantCulture)} {(count == 1 ? "record was" : "records were")} overwritten before the Console read them");

        /// <inheritdoc />
        public void Flush()
        {
        }

        /// <summary>The type of row a record of <paramref name="level"/> is: a warning for a Warning, an error for an Error or a Critical, and a log for the rest.</summary>
        public static LogType TypeOf(LogLevel level) => level switch
        {
            LogLevel.Warning => LogType.Warning,
            LogLevel.Error or LogLevel.Critical => LogType.Error,
            _ => LogType.Log,
        };

        private void Send(LogType type, string row)
        {
            _args[0] = row;
            using (UnityConsoleEcho.Mark())
                Debug.LogFormat(type, LogOption.NoStacktrace, null, "{0}", _args);
            _args[0] = null!;
        }

        /// <summary>An exception's whole text, with what the sink redacts left out.</summary>
        private static string Redacted(string text, LogField field, LogRedaction redact)
        {
            if (field.Personal && (redact & LogRedaction.Personal) != 0)
                return LogText.PersonalText;
            if ((redact & LogRedaction.HomeFolder) == 0)
                return text;

            char[] chars = text.ToCharArray();
            return new string(chars, 0, LogText.WithoutHome(chars));
        }

        /// <summary>The row's first line: whether context, the category, the message, what a limit dropped, and what it is under.</summary>
        private static bool TryRow(LogRecord record, bool backlog, LogContext context, Span<char> row, out int written)
        {
            written = 0;
            if (backlog && !LogText.Append("(context) ", row, ref written))
                return false;
            if (!LogText.Append(record.Event.Category, row, ref written) || !LogText.Append("  ", row, ref written))
                return false;
            if (!context.TryFormat(record, row.Slice(written), out int message))
                return false;
            written += message;

            if (record.Kind == LogRecordKind.ScopeEnd)
            {
                if (!LogText.Append(": done in ", row, ref written)
                    || !record.Elapsed.TotalMilliseconds.TryFormat(row.Slice(written), out int ms, "0.0", CultureInfo.InvariantCulture))
                    return false;
                written += ms;
                if (!LogText.Append(" ms", row, ref written))
                    return false;
            }

            if (record.Suppressed > 0)
            {
                if (!LogText.Append("  (+", row, ref written)
                    || !record.Suppressed.TryFormat(row.Slice(written), out int suppressed, default, CultureInfo.InvariantCulture))
                    return false;
                written += suppressed;
                if (!LogText.Append(")", row, ref written))
                    return false;
            }

            if (record.Scope != 0 && context.TryScope(record.Scope, out LogRecord under))
            {
                if (!LogText.Append("  < ", row, ref written) || !under.TryFormat(row.Slice(written), out int scope, context.Redact))
                    return false;
                written += scope;
            }

            return true;
        }
    }
}
