using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using NUnit.Framework;
using Tutan.Functional;

namespace Tutan.Logging.Tests
{
    public class LogFilterTests
    {
        [Test]
        public void A_query_reads_as_its_terms()
        {
            LogFilter filter = Parse("level>=warning  tests.*  -tests.save/Step  File:*.sav  frame:1200..1300  scope:4182  thread:7  \"no image\"");

            Assert.That(filter.Levels, Is.EqualTo(LogLevelRange.Between(LogLevel.Warning, LogLevel.Critical)));
            Assert.That(filter.Frames, Is.EqualTo(LogFrameRange.Between(1200, 1300)));
            Assert.That(filter.Keep, Is.EqualTo(new LogFilterTerm[]
            {
                new LogFilterTerm.Event("tests.*"),
                new LogFilterTerm.Field("File", "*.sav"),
                new LogFilterTerm.Scope(4182),
                new LogFilterTerm.Thread(7),
                new LogFilterTerm.Text("no image"),
            }));
            Assert.That(filter.LeaveOut, Is.EqualTo(new LogFilterTerm[] { new LogFilterTerm.Event("tests.save/Step") }));
        }

        [TestCase("level>=warn", LogLevel.Warning, LogLevel.Critical)]
        [TestCase("level>info", LogLevel.Warning, LogLevel.Critical)]
        [TestCase("level<=debug", LogLevel.Trace, LogLevel.Debug)]
        [TestCase("level<error", LogLevel.Trace, LogLevel.Warning)]
        [TestCase("level=error", LogLevel.Error, LogLevel.Error)]
        [TestCase("LEVEL:Information", LogLevel.Information, LogLevel.Information)]
        [TestCase("level>=debug level<=warning", LogLevel.Debug, LogLevel.Warning)]
        public void A_level_is_read_as_rules_name_it(string query, LogLevel least, LogLevel most)
        {
            Assert.That(Parse(query).Levels, Is.EqualTo(LogLevelRange.Between(least, most)));
        }

        [TestCase("level>critical")]
        [TestCase("level<trace")]
        [TestCase("level>=error level<=warning")]
        public void Levels_that_meet_nowhere_keep_none(string query)
        {
            LogFilter filter = Parse(query);

            Assert.That(filter.Levels.IsEmpty, Is.True);
            Assert.That(filter.Levels, Is.EqualTo(LogLevelRange.Empty));
            Assert.That(filter.ToString(), Is.EqualTo("level>critical"));
        }

        [TestCase("frame:1240", 1240, 1240)]
        [TestCase("frame:1200..", 1200, long.MaxValue)]
        [TestCase("frame:..1300", 0, 1300)]
        [TestCase("frame:1200..1300 frame:1250..", 1250, 1300)]
        public void Frames_are_a_range(string query, long first, long last)
        {
            Assert.That(Parse(query).Frames, Is.EqualTo(LogFrameRange.Between(first, last)));
        }

        [TestCase("")]
        [TestCase("level>=warning")]
        [TestCase("level=error")]
        [TestCase("level<=debug")]
        [TestCase("level>=debug level<=warning")]
        [TestCase("level>critical")]
        [TestCase("frame:5")]
        [TestCase("frame:5..")]
        [TestCase("frame:..9")]
        [TestCase("frame:5..9")]
        [TestCase("frame:9..5")]
        [TestCase("tests.* -tests.save/Step * tests game.save/Saved")]
        [TestCase("File:*.sav File:\"a b\" File:\"\" File:\"say \\\"hi\\\"\" Name:a\\b")]
        [TestCase("\"no image\" \"say \\\"hi\\\" \\\\ there\" -\"x\"")]
        [TestCase("scope:3 -scope:4 thread:2 -thread:1")]
        [TestCase("/no +image/ -/^Step/ File:/\\.sav$/ -@level:/x\\/y/ /a b\\/c/")]
        [TestCase("File:\"/saves/a.sav\" \"/x/\"")]
        public void A_filter_writes_a_query_that_reads_back_as_it(string query)
        {
            LogFilter filter = Parse(query);

            Assert.That(Parse(filter.ToString()), Is.EqualTo(filter));
            Assert.That(Parse(filter.ToString()).GetHashCode(), Is.EqualTo(filter.GetHashCode()));
        }

