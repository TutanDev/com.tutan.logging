using System;
using System.Linq;
using NUnit.Framework;
using Tutan.Logging.Editor;

namespace Tutan.Logging.Tests
{
    public class CategoryTreeTests
    {
        private static readonly LogSource s_here = new LogSource("Tests", "Tests.Tree", "Tree.cs", 1);

        // Declared, not registered: the tree is of a session's events, whichever they are.
        private static readonly LogEventSpec[] s_events =
        {
            Spec("game.save", "Saved"),          // 0
            Spec("game.save", "Failed"),         // 1
            Spec("game", "Started"),             // 2
            Spec("game.input", "Pressed"),       // 3
            Spec("net", "Joined"),               // 4
            Spec("game.save.cloud", "Uploaded"), // 5
            Spec("audio.music", "Played"),       // 6
            Spec("game.saves", "Listed"),        // 7
        };

        [Test]
        public void A_tree_has_each_category_and_those_above_one_each_before_those_under_it()
        {
            CategoryTree tree = CategoryTree.Of(s_events);

            Assert.That(tree.Nodes.Select(node => (node.Category, node.Label, node.Depth)), Is.EqualTo(new[]
            {
                ("audio", "audio", 0), ("audio.music", "music", 1), ("game", "game", 0), ("game.input", "input", 1),
                ("game.save", "save", 1), ("game.save.cloud", "cloud", 2), ("game.saves", "saves", 1), ("net", "net", 0),
            }));
            Assert.That(tree.Events, Is.EqualTo(8));
            Assert.That(Node(tree, "audio").Own, Is.Empty, "A category no event is of, above one that has some.");
            Assert.That(Node(tree, "audio").Under, Is.EqualTo(new[] { 6 }));
            Assert.That(Node(tree, "game").Own, Is.EqualTo(new[] { 2 }));
            Assert.That(Node(tree, "game").Under, Is.EqualTo(new[] { 0, 1, 2, 3, 5, 7 }));
            Assert.That(Node(tree, "game.save").Own, Is.EqualTo(new[] { 0, 1 }));
            Assert.That(Node(tree, "game.save").Under, Is.EqualTo(new[] { 0, 1, 5 }), "Not game.saves, whose name only starts with it.");
        }

        [Test]
        public void A_tree_of_no_events_has_no_categories()
        {
            Assert.That(CategoryTree.Of(Array.Empty<LogEventSpec>()).Nodes, Is.Empty);
            Assert.Throws<ArgumentNullException>(() => CategoryTree.Of(null!));
        }

        [Test]
        public void A_category_is_shown_all_some_or_none_as_the_filters_event_terms_say()
        {
            CategoryTree tree = CategoryTree.Of(s_events);
            LogFilter hidden = Parse("-game.save.*");
            LogFilter kept = Parse("net game.input level>=error \"disk\"");

            Assert.That(Shown(tree, hidden), Is.EqualTo(new[]
            {
                ("audio", CategoryShown.All), ("audio.music", CategoryShown.All), ("game", CategoryShown.Some), ("game.input", CategoryShown.All),
                ("game.save", CategoryShown.None), ("game.save.cloud", CategoryShown.None), ("game.saves", CategoryShown.All), ("net", CategoryShown.All),
            }));
            Assert.That(Shown(tree, kept), Is.EqualTo(new[]
            {
                ("audio", CategoryShown.None), ("audio.music", CategoryShown.None), ("game", CategoryShown.Some), ("game.input", CategoryShown.All),
                ("game.save", CategoryShown.None), ("game.save.cloud", CategoryShown.None), ("game.saves", CategoryShown.None), ("net", CategoryShown.All),
            }), "Its other terms do not say.");
        }

