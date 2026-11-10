using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Tutan.Functional;

namespace Tutan.Logging.Editor
{
    /// <summary>A column of the console's rows, from 1, so the default is none of them.</summary>
    internal enum ConsoleColumn
    {
        /// <summary>When the record was written.</summary>
        Time = 1,

        /// <summary>The frame it was written in.</summary>
        Frame,

        /// <summary>Its level.</summary>
        Level,

        /// <summary>Its event's category.</summary>
        Category,

        /// <summary>Its event's name.</summary>
        Event,

        /// <summary>Its message, with what the row shows of itself: the column that fills.</summary>
        Message,

        /// <summary>How long a scope's row took.</summary>
        Duration,

        /// <summary>Where it was written.</summary>
        CallSite,

        /// <summary>The managed thread it was written on.</summary>
        Thread,
    }

    /// <summary>
    /// The console's columns: the one list of them, in the order they are drawn, and what each is.
    /// Each throws <see cref="ArgumentOutOfRangeException"/> for a value of <see cref="ConsoleColumn"/>
    /// not in <see cref="All"/>, so no such value is ever taken for one of them.
    /// </summary>
    internal static class ConsoleColumns
    {
        private static readonly ReadOnlyCollection<ConsoleColumn> s_all = Array.AsReadOnly(new[]
        {
            ConsoleColumn.Time, ConsoleColumn.Frame, ConsoleColumn.Level, ConsoleColumn.Category, ConsoleColumn.Event,
            ConsoleColumn.Message, ConsoleColumn.Duration, ConsoleColumn.CallSite, ConsoleColumn.Thread,
        });

        // Each fit made once, in pixels: least, at first, most, and whether it is shown at first.
        private static readonly ColumnFit s_time = new ColumnFit.Sized(60, 92, 160, shownByDefault: true);
        private static readonly ColumnFit s_frame = new ColumnFit.Sized(32, 52, 120, shownByDefault: true);
        private static readonly ColumnFit s_level = new ColumnFit.Sized(28, 36, 80, shownByDefault: true);
        private static readonly ColumnFit s_category = new ColumnFit.Sized(48, 140, 400, shownByDefault: true);
        private static readonly ColumnFit s_event = new ColumnFit.Sized(48, 120, 400, shownByDefault: false);
        private static readonly ColumnFit s_message = new ColumnFit.Fills(120);
        private static readonly ColumnFit s_duration = new ColumnFit.Sized(40, 64, 160, shownByDefault: true);
        private static readonly ColumnFit s_callSite = new ColumnFit.Sized(60, 160, 600, shownByDefault: true);
        private static readonly ColumnFit s_thread = new ColumnFit.Sized(32, 48, 120, shownByDefault: false);

        /// <summary>Every column, in the order drawn.</summary>
        public static ReadOnlyCollection<ConsoleColumn> All => s_all;

        /// <summary>
        /// The name <paramref name="column"/> goes by in the window: the <c>Column</c>'s name and its
        /// cells' class, which the stylesheet reads, as <c>call-site</c>. A file holds a name of its
        /// own, <see cref="ColumnEntry.NameOf"/>, so restyling a column changes no file.
        /// </summary>
        public static string Name(ConsoleColumn column) => column switch
        {
            ConsoleColumn.Time => "time",
            ConsoleColumn.Frame => "frame",
            ConsoleColumn.Level => "level",
            ConsoleColumn.Category => "category",
            ConsoleColumn.Event => "event",
            ConsoleColumn.Message => "message",
            ConsoleColumn.Duration => "duration",
            ConsoleColumn.CallSite => "call-site",
            ConsoleColumn.Thread => "thread",
            _ => throw Unknown(column),
        };

        /// <summary>What the header calls <paramref name="column"/>, as <c>Call Site</c>.</summary>
        public static string Title(ConsoleColumn column) => column switch
        {
            ConsoleColumn.Time => "Time",
            ConsoleColumn.Frame => "Frame",
            ConsoleColumn.Level => "Level",
            ConsoleColumn.Category => "Category",
            ConsoleColumn.Event => "Event",
            ConsoleColumn.Message => "Message",
            ConsoleColumn.Duration => "Duration",
            ConsoleColumn.CallSite => "Call Site",
            ConsoleColumn.Thread => "Thread",
            _ => throw Unknown(column),
        };