        [Test]
        public void A_filter_writes_the_fewest_words_that_say_it()
        {
            Assert.That(LogFilter.All.ToString(), Is.EqualTo(""));
            Assert.That(
                Parse("  tests.*  -tests.save/Step level>=warning level<=critical File:\"my file.sav\" frame:1200..1300 \"no \\\"image\\\"\" -scope:4 ").ToString(),
                Is.EqualTo("level>=warning frame:1200..1300 tests.* File:\"my file.sav\" \"no \\\"image\\\"\" -tests.save/Step -scope:4"));
        }

        [TestCase("sponza!", LogFilterError.NotATerm, "sponza!")]
        [TestCase("Tests.Save", LogFilterError.NotATerm, "Tests.Save")]
        [TestCase("-", LogFilterError.NotATerm, "-")]
        [TestCase("a\"b\"", LogFilterError.NotATerm, "a\"b\"")]
        [TestCase("\"a\"b", LogFilterError.NotATerm, "\"a\"b")]
        [TestCase("frame>3", LogFilterError.NotATerm, "frame>3")]
        [TestCase("tests.save:x", LogFilterError.NotATerm, "tests.save:x")]
        [TestCase("File:a\"b", LogFilterError.NotATerm, "File:a\"b")]
        [TestCase("level>=loud", LogFilterError.NotALevel, "level>=loud")]
        [TestCase("level>=off", LogFilterError.NotALevel, "level>=off")]
        [TestCase("frame:a..b", LogFilterError.NotAFrame, "frame:a..b")]
        [TestCase("frame:..", LogFilterError.NotAFrame, "frame:..")]
        [TestCase("frame:-1", LogFilterError.NotAFrame, "frame:-1")]
        [TestCase("scope:x", LogFilterError.NotAScope, "scope:x")]
        [TestCase("scope:0", LogFilterError.NotAScope, "scope:0")]
        [TestCase("thread:-1", LogFilterError.NotAThread, "thread:-1")]
        [TestCase("thread:99999999999", LogFilterError.NotAThread, "thread:99999999999")]
        [TestCase("File:", LogFilterError.NoValue, "File:")]
        [TestCase("@File:", LogFilterError.NoValue, "@File:")]
        [TestCase("level>= warning", LogFilterError.NotALevel, "level>=")]
        [TestCase("level:", LogFilterError.NotALevel, "level:")]
        [TestCase("frame:", LogFilterError.NotAFrame, "frame:")]
        [TestCase("scope:", LogFilterError.NotAScope, "scope:")]
        [TestCase("thread:", LogFilterError.NotAThread, "thread:")]
        [TestCase("File>=", LogFilterError.NotATerm, "File>=")]
        [TestCase("\"no image", LogFilterError.QuoteNotClosed, "\"no image")]
        [TestCase("a File:\"no image", LogFilterError.QuoteNotClosed, "File:\"no image")]
        [TestCase("-level>=warning", LogFilterError.CannotLeaveOut, "-level>=warning")]
        [TestCase("-frame:3", LogFilterError.CannotLeaveOut, "-frame:3")]
        [TestCase("\"\"", LogFilterError.NoText, "\"\"")]
        [TestCase("a -\"\"", LogFilterError.NoText, "-\"\"")]
        [TestCase("@level>=warning", LogFilterError.NotATerm, "@level>=warning")]
        [TestCase("@1st:a", LogFilterError.NotATerm, "@1st:a")]
        [TestCase("@:a", LogFilterError.NotATerm, "@:a")]
        [TestCase("/no image", LogFilterError.RegexNotClosed, "/no image")]
        [TestCase("a -File:/x", LogFilterError.RegexNotClosed, "-File:/x")]
        [TestCase("/a/b", LogFilterError.NotARegex, "/a/b")]
        [TestCase("//", LogFilterError.NotARegex, "//")]
        [TestCase("File://", LogFilterError.NotARegex, "File://")]
        [TestCase("/(/", LogFilterError.NotARegex, "/(/")]
        [TestCase("File:/[a/", LogFilterError.NotARegex, "File:/[a/")]
        [TestCase("level>=/warn/", LogFilterError.NotALevel, "level>=/warn/")]
        public void A_word_that_is_not_a_term_is_said(string query, LogFilterError why, string word)
        {
            Error error = Refused(query);

            Assert.That(error.Code, Is.EqualTo((int)why), error.Message);
            Assert.That(error.Message, Does.Contain(word));
        }