        [Test]
        public void A_press_on_a_category_shown_whole_hides_it_and_everything_under_it_and_again_shows_it()
        {
            CategoryTree tree = CategoryTree.Of(s_events);

            LogFilter hidden = tree.Press(LogFilter.All, Node(tree, "game.save"), CategoryPress.Toggle);
            LogFilter shown = tree.Press(hidden, Node(tree, "game.save"), CategoryPress.Toggle);

            Assert.That(hidden.ToString(), Is.EqualTo("-game.save.*"));
            Assert.That(shown, Is.EqualTo(LogFilter.All));
            Assert.That(tree.Press(LogFilter.All, Node(tree, "game"), CategoryPress.Toggle).ToString(), Is.EqualTo("-game.*"));
        }

        [Test]
        public void A_press_on_a_category_shown_in_part_shows_it_whole()
        {
            CategoryTree tree = CategoryTree.Of(s_events);

            LogFilter pressed = tree.Press(Parse("-game.save/Saved -net"), Node(tree, "game.save"), CategoryPress.Toggle);

            Assert.That(pressed.ToString(), Is.EqualTo("-net.*"));
        }

        [Test]
        public void The_events_hidden_are_written_by_category_where_they_are_all_of_one_and_by_key_where_not()
        {
            CategoryTree tree = CategoryTree.Of(s_events);

            LogFilter pressed = tree.Press(Parse("-game.save/Saved"), Node(tree, "net"), CategoryPress.Toggle);
            LogFilter own = tree.Press(Parse("-game.save/Saved -game.save/Failed"), Node(tree, "audio"), CategoryPress.Toggle);

            Assert.That(pressed.ToString(), Is.EqualTo("-game.save/Saved -net.*"));
            Assert.That(own.ToString(), Is.EqualTo("-audio.* -game.save"), "The category's own events, and none under it.");
        }

        [Test]
        public void The_events_shown_are_written_kept_when_that_is_fewer_terms_and_left_out_when_it_ties()
        {
            CategoryTree tree = CategoryTree.Of(s_events);
            CategoryTree two = CategoryTree.Of(new[] { Spec("left", "A"), Spec("right", "B") });

            LogFilter only = tree.Press(LogFilter.All, Node(tree, "net"), CategoryPress.Only);
            LogFilter tie = two.Press(LogFilter.All, Node(two, "right"), CategoryPress.Toggle);

            Assert.That(only.ToString(), Is.EqualTo("net.*"));
            Assert.That(tie.ToString(), Is.EqualTo("-right.*"));
        }

        [Test]
        public void Hiding_every_category_leaves_every_event_out()
        {
            CategoryTree tree = CategoryTree.Of(s_events);

            LogFilter none = tree.Press(Parse("net"), Node(tree, "net"), CategoryPress.Toggle);

            Assert.That(none.ToString(), Is.EqualTo("-*"));
            Assert.That(tree.Shown(Node(tree, "game"), none), Is.EqualTo(CategoryShown.None));
            Assert.That(tree.Press(none, Node(tree, "audio"), CategoryPress.Toggle).ToString(), Is.EqualTo("audio.*"));
        }

        [Test]
        public void Showing_only_a_category_and_again_shows_every_one_keeping_the_other_terms()
        {
            CategoryTree tree = CategoryTree.Of(s_events);
            LogFilter filter = Parse("level>=warning \"disk\" -thread:2 -net");

            LogFilter only = tree.Press(filter, Node(tree, "game.save"), CategoryPress.Only);
            LogFilter again = tree.Press(only, Node(tree, "game.save"), CategoryPress.Only);

            Assert.That(only.ToString(), Is.EqualTo("level>=warning game.save.* \"disk\" -thread:2"));
            Assert.That(again.ToString(), Is.EqualTo("level>=warning \"disk\" -thread:2"));
            Assert.That(tree.Press(only, Node(tree, "net"), CategoryPress.Only).ToString(), Is.EqualTo("level>=warning net.* \"disk\" -thread:2"),
                "Only another shows that one alone.");
        }

        [Test]
        public void Showing_every_category_drops_the_event_terms_alone()
        {
            CategoryTree tree = CategoryTree.Of(s_events);

            LogFilter every = tree.Press(Parse("game.* -game.save/Saved \"slot\" -\"slow\""), Node(tree, "net"), CategoryPress.Everything);

            Assert.That(every.ToString(), Is.EqualTo("\"slot\" -\"slow\""));
        }

