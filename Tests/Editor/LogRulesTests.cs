using NUnit.Framework;
using Tutan.Functional;

namespace Tutan.Logging.Tests
{
    public class LogRulesTests
    {
        [TestCase("debug; tests.save=trace; tests.*=warning; tests.save/Saved=off; tests.limits/Hit=10/s")]
        [TestCase("information; *=error,5/s")]
        [TestCase("trace")]
        public void Rules_read_back_as_the_text_they_were_parsed_from(string text)
        {
            Result<LogRules> rules = LogRules.Parse(text);

            Assert.That(rules.IsSuccess, Is.True, rules.ToString());
            Assert.That(rules.ValueUnsafe().ToString(), Is.EqualTo(text));
            Assert.That(LogRules.Parse(rules.ValueUnsafe().ToString()).ValueUnsafe(), Is.EqualTo(rules.ValueUnsafe()));
        }

        [Test]
        public void Spacing_case_and_the_short_names_of_levels_are_read()
        {
            LogRules rules = LogRules.Parse("  Info ;tests.save = WARN , 3/s ; ").ValueUnsafe();

            Assert.That(rules.ToString(), Is.EqualTo("information; tests.save=warning,3/s"));
        }

        [Test]
        public void Text_with_no_bare_level_keeps_the_defaults_least()
        {
            LogRules rules = LogRules.Parse("tests.save=debug").ValueUnsafe();

            Assert.That(rules.Least, Is.EqualTo(LogRules.Default.Least));
        }

        [TestCase("loud", LogRulesError.NotALevel)]
        [TestCase("tests.save=loud", LogRulesError.NotALevel)]
        [TestCase("tests.save=debug,trace", LogRulesError.NotALevel)]
        [TestCase("Tests.Save=debug", LogRulesError.NotAPattern)]
        [TestCase("tests.*.save=debug", LogRulesError.NotAPattern)]
        [TestCase("tests.save/=debug", LogRulesError.NotAPattern)]
        [TestCase("tests.save=x/s", LogRulesError.NotALimit)]
        [TestCase("tests.save=-1/s", LogRulesError.NotALimit)]
        [TestCase("tests.save=1/s,2/s", LogRulesError.NotALimit)]
        [TestCase("debug; trace", LogRulesError.LeastTwice)]
        public void Malformed_text_fails_saying_why(string text, LogRulesError why)
        {
            Result<LogRules> rules = LogRules.Parse(text);

            Assert.That(rules.IsError, Is.True);
            Assert.That(rules.ErrorUnsafe().Code, Is.EqualTo((int)why), rules.ErrorUnsafe().Message);
        }

        [Test]
        public void The_most_specific_rule_says_an_events_level_a_key_before_a_category_before_a_prefix()
        {
            LogRules rules = LogRules.Parse("error; tests.save/Saved=trace; tests.save=warning; tests.*=debug; *=critical").ValueUnsafe();

            Assert.That(rules.LeastFor(SaveLog.Events.Saved), Is.EqualTo(LogLevel.Trace));
            Assert.That(rules.LeastFor(SaveLog.Events.Wrote), Is.EqualTo(LogLevel.Warning));
            Assert.That(rules.LeastFor(FieldLog.Events.Numbers), Is.EqualTo(LogLevel.Debug));
            Assert.That(rules.LeastFor(UnityLog.Events.Logged), Is.EqualTo(LogLevel.Critical));
        }

        [Test]
        public void Of_two_prefixes_the_longer_wins_and_of_two_alike_the_later()
        {
            LogRules rules = LogRules.Parse("tests.*=debug; *=error; tests.save.*=trace; tests.*=warning").ValueUnsafe();

            Assert.That(rules.LeastFor(FieldLog.Events.Numbers), Is.EqualTo(LogLevel.Warning));
            Assert.That(rules.LeastFor(SaveLog.Events.Saved), Is.EqualTo(LogLevel.Trace), "tests.save.* takes in tests.save itself.");
        }

        [Test]
        public void A_rule_that_does_not_match_an_event_says_nothing_of_it()
        {
            LogRules rules = LogRules.Parse("warning; tests.fields=trace,5/s").ValueUnsafe();

            Assert.That(rules.LeastFor(SaveLog.Events.Saved), Is.EqualTo(LogLevel.Warning));
            Assert.That(rules.PerSecondFor(SaveLog.Events.Saved), Is.EqualTo(0));
        }

        [Test]
        public void A_prefix_takes_in_only_whole_words()
        {
            LogRules rules = LogRules.Parse("error; tests.sa.*=trace").ValueUnsafe();

            Assert.That(rules.LeastFor(SaveLog.Events.Saved), Is.EqualTo(LogLevel.Error));
        }

        [Test]
        public void A_rule_with_only_a_limit_keeps_the_level_a_less_specific_rule_says()
        {
            LogRules rules = LogRules.Parse("error; tests.*=debug; tests.limits=5/s").ValueUnsafe();

            Assert.That(rules.LeastFor(LimitLog.Events.Hit), Is.EqualTo(LogLevel.Debug));
            Assert.That(rules.PerSecondFor(LimitLog.Events.Hit), Is.EqualTo(5));
            Assert.That(rules.PerSecondFor(LimitLog.Events.Struck), Is.EqualTo(5));
            Assert.That(rules.PerSecondFor(SaveLog.Events.Saved), Is.EqualTo(0));
        }

        [Test]
        public void With_no_rule_on_its_limit_an_event_keeps_its_own()
        {
            Assert.That(LogRules.Default.PerSecondFor(LimitLog.Events.Hit), Is.EqualTo(2));
        }

        [Test]
        public void The_rules_matching_nothing_are_found()
        {
            LogRules rules = LogRules.Parse("tests.save=debug; tests.nothing=debug; tests.save/Nothing=off; tests.*=trace").ValueUnsafe();

            var nothing = rules.MatchingNothing(new[] { SaveLog.Events.Saved, SaveLog.Events.Wrote });

            Assert.That(nothing, Has.Count.EqualTo(2));
            Assert.That(nothing[0].Pattern, Is.EqualTo("tests.nothing"));
            Assert.That(nothing[1].Pattern, Is.EqualTo("tests.save/Nothing"));
        }
    }
}