        [Test]
        public void A_word_that_is_not_a_term_suggests_quotes()
        {
            Error error = Refused("level>=warning no-image");

            Assert.That(error.Code, Is.EqualTo((int)LogFilterError.NotATerm));
            Assert.That(error.Message, Does.EndWith("text to find in messages goes in quotes, \"no-image\""));
        }

        [Test]
        public void Quotes_with_nothing_between_them_say_they_find_everything()
        {
            Error error = Refused("level>=warning \"\"");

            Assert.That(error.Code, Is.EqualTo((int)LogFilterError.NoText));
            Assert.That(error.Message, Is.EqualTo("\"\" has nothing between its quotes, and every message holds that: put the text to find in them"));
        }

        [Test]
        public void A_term_refuses_what_it_cannot_match()
        {
            Assert.Throws<ArgumentException>(() => new LogFilterTerm.Event("Tests.Save"));
            Assert.Throws<ArgumentException>(() => new LogFilterTerm.Field("1st", "a"));
            Assert.Throws<ArgumentException>(() => new LogFilterTerm.Text(""));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LogFilterTerm.Scope(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LogFilterTerm.Thread(-1));
            Assert.Throws<ArgumentException>(() => new LogFilterTerm.TextRegex(""));
            Assert.Throws<ArgumentException>(() => new LogFilterTerm.TextRegex("(a"));
            Assert.Throws<ArgumentException>(() => new LogFilterTerm.FieldRegex("1st", "a"));
            Assert.Throws<ArgumentException>(() => new LogFilterTerm.FieldRegex("File", "[a"));
            Assert.That(new LogFilterTerm.Thread(0).Id, Is.EqualTo(0));
        }

        [Test]
        public void A_field_named_as_a_term_is_written_with_an_at()
        {
            LogFilter filter = Parse("@level:high @Thread:7 @File:*.sav -@scope:\"a b\"");

            Assert.That(filter.Keep, Is.EqualTo(new LogFilterTerm[]
            {
                new LogFilterTerm.Field("level", "high"), new LogFilterTerm.Field("Thread", "7"), new LogFilterTerm.Field("File", "*.sav"),
            }));
            Assert.That(filter.LeaveOut, Is.EqualTo(new LogFilterTerm[] { new LogFilterTerm.Field("scope", "a b") }));
            Assert.That(filter.Levels.IsAll, Is.True);
            Assert.That(filter.ToString(), Is.EqualTo("@level:high @Thread:7 File:*.sav -@scope:\"a b\""));
            Assert.That(Parse(filter.ToString()), Is.EqualTo(filter));
        }

        [Test]
        public void A_regular_expression_is_written_between_slashes_with_its_slashes_escaped()
        {
            LogFilter filter = Parse("/a\\/b c/ -File:/^\\d+$/ @frame:/x/");

            Assert.That(filter.Keep, Is.EqualTo(new LogFilterTerm[]
            {
                new LogFilterTerm.TextRegex("a/b c"), new LogFilterTerm.FieldRegex("frame", "x"),
            }));
            Assert.That(filter.LeaveOut, Is.EqualTo(new LogFilterTerm[] { new LogFilterTerm.FieldRegex("File", "^\\d+$") }));
            Assert.That(filter.ToString(), Is.EqualTo("/a\\/b c/ @frame:/x/ -File:/^\\d+$/"));
        }

        [Test]
        public void A_value_that_starts_with_a_slash_but_is_not_an_expression_goes_in_quotes()
        {
            LogFilter filter = Parse("File:\"/saves/a.sav\"");

            Assert.That(filter.Keep, Is.EqualTo(new LogFilterTerm[] { new LogFilterTerm.Field("File", "/saves/a.sav") }));
            Assert.That(filter.ToString(), Is.EqualTo("File:\"/saves/a.sav\""));
        }