        /// <summary>How <paramref name="column"/> is sized: the message fills, and every other has a width within bounds.</summary>
        public static ColumnFit Fit(ConsoleColumn column) => column switch
        {
            ConsoleColumn.Time => s_time,
            ConsoleColumn.Frame => s_frame,
            ConsoleColumn.Level => s_level,
            ConsoleColumn.Category => s_category,
            ConsoleColumn.Event => s_event,
            ConsoleColumn.Message => s_message,
            ConsoleColumn.Duration => s_duration,
            ConsoleColumn.CallSite => s_callSite,
            ConsoleColumn.Thread => s_thread,
            _ => throw Unknown(column),
        };

        /// <summary>Whether <paramref name="column"/> is the one that fills: always shown, and keeping no width.</summary>
        public static bool Fills(ConsoleColumn column) => Fit(column).Match(static _ => true, static _ => false);

        /// <summary>The exception every function of the columns throws for a value not in <see cref="All"/>.</summary>
        public static ArgumentOutOfRangeException Unknown(ConsoleColumn column) =>
            new ArgumentOutOfRangeException(nameof(column), $"{((int)column).ToString(CultureInfo.InvariantCulture)} is not a column of the console's rows.");
    }

    /// <summary>How a column is sized: it fills the room the others leave, or it has a width within bounds.</summary>
    internal abstract record ColumnFit
    {
        private ColumnFit()
        {
        }

        /// <summary>Does what <paramref name="fills"/> or <paramref name="sized"/> does with the case it is.</summary>
        public abstract R Match<R>(Func<Fills, R> fills, Func<Sized, R> sized);

        /// <summary>The message's fit: as wide as the room the others leave, at least <see cref="Least"/>; always shown, and keeping no width.</summary>
        public sealed record Fills : ColumnFit
        {
            /// <summary>A fit that fills, no narrower than <paramref name="least"/> pixels.</summary>
            /// <exception cref="ArgumentOutOfRangeException"><paramref name="least"/> is under 1.</exception>
            internal Fills(int least)
            {
                if (least < 1)
                    throw new ArgumentOutOfRangeException(nameof(least), $"A column is 1 pixel wide at least, and {least.ToString(CultureInfo.InvariantCulture)} was given.");

                Least = least;
            }

            /// <summary>The fewest pixels it is wide.</summary>
            public int Least { get; }

            /// <inheritdoc />
            public override R Match<R>(Func<Fills, R> fills, Func<Sized, R> sized) => fills(this);
        }

        /// <summary>A fit of a width a developer sets, from <see cref="Least"/> to <see cref="Most"/> pixels.</summary>
        public sealed record Sized : ColumnFit
        {
            /// <summary>A fit from <paramref name="least"/> to <paramref name="most"/> pixels, <paramref name="default"/> at first, and shown at first or not.</summary>
            /// <exception cref="ArgumentOutOfRangeException">Unless 1 &lt;= <paramref name="least"/> &lt;= <paramref name="default"/> &lt;= <paramref name="most"/>.</exception>
            internal Sized(int least, int @default, int most, bool shownByDefault)
            {
                if (least < 1 || least > @default || @default > most)
                    throw new ArgumentOutOfRangeException(least < 1 || least > @default ? nameof(least) : nameof(most),
                        $"A column's widths are 1 <= least <= at first <= most, and {least.ToString(CultureInfo.InvariantCulture)}, "
                        + $"{@default.ToString(CultureInfo.InvariantCulture)} and {most.ToString(CultureInfo.InvariantCulture)} were given.");

                Least = least;
                Default = @default;
                Most = most;
                ShownByDefault = shownByDefault;
            }

            /// <summary>The fewest pixels it is wide.</summary>
            public int Least { get; }

            /// <summary>How many pixels wide it is at first.</summary>
            public int Default { get; }

            /// <summary>The most pixels it is wide.</summary>
            public int Most { get; }

            /// <summary>Whether it is shown at first.</summary>
            public bool ShownByDefault { get; }

            /// <summary>
            /// <paramref name="width"/>, as a drag left it, held within <see cref="Least"/> and <see cref="Most"/>
            /// and rounded to a whole pixel: none for a width that is not a finite number above 0, which
            /// no drag makes, so it asks nothing.
            /// </summary>
            public Optional<int> Held(float width)
            {
                if (float.IsNaN(width) || float.IsInfinity(width) || width <= 0f)
                    return F.None;

                // Held within the bounds first, so the rounding never sees a number past an int.
                double within = Math.Min(Most, Math.Max(Least, (double)width));
                return Hold((int)Math.Round(within, MidpointRounding.AwayFromZero));
            }