        [Test]
        public void Every_filter_a_press_writes_reads_back_as_itself()
        {
            CategoryTree tree = CategoryTree.Of(s_events);
            LogFilter[] starts =
            {
                LogFilter.All, Parse("-game.save/Saved -net"), Parse("level>=warning game.* \"disk\" -thread:2"), Parse("-*"), Parse("net game.input"),
            };

            foreach (LogFilter start in starts)
            {
                foreach (CategoryNode node in tree.Nodes)
                {
                    foreach (CategoryPress press in new[] { CategoryPress.Toggle, CategoryPress.Only, CategoryPress.Everything })
                    {
                        LogFilter pressed = tree.Press(start, node, press);
                        Assert.That(Parse(pressed.ToString()), Is.EqualTo(pressed), $"{press} on {node.Category}, from \"{start}\".");
                    }
                }
            }
        }

        [Test]
        public void A_key_declared_twice_is_written_once()
        {
            CategoryTree tree = CategoryTree.Of(new[]
            {
                Spec("game.save", "Saved"), Spec("game.save", "Saved"), Spec("game.save", "Failed"), Spec("game.save", "Loaded"),
                Spec("net", "Joined"), Spec("audio", "Played"),
            });

            LogFilter pressed = tree.Press(Parse("-game.save/Saved"), Node(tree, "net"), CategoryPress.Toggle);

            Assert.That(pressed.ToString(), Is.EqualTo("-game.save/Saved -net.*"));
            Assert.That(Node(tree, "game.save").Own, Is.EqualTo(new[] { 0, 1, 2, 3 }), "Both its specs are the category's.");
        }

        [Test]
        public void A_category_of_another_tree_is_refused()
        {
            CategoryTree tree = CategoryTree.Of(s_events);
            CategoryTree other = CategoryTree.Of(new[] { Spec("game.save", "Saved") });

            Assert.Throws<ArgumentException>(() => tree.Shown(Node(other, "game.save"), LogFilter.All), "Of one name, with other events.");
            Assert.Throws<ArgumentException>(() => tree.Press(LogFilter.All, Node(CategoryTree.Of(new[] { Spec("zzz", "A") }), "zzz"), CategoryPress.Toggle));
            Assert.Throws<ArgumentOutOfRangeException>(() => tree.Press(LogFilter.All, Node(tree, "net"), (CategoryPress)9));
        }

        [Test]
        public void A_categorys_count_is_what_its_events_and_those_under_it_would_show()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(1, "A.sav");
            store.Log.Saved(2, "B.sav");
            store.Log.Wrote(5);
            store.Log.Heard(LogLevel.Warning, "loud");
            var view = new LogView(store, Parse("-tests.save.* \"slot\""));
            view.Update();
            CategoryTree tree = CategoryTree.Of(store.Events);

            Assert.That(view.Count, Is.EqualTo(0));
            Assert.That(tree.Count(Node(tree, "tests.save"), view), Is.EqualTo(2), "Hidden, and counted as if it were not: its text term keeps only the saves.");
            Assert.That(tree.Count(Node(tree, "tests.heard"), view), Is.EqualTo(0));
            Assert.That(tree.Count(Node(tree, "tests"), view), Is.EqualTo(2));
        }

        private static (string, CategoryShown)[] Shown(CategoryTree tree, LogFilter filter) =>
            tree.Nodes.Select(node => (node.Category, tree.Shown(node, filter))).ToArray();

        private static CategoryNode Node(CategoryTree tree, string category) => tree.Nodes.Single(node => node.Category == category);

        private static LogEventSpec Spec(string category, string name) =>
            LogEventSpec.Declare(category, name, LogEventKind.Event, LogLevel.Information, name, Array.Empty<LogField>(), 0, s_here);

        private static LogFilter Parse(string query) =>
            LogFilter.Parse(query).Match(error => throw new InvalidOperationException(error.Message), filter => filter);
    }
}
