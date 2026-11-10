using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Tutan.Functional;
using Tutan.Logging.Editor;

namespace Tutan.Logging.Tests
{
    public class ColumnsTests
    {
        private static readonly ConsoleColumn[] s_notColumns = { 0, (ConsoleColumn)10, (ConsoleColumn)(-1) };

        [Test]
        public void Every_value_of_the_enum_is_a_column_in_the_order_drawn()
        {
            Assert.That(ConsoleColumns.All, Is.EqualTo(Enum.GetValues(typeof(ConsoleColumn)).Cast<ConsoleColumn>()));
            Assert.That(ConsoleColumns.All.Select(ConsoleColumns.Title), Is.EqualTo(new[]
            {
                "Time", "Frame", "Level", "Category", "Event", "Message", "Duration", "Call Site", "Thread",
            }));
        }

        [Test]
        public void Each_column_has_a_class_of_its_own_a_name_in_the_file_of_its_own_and_one_fills()
        {
            Assert.That(ConsoleColumns.All.Select(ConsoleColumns.Name), Is.Unique.And.All.Match("^[a-z]+(-[a-z]+)*$"), "Each a class the stylesheet reads.");
            Assert.That(ConsoleColumns.All.Select(ColumnEntry.NameOf), Is.Unique);
            Assert.That(ConsoleColumns.All.Select(column => ColumnEntry.Named(ColumnEntry.NameOf(column))), Is.EqualTo(ConsoleColumns.All.Select(static column => F.Some(column))),
                "Each name read back is the column it was written of.");
            Assert.That(ConsoleColumns.All.Where(ConsoleColumns.Fills), Is.EqualTo(new[] { ConsoleColumn.Message }), "Exactly one fills the room the others leave.");
            Assert.That(ConsoleColumns.All.Select(column => ConsoleColumns.Fit(column).Match(static fills => fills.Least, static sized => sized.Least)), Has.All.GreaterThan(0));
        }

        [Test]
        public void A_name_no_column_has_is_none()
        {
            Assert.That(new[] { null, "", "Time", "call_site", "message " }.Select(static name => ColumnEntry.Named(name).IsNone), Has.All.True,
                "As spelt, so a file reads alike everywhere.");
            Assert.That(ColumnEntry.Named("call-site"), Is.EqualTo(F.Some(ConsoleColumn.CallSite)));
        }

        [Test]
        public void A_value_of_the_enum_not_a_column_is_refused_by_all_that_takes_a_column()
        {
            ColumnLayout layout = ColumnLayout.Default;
            foreach (ConsoleColumn column in s_notColumns)
            {
                Assert.Throws<ArgumentOutOfRangeException>(() => ConsoleColumns.Name(column));
                Assert.Throws<ArgumentOutOfRangeException>(() => ConsoleColumns.Title(column));
                Assert.Throws<ArgumentOutOfRangeException>(() => ConsoleColumns.Fit(column));
                Assert.Throws<ArgumentOutOfRangeException>(() => ConsoleColumns.Fills(column));
                Assert.Throws<ArgumentOutOfRangeException>(() => ColumnEntry.NameOf(column));
                Assert.Throws<ArgumentOutOfRangeException>(() => ColumnShown.Of(column, true, 50));
                Assert.Throws<ArgumentOutOfRangeException>(() => layout.Shows(column));
                Assert.Throws<ArgumentOutOfRangeException>(() => layout.Width(column));
                Assert.Throws<ArgumentOutOfRangeException>(() => layout.Showing(column, false));
                Assert.Throws<ArgumentOutOfRangeException>(() => layout.Sizing(column, 50f), "Not taken for the one that fills.");
            }
        }