        [Test]
        public void A_range_keeps_everything_until_it_is_narrowed()
        {
            Assert.That(default(LogLevelRange), Is.EqualTo(LogLevelRange.All));
            Assert.That(default(LogLevelRange).IsAll, Is.True);
            Assert.That(default(LogLevelRange).Least, Is.EqualTo(LogLevel.Trace));
            Assert.That(default(LogLevelRange).Most, Is.EqualTo(LogLevel.Critical));
            Assert.That(default(LogFrameRange), Is.EqualTo(LogFrameRange.All));
            Assert.That(default(LogFrameRange).IsAll, Is.True);
            Assert.That(default(LogFrameRange).First, Is.EqualTo(0L));
            Assert.That(default(LogFrameRange).Last, Is.EqualTo(long.MaxValue));
            Assert.That(new LogFilter(default, default, Array.Empty<LogFilterTerm>(), Array.Empty<LogFilterTerm>()), Is.EqualTo(LogFilter.All));
        }

        [Test]
        public void Ranges_that_keep_the_same_are_one_range()
        {
            Assert.That(LogLevelRange.Between(LogLevel.Error, LogLevel.Warning), Is.EqualTo(LogLevelRange.Empty));
            Assert.That(LogLevelRange.Only(LogLevel.None), Is.EqualTo(LogLevelRange.Empty));
            Assert.That(LogLevelRange.Between(LogLevel.Trace, LogLevel.None), Is.EqualTo(LogLevelRange.All));
            Assert.That(
                LogLevelRange.Between(LogLevel.Debug, LogLevel.Warning).Intersect(LogLevelRange.Between(LogLevel.Warning, LogLevel.Error)),
                Is.EqualTo(LogLevelRange.Only(LogLevel.Warning)));
            Assert.That(LogLevelRange.Only(LogLevel.Debug).Intersect(LogLevelRange.Only(LogLevel.Error)), Is.EqualTo(LogLevelRange.Empty));

            Assert.That(LogFrameRange.Between(9, 5), Is.EqualTo(LogFrameRange.Empty));
            Assert.That(LogFrameRange.Only(-1), Is.EqualTo(LogFrameRange.Empty));
            Assert.That(LogFrameRange.Between(-5, 9), Is.EqualTo(LogFrameRange.Between(0, 9)));
            Assert.That(LogFrameRange.Between(long.MinValue, long.MaxValue), Is.EqualTo(LogFrameRange.All));
            Assert.That(LogFrameRange.Only(3).Intersect(LogFrameRange.Only(4)), Is.EqualTo(LogFrameRange.Empty));
            Assert.That(LogFrameRange.Between(-5, 9).ToString(), Is.EqualTo("frame:..9"));
            Assert.That(LogFrameRange.Between(9, 5).ToString(), Is.EqualTo("frame:1..0"));
        }

        [Test]
        public void Events_are_alternatives_and_every_other_term_must_hold()
        {
            Assert.That(Kept("tests.save/Saved tests.save/Failed Slot:1", Saves),
                Is.EqualTo(new[] { "Saved slot 1 as B.dat", "Saving slot 1 failed" }));
        }

        [Test]
        public void A_term_left_out_must_not_hold()
        {
            Assert.That(Kept("tests.save -tests.save/Wrote -\"b.dat\"", Saves),
                Is.EqualTo(new[] { "Saved slot 3 as A.sav", "Saving slot 1 failed" }));
        }

        [TestCase("File:a.SAV", "Saved slot 3 as A.sav")]
        [TestCase("file:?.*", "Saved slot 3 as A.sav|Saved slot 1 as B.dat")]
        [TestCase("File:A", "")]
        [TestCase("Slot:3", "Saved slot 3 as A.sav")]
        [TestCase("Missing:*", "")]
        public void A_fields_pattern_matches_its_whole_value_ignoring_case(string query, string kept)
        {
            Assert.That(Kept(query + " tests.save/Saved", Saves), Is.EqualTo(kept.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)));
        }

