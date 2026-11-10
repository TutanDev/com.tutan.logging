using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Tutan.Functional;
using Tutan.Logging.Editor;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace Tutan.Logging.Tests
{
    // The settings as values, and as the files hold them: no test reads or writes the project's own files.
    public class ConsoleSettingsTests
    {
        private static readonly ChosenColour s_red = new ChosenColour(255, 0, 0);
        private static readonly ChosenColour s_blue = new ChosenColour(0, 0, 255);

        [Test]
        public void A_filter_name_is_its_text_trimmed_of_one_to_32_characters_on_one_line()
        {
            Assert.That(Name("  Saves  ").Value, Is.EqualTo("Saves"));
            Assert.That(Name(new string('x', FilterName.MostLength)).Value.Length, Is.EqualTo(32));
            Assert.That(Code(FilterName.Of("   ")), Is.EqualTo(SettingsError.NameEmpty));
            Assert.That(Code(FilterName.Of(new string('x', 33))), Is.EqualTo(SettingsError.NameTooLong));
            Assert.That(Code(FilterName.Of("Saves\tAll")), Is.EqualTo(SettingsError.NameUnreadable));
            Assert.That(Code(FilterName.Of("Saves\nAll")), Is.EqualTo(SettingsError.NameUnreadable));
            Assert.Throws<ArgumentNullException>(() => FilterName.Of(null!));
        }

        [Test]
        public void Two_names_alike_but_for_case_are_one_name_to_a_team_and_two_spellings()
        {
            Assert.That(Name("Saves").SameAs(Name("SAVES")), Is.True);
            Assert.That(Name("Saves").SameAs(Name("Saving")), Is.False);
            Assert.That(Name("Saves"), Is.Not.EqualTo(Name("SAVES")), "A rename that changes only the case is a change.");
            Assert.That(Name("Saves"), Is.EqualTo(Name(" Saves")));
            Assert.That(Name("Saves").ToString(), Is.EqualTo("Saves"));
        }

        [TestCase("#3FA9F5")]
        [TestCase("3fa9f5")]
        [TestCase("  #3Fa9F5 ")]
        public void A_colour_is_six_hex_digits_after_a_hash_or_not(string text)
        {
            ChosenColour colour = Colour(text);

            Assert.That(colour, Is.EqualTo(new ChosenColour(0x3F, 0xA9, 0xF5)));
            Assert.That((colour.Hex, colour.ToString()), Is.EqualTo(("#3FA9F5", "#3FA9F5")));
        }

        [TestCase("")]
        [TestCase("#3FA9F")]
        [TestCase("#3FA9F50")]
        [TestCase("#GGA9F5")]
        [TestCase("##3FA9F5")]
        [TestCase("#3FA9F5FF")]
        [TestCase("#+FA9F5")]
        [TestCase("#3F A9F")]
        [TestCase("blue")]
        public void Text_that_is_not_six_hex_digits_is_no_colour(string text) =>
            Assert.That(Code(ChosenColour.Parse(text)), Is.EqualTo(SettingsError.ColourUnreadable));

        [Test]
        public void A_colour_chosen_is_opaque_and_rounded_to_bytes_and_reads_back_as_itself()
        {
            Assert.That(ChosenColour.Of(new Color(1f, 0.25f, -1f, 0.2f)), Is.EqualTo(new ChosenColour(255, 64, 0)), "Its alpha is dropped, and each channel clamped.");
            Assert.That(ChosenColour.Of(new Color(2f, 0f, 0f)), Is.EqualTo(s_red));
            Assert.That(s_red.Colour, Is.EqualTo(new Color(1f, 0f, 0f, 1f)));
            foreach (ChosenColour colour in new[] { s_red, s_blue, new ChosenColour(1, 2, 3), new ChosenColour(0x3F, 0xA9, 0xF5), new ChosenColour(254, 128, 127) })
            {
                Assert.That(ChosenColour.Of(colour.Colour), Is.EqualTo(colour), "As Unity draws it, and back.");
                Assert.That(Colour(colour.Hex), Is.EqualTo(colour), "As a file holds it, and back.");
            }
        }

        [TestCase("game")]
        [TestCase("game.save")]
        [TestCase("a1.b2c.d")]
        [TestCase("")]
        [TestCase("Game")]
        [TestCase("game..save")]
        [TestCase(".game")]
        [TestCase("game.")]
        [TestCase("1game")]
        [TestCase("game.1x")]
        [TestCase("game save")]
        [TestCase("game-save")]
        [TestCase("game_save")]
        [TestCase("jeu.é")]
        public void A_category_reads_as_the_runtime_declares_one(string text) =>
            Assert.That(CategoryName.IsCategory(text), Is.EqualTo(LogNames.IsCategory(text)), "The editor's copy holds what the runtime does.");

        [Test]
        public void A_category_name_is_its_text_trimmed_and_holds_itself_and_the_categories_under_it()
        {
            CategoryName game = Category(" game ");

            Assert.That(game.Value, Is.EqualTo("game"));
            Assert.That(Code(CategoryName.Of("Game.Save")), Is.EqualTo(SettingsError.CategoryUnreadable));
            Assert.That(new[] { "game", "game.save", "game.save.slots", "gamer", "games.save", "gam", "other.game" }.Select(game.Holds),
                Is.EqualTo(new[] { true, true, true, false, false, false, false }));
        }

        [Test]
        public void A_category_is_drawn_in_the_colour_of_the_nearest_category_it_is_under_that_has_one()
        {
            CategoryColours colours = CategoryColours.None.Choosing(Category("game.save"), s_blue).Choosing(Category("game"), s_red);

            Assert.That(colours.Chosen.Select(static chosen => chosen.Category.Value), Is.EqualTo(new[] { "game", "game.save" }), "In order of the category, as a file writes them.");
            Assert.That(colours.Of("game"), Is.EqualTo(F.Some(s_red)));
            Assert.That(colours.Of("game.ui.menu"), Is.EqualTo(F.Some(s_red)));
            Assert.That(colours.Of("game.save"), Is.EqualTo(F.Some(s_blue)));
            Assert.That(colours.Of("game.save.slots"), Is.EqualTo(F.Some(s_blue)), "The nearer category's colour.");
            Assert.That(colours.Of("gamer").IsNone, Is.True);
            Assert.That(colours.Of("other").IsNone, Is.True);
            Assert.That((colours.IsChosen(Category("game.save")), colours.IsChosen(Category("game.save.slots"))), Is.EqualTo((true, false)),
                "A category under one with a colour has none of its own.");

            void Draw() => _ = colours.Of("game.save.slots");
            Draw();
            Assert.That(Draw, Is.Not.AllocatingGCMemory(), "A row is drawn with it.");
        }

        [Test]
        public void A_colour_chosen_again_takes_the_place_of_the_one_before_and_one_unchosen_goes()
        {
            CategoryColours colours = CategoryColours.None.Choosing(Category("game"), s_red).Choosing(Category("game.save"), s_blue);

            Assert.That(colours.Choosing(Category("game"), s_red), Is.SameAs(colours), "As it was, it is this one.");
            Assert.That(colours.Choosing(Category("game"), s_blue).Of("game.ui"), Is.EqualTo(F.Some(s_blue)));
            Assert.That(colours.Choosing(Category("game"), s_blue).Chosen.Count, Is.EqualTo(2));
            Assert.That(colours.Unchoosing(Category("other")), Is.SameAs(colours));

            CategoryColours without = colours.Unchoosing(Category("game"));
            Assert.That((without.Of("game.ui").IsNone, without.Of("game.save.slots")), Is.EqualTo((true, F.Some(s_blue))));
            Assert.That(CategoryColours.None.Choosing(Category("game.save"), s_blue).Choosing(Category("game"), s_red), Is.EqualTo(colours),
                "Chosen alike, in any order, they are equal.");
            Assert.That(without.Choosing(Category("game"), s_red).GetHashCode(), Is.EqualTo(colours.GetHashCode()));
        }

        [Test]
        public void What_is_always_left_out_is_words_after_a_minus_only()
        {
            LeftOut always = Left("-unity/Logged   -\"Shader warning\"");

            Assert.That(always.Query, Is.EqualTo("-unity/Logged -\"Shader warning\""), "As its filter writes it.");
            Assert.That(always.Terms.Count, Is.EqualTo(2));
            Assert.That(Left("  "), Is.EqualTo(LeftOut.None));
            Assert.That(LeftOut.None.Query, Is.EqualTo(""));
            Assert.That(Code(LeftOut.Parse("level>=warning")), Is.EqualTo(SettingsError.LeftOutKeeps));
            Assert.That(Code(LeftOut.Parse("-unity/Logged tests.save")), Is.EqualTo(SettingsError.LeftOutKeeps));
            Assert.That(Code(LeftOut.Parse("frame:1..2")), Is.EqualTo(SettingsError.LeftOutKeeps));

            Result<LeftOut> unread = LeftOut.Parse("-level>=loud");
            Assert.That(Code(unread), Is.EqualTo(SettingsError.QueryUnreadable));
            Assert.That(unread.ErrorUnsafe().InnerErrors.Length, Is.EqualTo(1), "The filter's error is inside.");
            Assert.That(unread.ErrorUnsafe().Message, Does.StartWith("What is always left out does not read: "));
        }

        [Test]
        public void The_rows_leave_out_what_is_always_left_out_after_the_query_lines_own_words_each_once()
        {
            LeftOut always = Left("-unity/Logged -\"Shader warning\"");
            LogFilter shown = Parse("level>=warning -unity/Logged");

            LogFilter rows = always.From(shown);

            Assert.That(rows, Is.EqualTo(Parse("level>=warning -unity/Logged -\"Shader warning\"")));
            Assert.That(always.From(rows), Is.SameAs(rows), "It leaves out all of them already.");
            Assert.That(LeftOut.None.From(shown), Is.SameAs(shown), "Nothing is left out.");
            Assert.That(always.From(LogFilter.All), Is.EqualTo(Parse("-unity/Logged -\"Shader warning\"")));
        }

        [Test]
        public void A_saved_filter_is_a_name_and_a_query_that_reads()
        {
            SavedFilter errors = Saved("Errors", "tests.*  level>=error");

            Assert.That((errors.Name.Value, errors.Query, errors.Colour.IsNone), Is.EqualTo(("Errors", "level>=error tests.*", true)), "Its query as its filter writes it.");
            Assert.That(errors.IsOn(Parse("level>=error tests.*")), Is.True);
            Assert.That(errors.IsOn(Parse("level>=error")), Is.False);
            Assert.That(Code(SavedFilter.Of(" ", "level>=error", F.None)), Is.EqualTo(SettingsError.NameEmpty));

            Result<SavedFilter> unread = SavedFilter.Of("Loud", "level>=loud", F.None);
            Assert.That(Code(unread), Is.EqualTo(SettingsError.QueryUnreadable));
            Assert.That(unread.ErrorUnsafe().InnerErrors.Length, Is.EqualTo(1));
            Assert.That(unread.ErrorUnsafe().Message, Does.StartWith("The query of Loud does not read: "));
        }

        [Test]
        public void A_filter_is_saved_after_the_others_and_no_two_have_one_name()
        {
            SharedSettings shared = Ok(Ok(SharedSettings.None.Saving(Saved("Errors", "level>=error"))).Saving(Saved("Saves", "tests.save.*")));

            Assert.That(shared.Filters.Select(static filter => filter.Name.Value), Is.EqualTo(new[] { "Errors", "Saves" }));
            Assert.That(Code(shared.Saving(Saved("ERRORS", "level>=warning"))), Is.EqualTo(SettingsError.NameTaken), "Names that differ only in case are one name.");
            Assert.That(shared.Find(Name("saves")).Map(static filter => filter.Query), Is.EqualTo(F.Some("tests.save.*")));
            Assert.That(shared.Find(Name("Nothing")).IsNone, Is.True);
            Assert.Throws<NotSupportedException>(() => ((IList<SavedFilter>)shared.Filters).Add(Saved("More", "")), "They change only as the settings do.");
        }

        [Test]
        public void A_filter_edited_keeps_its_place_and_is_renamed_only_to_a_name_no_other_has()
        {
            SharedSettings shared = Shared("Errors", "Saves", "Warnings");

            SharedSettings edited = Ok(shared.Replacing(Name("Saves"), Saved("Slots", "tests.save.slots.*")));
            Assert.That(edited.Filters.Select(static filter => filter.Name.Value), Is.EqualTo(new[] { "Errors", "Slots", "Warnings" }));
            Assert.That(edited.Filters[1].Query, Is.EqualTo("tests.save.slots.*"));

            Assert.That(Code(shared.Replacing(Name("Saves"), Saved("warnings", "level>=warning"))), Is.EqualTo(SettingsError.NameTaken));
            Assert.That(Code(shared.Replacing(Name("Gone"), Saved("Gone", ""))), Is.EqualTo(SettingsError.NoSuchFilter));
            Assert.That(Ok(shared.Replacing(Name("Saves"), Saved("SAVES", "tests.*"))).Filters[1].Name.Value, Is.EqualTo("SAVES"),
                "Its own name in another case is its own.");
            Assert.That(Ok(shared.Replacing(Name("Saves"), shared.Filters[1])), Is.SameAs(shared), "As it was, it is this one.");
        }

        [Test]
        public void A_filter_is_removed_or_moved_among_the_others()
        {
            SharedSettings shared = Shared("Errors", "Saves", "Warnings");

            Assert.That(Names(shared.Removing(Name("saves"))), Is.EqualTo(new[] { "Errors", "Warnings" }));
            Assert.That(shared.Removing(Name("Gone")), Is.SameAs(shared));
            Assert.That(Names(shared.MovingBy(Name("warnings"), -1)), Is.EqualTo(new[] { "Errors", "Warnings", "Saves" }));
            Assert.That(Names(shared.MovingBy(Name("Warnings"), -2)), Is.EqualTo(new[] { "Warnings", "Errors", "Saves" }));
            Assert.That(Names(shared.MovingBy(Name("Errors"), 1)), Is.EqualTo(new[] { "Saves", "Errors", "Warnings" }));
            Assert.That(Names(shared.MovingBy(Name("Errors"), 10)), Is.EqualTo(new[] { "Saves", "Warnings", "Errors" }), "Kept among the others.");
            Assert.That(Names(shared.MovingBy(Name("Errors"), int.MaxValue)), Is.EqualTo(new[] { "Saves", "Warnings", "Errors" }), "No step wraps past the ends.");
            Assert.That(Names(shared.MovingBy(Name("Warnings"), int.MinValue)), Is.EqualTo(new[] { "Warnings", "Errors", "Saves" }));
            Assert.That(shared.MovingBy(Name("Errors"), -1), Is.SameAs(shared), "The first moves no further left.");
            Assert.That(shared.MovingBy(Name("Warnings"), 1), Is.SameAs(shared), "The last moves no further right.");
            Assert.That(shared.MovingBy(Name("Saves"), 0), Is.SameAs(shared));
            Assert.That(shared.MovingBy(Name("Gone"), 1), Is.SameAs(shared));

            // A teammate's order read again under the filters as drawn: a step left is a step left of where the file has it.
            SharedSettings reordered = Shared("Warnings", "Errors", "Saves");
            Assert.That(Names(reordered.MovingBy(Name("Saves"), -1)), Is.EqualTo(new[] { "Warnings", "Saves", "Errors" }));
        }

        [Test]
        public void Shared_settings_change_only_as_a_change_changes_them_and_are_equal_when_alike()
        {
            SharedSettings shared = Shared("Errors", "Saves");
            CategoryColours colours = CategoryColours.None.Choosing(Category("tests"), s_red);
            LeftOut always = Left("-unity/Logged");

            Assert.That(shared.Colouring(CategoryColours.None), Is.SameAs(shared));
            Assert.That(shared.LeavingOut(Left("")), Is.SameAs(shared));
            SharedSettings changed = shared.Colouring(colours).LeavingOut(always);
            Assert.That((changed.Colours, changed.LeftOut, changed.Filters), Is.EqualTo((colours, always, shared.Filters)));
            Assert.That(changed.Colouring(CategoryColours.None.Choosing(Category("tests"), s_red)), Is.SameAs(changed), "Equal colours are no change.");

            SharedSettings alike = Shared("Errors", "Saves").Colouring(colours).LeavingOut(Left("-unity/Logged"));
            Assert.That(alike, Is.EqualTo(changed));
            Assert.That(alike.GetHashCode(), Is.EqualTo(changed.GetHashCode()));
            Assert.That(Shared("Saves", "Errors").Colouring(colours).LeavingOut(always), Is.Not.EqualTo(changed), "The order of the filters is theirs.");
        }

        [Test]
        public void A_category_or_a_saved_filter_is_drawn_in_its_colour_or_its_names_hue()
        {
            CategoryColours colours = CategoryColours.None.Choosing(Category("tests"), s_red);

            Assert.That(Look.Category("tests.save", colours, dark: true), Is.EqualTo(s_red.Colour));
            Assert.That(Look.Category("other", colours, dark: true), Is.EqualTo(CategoryHue.Colour("other", dark: true)));
            Assert.That(Look.Saved(Saved("Saves", "tests.*"), dark: false), Is.EqualTo(CategoryHue.Colour("Saves", dark: false)));
            Assert.That(Look.Saved(Ok(SavedFilter.Of("Saves", "tests.*", s_blue)), dark: false), Is.EqualTo(s_blue.Colour), "One colour on both skins.");
        }

        [Test]
        public void Shared_settings_read_back_from_their_file_as_they_were_written()
        {
            SharedSettings shared = Ok(Ok(SharedSettings.None.Saving(Ok(SavedFilter.Of("Errors", "level>=error", s_blue)))).Saving(Saved("Saves", "tests.save.*")))
                .Colouring(CategoryColours.None.Choosing(Category("tests.save"), s_red).Choosing(Category("game"), s_blue))
                .LeavingOut(Left("-unity/Logged -\"Shader warning\""));

            string json = SharedSettingsFile.FromDomain(shared).ToJson();
            SharedSettingsFile file = JsonUtility.FromJson<SharedSettingsFile>(json);

            Assert.That(SharedSettingsFile.Read(json), Is.EqualTo(new SettingsRead<SharedSettings>.Read(shared, SettingsText.NoneSkipped)));
            Assert.That(file.Version, Is.EqualTo(SharedSettingsFile.Current));
            Assert.That(file.Filters!.Select(static entry => (entry.Name, entry.Query, entry.Colour)),
                Is.EqualTo(new[] { ("Errors", "level>=error", "#0000FF"), ("Saves", "tests.save.*", "") }), "An empty colour is the name's hue.");
            Assert.That(file.Colours!.Select(static entry => entry.Category), Is.EqualTo(new[] { "game", "tests.save" }), "By category, so a change reads as a small diff.");
            Assert.That(file.LeftOut, Is.EqualTo("-unity/Logged -\"Shader warning\""));
            Assert.That(json, Does.Contain("\n"), "Indented, as it is committed.");
        }

        [Test]
        public void A_file_of_version_1_reads_as_it_was_written()
        {
            const string shared = "{ \"Version\": 1, \"Filters\": [ { \"Name\": \"Errors\", \"Query\": \"level>=error\", \"Colour\": \"#0000FF\" }, "
                + "{ \"Name\": \"Saves\", \"Query\": \"tests.save.*\", \"Colour\": \"\" } ], "
                + "\"Colours\": [ { \"Category\": \"game\", \"Colour\": \"#FF0000\" } ], \"LeftOut\": \"-unity/Logged\" }";
            SharedSettings written = Ok(Ok(SharedSettings.None.Saving(Ok(SavedFilter.Of("Errors", "level>=error", s_blue)))).Saving(Saved("Saves", "tests.save.*")))
                .Colouring(CategoryColours.None.Choosing(Category("game"), s_red))
                .LeavingOut(Left("-unity/Logged"));

            Assert.That(SharedSettingsFile.Read(shared), Is.EqualTo(new SettingsRead<SharedSettings>.Read(written, SettingsText.NoneSkipped)),
                "A file committed by version 1 reads as it did, whatever its DTO is named in code.");

            const string personal = "{ \"Version\": 1, \"ClearOnPlay\": false, \"PauseOnError\": true }";
            Assert.That(PersonalSettingsFile.Read(personal), Is.EqualTo(new SettingsRead<PersonalSettings>.Read(new PersonalSettings(false, true, ColumnLayout.Default), SettingsText.NoneSkipped)),
                "A file version 1 wrote, before the columns, reads with the columns as they are at first.");
            Assert.That(Unreadable(PersonalSettingsFile.Read("{}")), Does.StartWith("It is of version 0,"), "A file that says no version was written by no console.");
        }

        [Test]
        public void No_file_or_an_empty_one_is_nothing_saved_and_one_that_cannot_be_read_is_not_written_over()
        {
            var nothing = new SettingsRead<SharedSettings>.Read(SharedSettings.None, SettingsText.NoneSkipped);

            Assert.That(SharedSettingsFile.Read(F.None), Is.EqualTo(nothing));
            Assert.That(SharedSettingsFile.Read(" \r\n "), Is.EqualTo(nothing));
            Assert.That(Unreadable(SharedSettingsFile.Read("{ \"Version\": 1, ")), Does.StartWith("It is not JSON: "));
            Assert.That(Unreadable(SharedSettingsFile.Read("{ \"Version\": 2 }")), Does.StartWith("It is of version 2, which this package does not know"));
            Assert.That(Unreadable(SharedSettingsFile.Read("{}")), Does.StartWith("It is of version 0,"), "A file that says no version was written by no console.");
            Assert.That(SharedSettingsFile.Read("{ \"Version\": 2 }").SettingsOr(SharedSettings.None), Is.SameAs(SharedSettings.None));
            Assert.That(SharedSettingsFile.Read("{ \"Version\": 1 }"), Is.EqualTo(nothing), "A file of version 1 that holds nothing saves nothing.");
        }

        [Test]
        public void An_entry_of_the_shared_file_that_does_not_read_is_skipped_and_said_and_the_rest_are_kept()
        {
            var file = new SharedSettingsFile
            {
                Version = 1,
                Filters = new[]
                {
                    Entry("Errors", "level>=error", ""),
                    Entry("", "level>=error", ""),
                    Entry("Loud", "level>=loud", ""),
                    Entry("Tinted", "tests.*", "blue"),
                    Entry("ERRORS", "level>=warning", ""),
                    null!,
                    new SavedFilterEntry(),
                    Entry("Saves", "tests.save.*", "#3FA9F5"),
                },
                Colours = new[]
                {
                    new CategoryColourEntry { Category = "tests", Colour = "#FF0000" },
                    new CategoryColourEntry { Category = "Tests.Save", Colour = "#00FF00" },
                    new CategoryColourEntry { Category = "tests.save", Colour = "green" },
                    new CategoryColourEntry { Category = "tests", Colour = "#0000FF" },
                    null!,
                },
                LeftOut = "level>=warning",
            };

            SettingsRead<SharedSettings>.Read read = file.ToDomain().Match(static read => read, static unreadable => throw new AssertionException(unreadable.Reason));

            Assert.That(Names(read.Settings), Is.EqualTo(new[] { "Errors", "Saves" }));
            Assert.That(read.Settings.Filters[1].Colour, Is.EqualTo(F.Some(new ChosenColour(0x3F, 0xA9, 0xF5))));
            Assert.That(read.Settings.Colours.Chosen, Is.EqualTo(new[] { new CategoryColour(Category("tests"), s_red) }), "The first colour of a category is kept.");
            Assert.That(read.Settings.LeftOut, Is.EqualTo(LeftOut.None));
            Assert.That(read.Skipped.Select(static skipped => skipped.Entry), Is.EqualTo(new[]
            {
                "Saved filter 2, \"\"",
                "Saved filter 3, \"Loud\"",
                "Saved filter 4, \"Tinted\"",
                "Saved filter 5, \"ERRORS\"",
                "Saved filter 6",
                "Saved filter 7, \"\"",
                "Category colour 2, \"Tests.Save\"",
                "Category colour 3, \"tests.save\"",
                "Category colour 4, \"tests\"",
                "Category colour 5",
                "What is always left out",
            }));
            Assert.That(read.Skipped.Select(static skipped => skipped.Reason), Has.All.Match(@"[.?!]$"), "Each reason is closed, as the settings page says them in a sentence.");
            Assert.That(read.Skipped[3].Reason, Does.StartWith("A saved filter is named ERRORS already"));
            Assert.That(read.Skipped[8].Reason, Is.EqualTo("A colour is chosen for tests already, by an entry before it."));
            Assert.That(SharedSettingsFile.FromDomain(read.Settings).Filters!.Length, Is.EqualTo(2), "The next change writes the file without them.");
        }

        [Test]
        public void Personal_settings_read_back_from_their_file_and_no_file_is_the_defaults()
        {
            var mine = new PersonalSettings(ClearOnPlay: false, PauseOnError: true,
                ColumnLayout.Default.Showing(ConsoleColumn.Thread, true).Showing(ConsoleColumn.Frame, false).Sizing(ConsoleColumn.CallSite, 240f));
            string json = PersonalSettingsFile.FromDomain(mine).ToJson();

            Assert.That(PersonalSettingsFile.Read(json), Is.EqualTo(new SettingsRead<PersonalSettings>.Read(mine, SettingsText.NoneSkipped)));
            Assert.That(JsonUtility.FromJson<PersonalSettingsFile>(json).Version, Is.EqualTo(PersonalSettingsFile.Current));
            Assert.That(PersonalSettingsFile.Read(F.None), Is.EqualTo(new SettingsRead<PersonalSettings>.Read(PersonalSettings.Default, SettingsText.NoneSkipped)));
            Assert.That(Unreadable(PersonalSettingsFile.Read("{ \"Version\": 3, \"ClearOnPlay\": false }")), Does.StartWith("It is of version 3,"));
            Assert.That(PersonalSettingsFile.Read("not json").SettingsOr(PersonalSettings.Default), Is.SameAs(PersonalSettings.Default));
        }

        [Test]
        public void The_personal_file_holds_each_column_but_the_message_by_name_in_the_order_drawn()
        {
            PersonalSettingsFile file = PersonalSettingsFile.FromDomain(PersonalSettings.Default with { Columns = ColumnLayout.Default.Sizing(ConsoleColumn.Time, 100f) });

            Assert.That(file.Columns!.Select(static entry => (entry.Column, entry.Shown, entry.Width)), Is.EqualTo(new[]
            {
                ("time", true, 100), ("frame", true, 52), ("level", true, 36), ("category", true, 140), ("event", false, 120),
                ("duration", true, 64), ("call-site", true, 160), ("thread", false, 48),
            }));

            const string written = "{ \"Version\": 2, \"ClearOnPlay\": true, \"PauseOnError\": false, \"Columns\": [ "
                + "{ \"Column\": \"event\", \"Shown\": true, \"Width\": 90 }, { \"Column\": \"time\", \"Shown\": false, \"Width\": 92 } ] }";
            Assert.That(PersonalSettingsFile.Read(written), Is.EqualTo(new SettingsRead<PersonalSettings>.Read(
                PersonalSettings.Default with { Columns = ColumnLayout.Default.Showing(ConsoleColumn.Event, true).Sizing(ConsoleColumn.Event, 90f).Showing(ConsoleColumn.Time, false) },
                SettingsText.NoneSkipped)), "A file of version 2 reads in any order, and a column it has no entry of is as it is at first.");
        }

        [Test]
        public void A_file_of_version_1_is_made_one_of_version_2_with_its_toggles_and_no_columns()
        {
            var first = new PersonalSettingsFile { Version = 1, ClearOnPlay = false, PauseOnError = true };
            PersonalSettingsFile second = first.Version2Of1();

            Assert.That((second.Version, second.ClearOnPlay, second.PauseOnError), Is.EqualTo((2, false, true)));
            Assert.That(second.Columns, Is.Empty, "Its columns are read as they are at first.");
            Assert.That(first.Version, Is.EqualTo(1), "The file read is left as it is.");
            Assert.Throws<InvalidOperationException>(() => second.Version2Of1(), "Each step is from one version to the next.");
        }

        [Test]
        public void A_column_entry_that_does_not_read_is_skipped_and_said_why()
        {
            var file = new PersonalSettingsFile
            {
                Version = 2,
                ClearOnPlay = true,
                Columns = new[]
                {
                    new ColumnEntry { Column = "time", Shown = false, Width = 1000 },
                    null!,
                    new ColumnEntry { Column = "Time", Shown = true, Width = 80 },
                    new ColumnEntry { Column = null, Shown = true, Width = 80 },
                    new ColumnEntry { Column = "message", Shown = false, Width = 300 },
                    new ColumnEntry { Column = "frame", Shown = false, Width = 0 },
                    new ColumnEntry { Column = "frame", Shown = true, Width = 40 },
                    new ColumnEntry { Column = "time", Shown = true, Width = 70 },
                    new ColumnEntry { Column = "thread", Shown = true, Width = 2 },
                },
            };

            SettingsRead<PersonalSettings>.Read read = file.ToDomain().Match(static read => read, static unreadable => throw new AssertionException(unreadable.Reason));

            Assert.That(read.Settings.Columns, Is.EqualTo(ColumnLayout.Default
                .Showing(ConsoleColumn.Time, false).Sizing(ConsoleColumn.Time, 160f)
                .Sizing(ConsoleColumn.Frame, 40f)
                .Showing(ConsoleColumn.Thread, true).Sizing(ConsoleColumn.Thread, 32f)),
                "A width is held within its column's bounds, and the first entry of a column read is kept.");
            Assert.That(read.Skipped.Select(static skipped => skipped.Entry), Is.EqualTo(new[]
            {
                "Column 2",
                "Column 3, \"Time\"",
                "Column 4, \"\"",
                "Column 5, \"message\"",
                "Column 6, \"frame\"",
                "Column 8, \"time\"",
            }));
            Assert.That(read.Skipped.Select(static skipped => skipped.Reason), Has.All.Match(@"[.?!]$"), "Each reason is closed, as the settings page says them in a sentence.");
            Assert.That(read.Skipped[3].Reason, Does.StartWith("The Message column is always shown"));
            Assert.That(read.Skipped[5].Reason, Is.EqualTo("An entry before it is of the Time column, and that one is read."));
            Assert.That(PersonalSettingsFile.FromDomain(read.Settings).Columns!.Length, Is.EqualTo(8), "The next change writes the file without them.");
        }

        [Test]
        public void A_change_is_written_only_when_it_changes_the_settings_and_never_over_a_file_that_cannot_be_read()
        {
            const string path = "ProjectSettings/TutanLogConsole.json";
            SharedSettings held = Shared("Errors");
            SettingsRead<SharedSettings> read = new SettingsRead<SharedSettings>.Read(held, SettingsText.NoneSkipped);

            Assert.That(Ok(read.Changing(static shared => shared.Saving(Saved("Saves", "tests.*")), path)).HasValue(out SharedSettings saved), Is.True, "A change of the settings is written.");
            Assert.That(Names(saved), Is.EqualTo(new[] { "Errors", "Saves" }));
            SharedSettings? given = null;
            Assert.That(Ok(read.Changing(shared => { given = shared; return shared; }, path)).IsNone, Is.True);
            Assert.That(given, Is.SameAs(held), "The change is made to the settings read.");
            Assert.That(Ok(read.Changing(static _ => Shared("Errors"), path)).IsNone, Is.True, "Settings equal to those read are not written.");
            Assert.That(Code(read.Changing(static shared => shared.Saving(Saved("ERRORS", "tests.*")), path)), Is.EqualTo(SettingsError.NameTaken), "A change refused is refused as it was.");

            SettingsRead<SharedSettings> unreadable = new SettingsRead<SharedSettings>.Unreadable("It is not JSON: Bad.");
            Result<Optional<SharedSettings>> refused = unreadable.Changing(static _ => throw new AssertionException("A file that cannot be read is not changed."), path);
            Assert.That(Code(refused), Is.EqualTo(SettingsError.Unwritable));
            Assert.That(refused.ErrorUnsafe().Message, Does.StartWith("ProjectSettings/TutanLogConsole.json cannot be read, so it is not written over: It is not JSON: Bad. "));

            Assert.Throws<ArgumentNullException>(() => read.Changing(null!, path));
            Assert.Throws<ArgumentNullException>(() => read.Changing(static shared => shared, null!));
        }

        [Test]
        public void Two_reads_alike_are_equal_so_a_file_read_again_unchanged_is_no_change()
        {
            SettingsRead<SharedSettings> once = new SettingsRead<SharedSettings>.Read(SharedSettings.None, Array.AsReadOnly(new[] { new SettingSkipped("Saved filter 1", "It is empty.") }));
            SettingsRead<SharedSettings> again = new SettingsRead<SharedSettings>.Read(SharedSettings.None, Array.AsReadOnly(new[] { new SettingSkipped("Saved filter 1", "It is empty.") }));

            Assert.That(once, Is.EqualTo(again));
            Assert.That(once.GetHashCode(), Is.EqualTo(again.GetHashCode()));
            Assert.That(once, Is.Not.EqualTo(new SettingsRead<SharedSettings>.Read(SharedSettings.None, SettingsText.NoneSkipped)));
            Assert.That(once, Is.Not.EqualTo(new SettingsRead<SharedSettings>.Unreadable("It is empty.")));
        }

        private static SavedFilterEntry Entry(string name, string query, string colour) => new SavedFilterEntry { Name = name, Query = query, Colour = colour };

        /// <summary>Saved filters of the names given, in order, each of the query <c>tests.*</c>.</summary>
        private static SharedSettings Shared(params string[] names) =>
            names.Aggregate(SharedSettings.None, static (shared, name) => Ok(shared.Saving(Saved(name, "tests.*"))));

        private static IEnumerable<string> Names(SharedSettings shared) => shared.Filters.Select(static filter => filter.Name.Value);

        private static FilterName Name(string text) => Ok(FilterName.Of(text));

        private static ChosenColour Colour(string text) => Ok(ChosenColour.Parse(text));

        private static CategoryName Category(string text) => Ok(CategoryName.Of(text));

        private static LeftOut Left(string text) => Ok(LeftOut.Parse(text));

        private static SavedFilter Saved(string name, string query) => Ok(SavedFilter.Of(name, query, F.None));

        private static LogFilter Parse(string query) => Ok(LogFilter.Parse(query));

        private static T Ok<T>(Result<T> result) => result.Match(static error => throw new InvalidOperationException(error.Message), static value => value);

        /// <summary>Why the change or the entry was refused.</summary>
        private static SettingsError Code<T>(Result<T> result) =>
            result.Match(static error => (SettingsError)error.Code, static _ => throw new InvalidOperationException("It was not refused."));

        /// <summary>Why the file cannot be read.</summary>
        private static string Unreadable<T>(SettingsRead<T> read) =>
            read.Match(static _ => throw new InvalidOperationException("The file read."), static unreadable => unreadable.Reason);
    }
}