        [Test]
        public void A_fits_bounds_are_in_order_from_one_pixel()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new ColumnFit.Fills(0));
            Assert.That(Assert.Throws<ArgumentOutOfRangeException>(() => new ColumnFit.Sized(0, 10, 20, shownByDefault: true))!.ParamName, Is.EqualTo("least"));
            Assert.That(Assert.Throws<ArgumentOutOfRangeException>(() => new ColumnFit.Sized(30, 10, 20, shownByDefault: true))!.ParamName, Is.EqualTo("least"));
            Assert.That(Assert.Throws<ArgumentOutOfRangeException>(() => new ColumnFit.Sized(10, 30, 20, shownByDefault: true))!.ParamName, Is.EqualTo("most"));
            Assert.That(new ColumnFit.Sized(10, 10, 10, shownByDefault: false), Is.EqualTo(new ColumnFit.Sized(10, 10, 10, shownByDefault: false)));
        }

        [TestCase(91.5f, 92)]
        [TestCase(92.49f, 92)]
        [TestCase(10f, 60)]
        [TestCase(1000f, 160)]
        [TestCase(1e30f, 160)]
        public void A_width_is_rounded_and_held_within_its_columns_bounds(float width, int held)
        {
            var time = (ColumnFit.Sized)ConsoleColumns.Fit(ConsoleColumn.Time);

            Assert.That(time.Held(width), Is.EqualTo(F.Some(held)));
        }

        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        [TestCase(float.NegativeInfinity)]
        [TestCase(0f)]
        [TestCase(-5f)]
        public void A_width_that_is_not_a_finite_number_above_0_asks_nothing(float width)
        {
            var time = (ColumnFit.Sized)ConsoleColumns.Fit(ConsoleColumn.Time);

            Assert.That(time.Held(width).IsNone, Is.True);
            Assert.That(ColumnLayout.Default.Sizing(ConsoleColumn.Time, width), Is.SameAs(ColumnLayout.Default));
        }

        [Test]
        public void At_first_the_event_and_the_thread_are_hidden_and_each_column_is_as_wide_as_its_fit_says()
        {
            ColumnLayout layout = ColumnLayout.Default;

            Assert.That(layout.Sized.Select(static shown => shown.Column), Is.EqualTo(ConsoleColumns.All.Where(static column => column != ConsoleColumn.Message)),
                "Every column but the one that fills, in the order drawn.");
            Assert.That(ConsoleColumns.All.Where(column => !layout.Shows(column)), Is.EqualTo(new[] { ConsoleColumn.Event, ConsoleColumn.Thread }));
            Assert.That(layout.Sized.Select(static shown => shown.Width), Is.EqualTo(new[] { 92, 52, 36, 140, 120, 64, 160, 48 }));
            Assert.That((layout.Shows(ConsoleColumn.Message), layout.Width(ConsoleColumn.Message).IsNone), Is.EqualTo((true, true)),
                "The one that fills is always shown, and keeps no width.");
        }

        [Test]
        public void A_change_that_changes_nothing_is_the_same_layout()
        {
            ColumnLayout layout = ColumnLayout.Default;

            Assert.That(layout.Showing(ConsoleColumn.Time, true), Is.SameAs(layout));
            Assert.That(layout.Sizing(ConsoleColumn.Time, 92.2f), Is.SameAs(layout), "Rounded, it is the width held.");
            Assert.That(layout.Sizing(ConsoleColumn.Time, 2000f).Sizing(ConsoleColumn.Time, 5000f).Width(ConsoleColumn.Time), Is.EqualTo(F.Some(160)));
            Assert.That(layout.Showing(ConsoleColumn.Message, false), Is.SameAs(layout), "The one that fills is never hidden.");
            Assert.That(layout.Sizing(ConsoleColumn.Message, 300f), Is.SameAs(layout), "The one that fills keeps no width.");
            Assert.That(layout.Taking(Array.Empty<ColumnAsked>()), Is.SameAs(layout));
        }

        [Test]
        public void A_layout_changes_a_column_at_a_time_and_is_equal_to_one_that_shows_the_same()
        {
            ColumnLayout widened = ColumnLayout.Default.Sizing(ConsoleColumn.Category, 200f);
            ColumnLayout hidden = widened.Showing(ConsoleColumn.Frame, false);

            Assert.That(widened.Width(ConsoleColumn.Category), Is.EqualTo(F.Some(200)));
            Assert.That(ColumnLayout.Default.Width(ConsoleColumn.Category), Is.EqualTo(F.Some(140)), "The layout it was made of is as it was.");
            Assert.That((hidden.Shows(ConsoleColumn.Frame), hidden.Width(ConsoleColumn.Frame)), Is.EqualTo((false, F.Some(52))), "A hidden column keeps its width.");

            ColumnLayout other = ColumnLayout.Default.Showing(ConsoleColumn.Frame, false).Sizing(ConsoleColumn.Category, 199.6f);
            Assert.That(other, Is.EqualTo(hidden).And.Not.SameAs(hidden), "Equal as each column is shown and as wide alike.");
            Assert.That(other.GetHashCode(), Is.EqualTo(hidden.GetHashCode()));
            Assert.That(hidden.Showing(ConsoleColumn.Frame, true), Is.Not.EqualTo(hidden));
            Assert.That(hidden.ToString(), Does.Contain("time shown 92, frame hidden 52, level shown 36, category shown 200, event hidden 120,"), "Said column by column, so a test that fails says how.");
        }

        [Test]
        public void What_the_header_asked_is_taken_in_turn()
        {
            var asked = new List<ColumnAsked>
            {
                new ColumnAsked.Sizing(ConsoleColumn.CallSite, 300f),
                new ColumnAsked.Showing(ConsoleColumn.Thread, true),
                new ColumnAsked.Sizing(ConsoleColumn.CallSite, 250f),
                new ColumnAsked.Sizing(ConsoleColumn.Message, 900f),
            };

            ColumnLayout taken = ColumnLayout.Default.Taking(asked);

            Assert.That(taken, Is.EqualTo(ColumnLayout.Default.Showing(ConsoleColumn.Thread, true).Sizing(ConsoleColumn.CallSite, 250f)), "The last of a column's widths stays.");
            Assert.That(ColumnLayout.Default.Taking(new ColumnAsked.Showing(ConsoleColumn.Event, true)).Shows(ConsoleColumn.Event), Is.True);
            Assert.Throws<ArgumentNullException>(() => ColumnLayout.Default.Taking((ColumnAsked)null!));
            Assert.Throws<ArgumentNullException>(() => ColumnLayout.Default.Taking((IReadOnlyList<ColumnAsked>)null!));
        }

        [Test]
        public void A_shown_column_is_held_within_its_bounds_and_the_one_that_fills_is_never_one()
        {
            Assert.That(ColumnShown.Of(ConsoleColumn.Time, true, 5).Width, Is.EqualTo(60));
            Assert.That(ColumnShown.Of(ConsoleColumn.Time, false, 500).Width, Is.EqualTo(160));
            Assert.Throws<ArgumentOutOfRangeException>(() => ColumnShown.Of(ConsoleColumn.Message, true, 200));
        }

        [Test]
        public void Personal_settings_need_a_layout()
        {
            Assert.Throws<ArgumentNullException>(() => _ = new PersonalSettings(true, false, null!));
            Assert.Throws<ArgumentNullException>(() => _ = PersonalSettings.Default with { Columns = null! }, "Nor by with.");
            Assert.That(PersonalSettings.Default.Columns, Is.SameAs(ColumnLayout.Default));
            Assert.That(PersonalSettings.Default with { PauseOnError = true }, Is.EqualTo(new PersonalSettings(true, true, ColumnLayout.Default.Sizing(ConsoleColumn.Time, 100f).Sizing(ConsoleColumn.Time, 92f))),
                "Equal by the layout each holds.");
        }
    }
}