        [Test]
        public void Text_is_found_in_the_message_ignoring_case()
        {
            Assert.That(Kept("\"SLOT 1 FAIL\"", Saves), Is.EqualTo(new[] { "Saving slot 1 failed" }));
        }

        [TestCase("/^SAVED SLOT \\d AS [ab]\\.sav$/", "Saved slot 3 as A.sav")]
        [TestCase("/BYTES$/", "Wrote 4 bytes")]
        [TestCase("-/saved/", "Wrote 4 bytes|Saving slot 1 failed")]
        [TestCase("File:/^[A-Z]\\.(SAV|dat)$/", "Saved slot 3 as A.sav|Saved slot 1 as B.dat")]
        [TestCase("Slot:/^1$/", "Saved slot 1 as B.dat|Saving slot 1 failed")]
        [TestCase("File:/sav/", "Saved slot 3 as A.sav")]
        [TestCase("Missing:/.*/", "")]
        public void A_regular_expression_finds_a_match_in_the_message_or_a_fields_value_ignoring_case(string query, string kept)
        {
            Assert.That(Kept(query, Saves), Is.EqualTo(kept.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)));
        }

        [Test]
        public void A_regular_expression_that_takes_too_long_over_a_record_does_not_match_it()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(1, new string('a', 40) + "!");
            store.Log.Saved(2, "aaa");
            LogFilter filter = Parse("File:/^(a+)+$/");
            var scopes = new LogScopes(store);
            var batch = new LogBatch();
            store.Read(store.First, batch);

            Assert.That(filter.Matches(batch[0], scopes), Is.False);
            Assert.That(filter.Matches(batch[1], scopes), Is.True, "A filter remembers nothing, so it tries the next record afresh.");
        }

        [Test]
        public void A_regular_expression_that_took_too_long_matches_nothing_for_the_rest_of_its_pass()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(1, new string('a', 40) + "!");
            store.Log.Saved(2, "aaa");
            var scopes = new LogScopes(store);
            var batch = new LogBatch();
            store.Read(store.First, batch);

            var keeping = new FilterPass(Parse("File:/^(a+)+$/"));
            Assert.That(keeping.Matches(batch[0], scopes), Is.False);
            Assert.That(keeping.GaveUp, Is.EqualTo(new[] { new LogFilterTerm.FieldRegex("File", "^(a+)+$") }));
            Assert.That(keeping.Matches(batch[1], scopes), Is.False, "Kept, it keeps nothing, rather than take as long over every record left.");

            var leaving = new FilterPass(Parse("-File:/^(a+)+$/"));
            Assert.That(leaving.Matches(batch[0], scopes), Is.True);
            Assert.That(leaving.GaveUp, Has.Count.EqualTo(1));
            Assert.That(leaving.Matches(batch[1], scopes), Is.True, "Left out, it leaves nothing out.");
        }

        [Test]
        public void Every_regular_expression_left_out_that_took_too_long_over_a_record_gives_up_at_it()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(1, new string('a', 40) + "!");
            var scopes = new LogScopes(store);
            var batch = new LogBatch();
            store.Read(store.First, batch);
            var pass = new FilterPass(Parse("-File:/^(a+)+$/ -File:/^(?:a+)+$/"));

            Assert.That(pass.Matches(batch[0], scopes), Is.True);
            Assert.That(pass.GaveUp, Is.EqualTo(new[]
            {
                new LogFilterTerm.FieldRegex("File", "^(a+)+$"),
                new LogFilterTerm.FieldRegex("File", "^(?:a+)+$"),
            }));
        }

        [Test]
        public void A_regular_expression_gives_up_from_the_record_it_took_too_long_over_and_a_copy_of_its_pass_does_too()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(1, "aaa");
            store.Log.Saved(2, new string('a', 40) + "!");
            store.Log.Saved(3, "aaa");
            var scopes = new LogScopes(store);
            var batch = new LogBatch();
            store.Read(store.First, batch);
            var pass = new FilterPass(Parse("File:/^(a+)+$/"));
            FilterPass before = pass.Copy();

            Assert.That(pass.Matches(batch[1], scopes), Is.False);
            FilterPass after = pass.Copy();

            Assert.That(pass.Matches(batch[0], scopes), Is.True, "A record before the one it took too long over, read after, is matched as it would have been.");
            Assert.That(pass.Matches(batch[2], scopes), Is.False);
            Assert.That(after.GaveUp, Is.EqualTo(pass.GaveUp));
            Assert.That(after.Matches(batch[2], scopes), Is.False, "A copy gives up where the pass did.");
            Assert.That((before.GaveUp.Count, before.Matches(batch[2], scopes)), Is.EqualTo((0, true)), "A copy made before goes on apart from it.");
        }

        [Test]
        public void An_escaped_slash_is_the_same_expression_as_a_slash()
        {
            var escaped = new LogFilterTerm.TextRegex(@"a\/b");
            var term = new LogFilterTerm.TextRegex("a/b");
            var backslash = new LogFilterTerm.TextRegex(@"a\\/b");

            Assert.That(escaped, Is.EqualTo(term));
            Assert.That(escaped.Pattern, Is.EqualTo("a/b"));
            Assert.That(Parse(escaped.ToString()).Keep, Is.EqualTo(new[] { term }));
            Assert.That(backslash.Pattern, Is.EqualTo(@"a\\/b"), "An escaped backslash before a slash is not an escaped slash.");
            Assert.That(Parse(backslash.ToString()).Keep, Is.EqualTo(new[] { backslash }));
        }

        [Test]
        public void Levels_and_frames_are_ranges()
        {
            Assert.That(Kept("level>=debug frame:2..", Saves), Is.EqualTo(new[] { "Wrote 4 bytes", "Saving slot 1 failed" }));
            Assert.That(Kept("frame:1", Saves), Is.EqualTo(new[] { "Saved slot 1 as B.dat" }));
        }

        [Test]
        public void A_scope_is_itself_and_everything_under_it_on_every_thread()
        {
            LogStore store = Stores.Everything();
            long outer;
            long inner;
            store.Log.Saved(0, "Before.sav");
            using (LogScope saving = store.Log.Saving(1))
            {
                outer = saving.Id;
                saving.Log.Saved(1, "A.sav");
                using LogScope nested = saving.Log.Saving(2);
                inner = nested.Id;
                Stores.OnAnotherThread(() => nested.Log.Step(5));
            }

            store.Log.Saved(9, "After.sav");

            Assert.That(Kept(store, $"scope:{outer}"), Is.EqualTo(new[]
            {
                "Saving slot 1", "Saved slot 1 as A.sav", "Saving slot 2", "Step 5", "Saving slot {Slot}", "Saving slot {Slot}",
            }));
            Assert.That(Kept(store, $"scope:{inner}"), Is.EqualTo(new[] { "Saving slot 2", "Step 5", "Saving slot {Slot}" }));
            Assert.That(Kept(store, $"-scope:{inner}"), Is.EqualTo(new[]
            {
                "Saved slot 0 as Before.sav", "Saving slot 1", "Saved slot 1 as A.sav", "Saving slot {Slot}", "Saved slot 9 as After.sav",
            }));
        }

        [Test]
        public void A_thread_is_what_it_wrote()
        {
            LogStore store = Stores.Everything();
            store.Log.Saved(3, "A.sav");
            Stores.OnAnotherThread(() => store.Log.Saved(1, "B.dat"));

            Assert.That(Kept(store, $"thread:{Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture)}"),
                Is.EqualTo(new[] { "Saved slot 3 as A.sav" }));
        }

        [Test]
        public void Matching_but_for_the_level_is_what_a_badge_counts()
        {
            LogStore store = Stores.Everything();
            Saves(store);
            LogFilter filter = Parse("level>=critical tests.save");
            var pass = new FilterPass(filter);
            var scopes = new LogScopes(store);
            var batch = new LogBatch();
            store.Read(store.First, batch);

            int matching = 0;
            int butForLevel = 0;
            for (int i = 0; i < batch.Count; i++)
            {
                matching += filter.Matches(batch[i], scopes) ? 1 : 0;
                butForLevel += pass.MatchesTerms(batch[i], scopes) ? 1 : 0;
            }

            Assert.That(matching, Is.EqualTo(0));
            Assert.That(butForLevel, Is.EqualTo(4));
        }

        [Test]
        public void A_term_no_event_is_or_has_is_said()
        {
            LogFilter filter = Parse("nowhere tests.save/Nope tests.* Slot:1 slot:2 Nope:2 Nope:/x/ -tests.save -nothing.here scope:1 thread:1 \"x\" /x/");
            LogEventSpec[] events = { SaveLog.Events.Saving, SaveLog.Events.Saved, SaveLog.Events.Wrote, SaveLog.Events.Step, SaveLog.Events.Failed };

            Assert.That(filter.MatchingNothing(events), Is.EqualTo(new LogFilterTerm[]
            {
                new LogFilterTerm.Event("nowhere"), new LogFilterTerm.Event("tests.save/Nope"), new LogFilterTerm.Field("Nope", "2"),
                new LogFilterTerm.FieldRegex("Nope", "x"), new LogFilterTerm.Event("nothing.here"),
            }));
        }

        [TestCase("*", "", true)]
        [TestCase("", "", true)]
        [TestCase("", "a", false)]
        [TestCase("**", "abc", true)]
        [TestCase("a*c", "abbc", true)]
        [TestCase("a*c", "ab", false)]
        [TestCase("a?c", "abc", true)]
        [TestCase("a?c", "ac", false)]
        [TestCase("*.sav", "Slot1.SAV", true)]
        [TestCase("a*b*c", "aXbYbZc", true)]
        [TestCase("*a*b", "xaybzb", true)]
        [TestCase("*a*b", "xaybzbc", false)]
        public void A_wildcard_matches_whole_text(string pattern, string text, bool matches)
        {
            Assert.That(Wildcard.Matches(pattern, text), Is.EqualTo(matches));
        }

        [Test]
        public void Filters_that_keep_the_same_are_equal()
        {
            Assert.That(Parse("level>=warning level<=critical"), Is.EqualTo(Parse("level>=warning")));
            Assert.That(Parse("level<trace frame:3..2"), Is.EqualTo(Parse("level>critical frame:1..0")));
            Assert.That(Parse("a"), Is.Not.EqualTo(Parse("a b")));
            Assert.That(Parse("/a/"), Is.Not.EqualTo(Parse("/A/")));
            Assert.That(Parse("  "), Is.EqualTo(LogFilter.All));
            Assert.That(LogFilter.All with { Keep = Array.Empty<LogFilterTerm>(), LeaveOut = new List<LogFilterTerm>() }, Is.EqualTo(LogFilter.All));
        }

        /// <summary>Writes the records the matching tests read: frame 0, then 1, then 2.</summary>
        private static void Saves(LogStore store)
        {
            store.Log.Saved(3, "A.sav");
            store.EnterFrame(1);
            store.Log.Saved(1, "B.dat");
            store.EnterFrame(2);
            store.Log.Wrote(4);
            store.Log.Failed(1, new IOException("A.sav is locked."));
        }

        private static LogFilter Parse(string query) =>
            LogFilter.Parse(query).Match(error => throw new InvalidOperationException(error.Message), filter => filter);

        private static Error Refused(string query) =>
            LogFilter.Parse(query).Match(error => error, filter => throw new InvalidOperationException($"It read as {filter}."));

        private static string[] Kept(string query, Action<LogStore> write)
        {
            LogStore store = Stores.Everything();
            write(store);
            return Kept(store, query);
        }

        /// <summary>The messages of the records the store holds that <paramref name="query"/> keeps; a scope's closing says its template.</summary>
        private static string[] Kept(LogStore store, string query)
        {
            LogFilter filter = Parse(query);
            var scopes = new LogScopes(store);
            var batch = new LogBatch();
            store.Read(store.First, batch);
            var kept = new List<string>();
            for (int i = 0; i < batch.Count; i++)
            {
                LogRecord record = batch[i];
                scopes.Learn(record);
                if (filter.Matches(record, scopes))
                    kept.Add(record.ToString());
            }

            return kept.ToArray();
        }
    }
}