            /// <summary><paramref name="width"/> held within <see cref="Least"/> and <see cref="Most"/>.</summary>
            public int Hold(int width) => Math.Min(Most, Math.Max(Least, width));

            /// <inheritdoc />
            public override R Match<R>(Func<Fills, R> fills, Func<Sized, R> sized) => sized(this);
        }
    }

    /// <summary>A sized column as a developer keeps it: whether it is shown, and how wide, within its bounds.</summary>
    internal sealed record ColumnShown
    {
        private ColumnShown(ConsoleColumn column, bool shown, int width)
        {
            Column = column;
            Shown = shown;
            Width = width;
        }

        /// <summary>The column.</summary>
        public ConsoleColumn Column { get; }

        /// <summary>Whether it is shown.</summary>
        public bool Shown { get; }

        /// <summary>How many pixels wide it is, kept while it is hidden.</summary>
        public int Width { get; }

        /// <summary><paramref name="column"/>, shown or not, <paramref name="width"/> pixels wide held within its bounds.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="column"/> is the one that fills, which keeps no width, or not a column.</exception>
        public static ColumnShown Of(ConsoleColumn column, bool shown, int width) =>
            ConsoleColumns.Fit(column).Match<ColumnShown>(
                _ => throw new ArgumentOutOfRangeException(nameof(column), $"The {ConsoleColumns.Title(column)} column fills the room the others leave, and keeps no width."),
                sized => new ColumnShown(column, shown, sized.Hold(width)));
    }

    /// <summary>What the developer asked of one column in the header: to show or hide it, or to make it as wide.</summary>
    internal abstract record ColumnAsked
    {
        private ColumnAsked()
        {
        }

        /// <summary>Does what <paramref name="showing"/> or <paramref name="sizing"/> does with the case it is.</summary>
        public abstract R Match<R>(Func<Showing, R> showing, Func<Sizing, R> sizing);

        /// <summary>The column shown, or hidden.</summary>
        /// <param name="Column">The column.</param>
        /// <param name="Shown">Whether it is shown.</param>
        public sealed record Showing(ConsoleColumn Column, bool Shown) : ColumnAsked
        {
            /// <inheritdoc />
            public override R Match<R>(Func<Showing, R> showing, Func<Sizing, R> sizing) => showing(this);
        }

        /// <summary>The column made as wide as a drag, or "Resize To Fit", left it.</summary>
        /// <param name="Column">The column.</param>
        /// <param name="Width">Its width in pixels, as Unity has it.</param>
        public sealed record Sizing(ConsoleColumn Column, float Width) : ColumnAsked
        {
            /// <inheritdoc />
            public override R Match<R>(Func<Showing, R> showing, Func<Sizing, R> sizing) => sizing(this);
        }
    }

