using System;
using System.Globalization;
using System.Text;

namespace Tutan.Logging.Editor
{
    /// <summary>
    /// Writes records as plain text, a line each, for the clipboard: when, the level, the category in
    /// brackets and the message, then how long a closing scope took, how many a limit dropped, and
    /// what it is under.
    /// <code>
    /// 14:03:12.481 INF [game.save] Saved slot 3  (+2)  &lt; Saving slot 3
    /// </code>
    /// A record written as an error's context starts with <c>(context)</c>, and an exception follows
    /// its record whole, each of its lines indented.
    /// </summary>
    internal sealed class TextLinesSink : ILogSink
    {
        private readonly StringBuilder _text = new StringBuilder();

        /// <inheritdoc />
        public string Name => "text";

        /// <summary>What it has written.</summary>
        public string Text => _text.ToString();

        /// <inheritdoc />
        public void Open(LogContext context)
        {
        }

        /// <inheritdoc />
        public void Write(LogRecord record, bool backlog, LogContext context)
        {
            if (context is null)
                throw new ArgumentNullException(nameof(context));

            _text.Append(RowCells.Clock(context.Clock.At(record.Timestamp))).Append(' ').Append(Short(record.Level)).Append(' ');
            if (backlog)
                _text.Append("(context) ");

            _text.Append('[').Append(record.Event.Category).Append("] ").Append(context.Text(record));
            if (record.Kind == LogRecordKind.ScopeEnd)
                _text.Append(": done in ").Append(RowCells.Elapsed(record.Elapsed));
            if (record.Suppressed > 0)
                _text.Append("  (+").Append(record.Suppressed.ToString(CultureInfo.InvariantCulture)).Append(')');
            if (record.Scope != 0 && context.TryScope(record.Scope, out LogRecord under))
                _text.Append("  < ").Append(context.Text(under));
            _text.Append('\n');

            for (int i = 0; i < record.FieldCount; i++)
            {
                LogValue value = record.Field(i);
                if (value.Kind != LogValueKind.Exception || value.IsNone)
                    continue;

                foreach (string line in context.Text(value).Split('\n'))
                    _text.Append("    ").Append(line.TrimEnd('\r')).Append('\n');
            }
        }

        /// <inheritdoc />
        public void Lost(long count, LogContext context) => _text.Append(RowCells.Lost(count)).Append('\n');

        /// <inheritdoc />
        public void Flush()
        {
        }

        /// <summary>A level in three letters, as a line starts with it.</summary>
        public static string Short(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => throw new ArgumentOutOfRangeException(nameof(level), $"{level} is not a level a record has."),
        };
    }
}