    /// <summary>
    /// The columns a developer shows, and how wide: every sized column once, in the order drawn, each
    /// within its bounds. It is made only from <see cref="Default"/>, by its own changes, so no
    /// column is missing or twice. Each function asks <see cref="ConsoleColumns.Fit"/> of the column
    /// first, so a value not one of the columns throws <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    internal sealed record ColumnLayout
    {
        private readonly ColumnShown[] _sized;

        private ColumnLayout(ColumnShown[] sized)
        {
            _sized = sized;
            Sized = Array.AsReadOnly(sized);
        }

        /// <summary>Each column as its fit has it at first: the event and the thread hidden.</summary>
        public static ColumnLayout Default { get; } = new ColumnLayout(Defaults());

        /// <summary>Every column but the one that fills, in the order drawn.</summary>
        public ReadOnlyCollection<ColumnShown> Sized { get; }

        /// <summary>Whether <paramref name="column"/> is shown: always, for the one that fills.</summary>
        public bool Shows(ConsoleColumn column)
        {
            int at = IndexOf(column);
            return at < 0 || _sized[at].Shown;
        }

        /// <summary>How many pixels wide <paramref name="column"/> is: none for the one that fills, which keeps no width.</summary>
        public Optional<int> Width(ConsoleColumn column)
        {
            int at = IndexOf(column);
            return at < 0 ? F.None : F.Some(_sized[at].Width);
        }

        /// <summary><paramref name="column"/> shown, or hidden: this one for the one that fills, always shown, and when it is so already.</summary>
        public ColumnLayout Showing(ConsoleColumn column, bool shown)
        {
            int at = IndexOf(column);
            if (at < 0 || _sized[at].Shown == shown)
                return this;

            return Replacing(at, ColumnShown.Of(column, shown, _sized[at].Width));
        }

        /// <summary>
        /// <paramref name="column"/> as wide as <paramref name="width"/>, rounded and held within its
        /// bounds: this one for the one that fills, for a width that is not a finite number above 0,
        /// and when it is so already.
        /// </summary>
        public ColumnLayout Sizing(ConsoleColumn column, float width)
        {
            int at = IndexOf(column);
            if (at < 0)
                return this;

            Optional<int> held = ConsoleColumns.Fit(column).Match<Optional<int>>(static _ => F.None, sized => sized.Held(width));
            if (!held.HasValue(out int pixels) || _sized[at].Width == pixels)
                return this;

            return Replacing(at, ColumnShown.Of(column, _sized[at].Shown, pixels));
        }

        /// <summary>What <paramref name="asked"/> asks made of this one: this one when it changes nothing.</summary>
        public ColumnLayout Taking(ColumnAsked asked)
        {
            if (asked is null)
                throw new ArgumentNullException(nameof(asked));

            return asked.Match(showing => Showing(showing.Column, showing.Shown), sizing => Sizing(sizing.Column, sizing.Width));
        }

        /// <summary>Each of <paramref name="asked"/> made in turn: this one when none changes it.</summary>
        public ColumnLayout Taking(IReadOnlyList<ColumnAsked> asked)
        {
            if (asked is null)
                throw new ArgumentNullException(nameof(asked));

            ColumnLayout layout = this;
            for (int i = 0; i < asked.Count; i++)
                layout = layout.Taking(asked[i]);
            return layout;
        }

        /// <summary>Equal when each column is shown and as wide alike.</summary>
        public bool Equals(ColumnLayout? other)
        {
            if (other is null)
                return false;
            if (ReferenceEquals(this, other))
                return true;

            for (int i = 0; i < _sized.Length; i++)
            {
                if (!_sized[i].Equals(other._sized[i]))
                    return false;
            }

            return true;
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (ColumnShown shown in _sized)
                hash.Add(shown);
            return hash.ToHashCode();
        }

        // What ToString says of it, as a test that fails prints it: each column, shown or hidden, and how wide.
        private bool PrintMembers(StringBuilder builder)
        {
            for (int i = 0; i < _sized.Length; i++)
            {
                ColumnShown shown = _sized[i];
                if (i > 0)
                    builder.Append(", ");
                builder.Append(ConsoleColumns.Name(shown.Column)).Append(shown.Shown ? " shown " : " hidden ").Append(shown.Width.ToString(CultureInfo.InvariantCulture));
            }

            return true;
        }

        private static ColumnShown[] Defaults()
        {
            var sized = new List<ColumnShown>(ConsoleColumns.All.Count);
            foreach (ConsoleColumn column in ConsoleColumns.All)
            {
                Optional<ColumnShown> shown = ConsoleColumns.Fit(column).Match<Optional<ColumnShown>>(
                    static _ => F.None,
                    fit => ColumnShown.Of(column, fit.ShownByDefault, fit.Default));
                if (shown.HasValue(out ColumnShown first))
                    sized.Add(first);
            }

            return sized.ToArray();
        }

        /// <summary>Where <paramref name="column"/> is among the sized: -1 for the one that fills.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="column"/> is not a column.</exception>
        private int IndexOf(ConsoleColumn column)
        {
            if (ConsoleColumns.Fills(column))
                return -1;

            for (int i = 0; i < _sized.Length; i++)
            {
                if (_sized[i].Column == column)
                    return i;
            }

            throw new InvalidOperationException($"The {ConsoleColumns.Title(column)} column is missing from the layout, which holds every sized column.");
        }

        private ColumnLayout Replacing(int at, ColumnShown shown)
        {
            var sized = (ColumnShown[])_sized.Clone();
            sized[at] = shown;
            return new ColumnLayout(sized);
        }
    }
}
