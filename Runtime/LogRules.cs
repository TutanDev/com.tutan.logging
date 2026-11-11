using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Tutan.Functional;

namespace Tutan.Logging
{
    /// <summary>
    /// What a store captures: a level every event is held to, the level from which its records carry
    /// their stack, and rules that change either, or limit how often an event is kept, for the events a
    /// pattern names. Rules are data, compiled with the catalog into one byte per event, which is all a
    /// call reads to decide it is off, and one more, which a call it is on reads to decide whether to
    /// capture its stack.
    /// </summary>
    public sealed class LogRules : IEquatable<LogRules>
    {
        /// <summary>The level from which records carry their stack where the rules do not say: <see cref="LogLevel.Error"/>.</summary>
        public const LogLevel DefaultStackLevel = LogLevel.Error;

        private readonly LogRule[] _rules;
        private readonly ReadOnlyCollection<LogRule> _ruleList;

        /// <summary>
        /// Rules holding every event to <paramref name="least"/>, its records carrying their stack from
        /// <see cref="DefaultStackLevel"/>, but where <paramref name="rules"/> say otherwise.
        /// </summary>
        /// <param name="least">The least level captured where no rule says otherwise.</param>
        /// <param name="rules">The rules. Where two patterns match an event, the more specific wins, and of two alike, the later.</param>
        public LogRules(LogLevel least, params LogRule[] rules)
            : this(least, DefaultStackLevel, rules)
        {
        }

        /// <summary>Rules holding every event to <paramref name="least"/>, its records carrying their stack from <paramref name="stackLevel"/>, but where <paramref name="rules"/> say otherwise.</summary>
        /// <param name="least">The least level captured where no rule says otherwise.</param>
        /// <param name="stackLevel">The level from which a record carries its stack where no rule says otherwise, <see cref="LogLevel.None"/> for none.</param>
        /// <param name="rules">The rules. Where two patterns match an event, the more specific wins, and of two alike, the later.</param>
        public LogRules(LogLevel least, LogLevel stackLevel, params LogRule[] rules)
        {
            if (rules is null)
                throw new ArgumentNullException(nameof(rules));
            if (least > LogLevel.None)
                throw new ArgumentOutOfRangeException(nameof(least), least, "A least level is trace to critical, or none for off.");
            if (stackLevel > LogLevel.None)
                throw new ArgumentOutOfRangeException(nameof(stackLevel), stackLevel, "A stack level is trace to critical, or none for none.");

            Least = least;
            StackLevel = stackLevel;
            _rules = (LogRule[])rules.Clone();
            foreach (LogRule rule in _rules)
            {
                if (rule is null)
                    throw new ArgumentNullException(nameof(rules), "One of the rules is null.");
                if (rule.Least.Map(static level => level > LogLevel.None).Or(false) || rule.StackLevel.Map(static level => level > LogLevel.None).Or(false))
                    throw new ArgumentOutOfRangeException(nameof(rules), $"The rule for {rule.Pattern} has a level that is none of trace to critical, or none.");
            }

            _ruleList = Array.AsReadOnly(_rules);
        }

        /// <summary>
        /// Debug and above, records from Error carrying their stack, and no rules: a store captures what
        /// a developer would want when something goes wrong, and the sinks, which write from Information,
        /// write it only as an error's context.
        /// </summary>
        public static LogRules Default { get; } = new LogRules(LogLevel.Debug);

        /// <summary>The least level captured where no rule says otherwise.</summary>
        public LogLevel Least { get; }

        /// <summary>The level from which a record carries its stack where no rule says otherwise, <see cref="LogLevel.None"/> for none.</summary>
        public LogLevel StackLevel { get; }

        /// <summary>The rules, in the order they were given.</summary>
        public IReadOnlyList<LogRule> Rules => _ruleList;

        /// <summary>
        /// Rules from their text: items separated by <c>;</c>, of which one may be a bare level, the
        /// least, one may be a bare <c>stack&gt;=level</c>, the level from which records carry their
        /// stack, and the others <c>pattern=what</c>, where what is a level, <c>off</c>, a limit such as
        /// <c>10/s</c>, a stack level, or more than one of them separated by commas.
        /// <code>
        /// debug; stack>=warning; game.save=trace, stack>=debug; ai.*=warning; game.input/Moved=off; physics/Contact=10/s
        /// </code>
        /// A pattern is <c>*</c>, a category (<c>game.save</c>), a category and everything under it
        /// (<c>game.*</c>), or a key (<c>game.save/Saved</c>). Text with no bare level keeps
        /// <see cref="Default"/>'s, and text with no bare stack level keeps <see cref="DefaultStackLevel"/>.
        /// <c>stack&gt;=off</c> captures no stack.
        /// </summary>
        public static Result<LogRules> Parse(string text)
        {
            if (text is null)
                throw new ArgumentNullException(nameof(text));

            LogLevel least = Default.Least;
            bool leastGiven = false;
            LogLevel stack = DefaultStackLevel;
            bool stackGiven = false;
            var rules = new List<LogRule>();

            foreach (string raw in text.Split(';'))
            {
                string item = raw.Trim();
                if (item.Length == 0)
                    continue;

                if (IsStack(item, out string stackName))
                {
                    if (!TryLevel(stackName, out LogLevel level))
                        return F.Fail<LogRules>(NotALevel(stackName));
                    if (stackGiven)
                        return F.Fail<LogRules>(F.Error($"'{item}' is a second bare stack level, and the rules have one", (int)LogRulesError.StackLevelTwice));

                    stack = level;
                    stackGiven = true;
                    continue;
                }

                int equals = item.IndexOf('=');
                if (equals < 0)
                {
                    if (!TryLevel(item, out LogLevel level))
                        return F.Fail<LogRules>(NotALevel(item));
                    if (leastGiven)
                        return F.Fail<LogRules>(F.Error($"'{item}' is a second bare level, and the rules have one least", (int)LogRulesError.LeastTwice));

                    least = level;
                    leastGiven = true;
                    continue;
                }

                string pattern = item.Substring(0, equals).Trim();
                if (!LogPattern.IsValid(pattern))
                {
                    return F.Fail<LogRules>(F.Error(
                        $"'{pattern}' is not a pattern, which is *, a category as game.save, a category and all under it as game.*, or a key as game.save/Saved",
                        (int)LogRulesError.NotAPattern));
                }

                Optional<LogLevel> ruleLeast = F.None;
                Optional<int> perSecond = F.None;
                Optional<LogLevel> ruleStack = F.None;
                foreach (string rawPart in item.Substring(equals + 1).Split(','))
                {
                    string part = rawPart.Trim();
                    if (IsStack(part, out string partStack))
                    {
                        if (!TryLevel(partStack, out LogLevel level))
                            return F.Fail<LogRules>(NotALevel(partStack));
                        if (ruleStack.IsSome)
                            return F.Fail<LogRules>(F.Error($"'{item}' has two stack levels, and a rule has one", (int)LogRulesError.StackLevelTwice));

                        ruleStack = level;
                    }
                    else if (part.EndsWith("/s", StringComparison.Ordinal))
                    {
                        if (perSecond.IsSome || !int.TryParse(part.Substring(0, part.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out int limit))
                            return F.Fail<LogRules>(F.Error($"'{item}' does not have one limit written as a whole number a second, as 10/s", (int)LogRulesError.NotALimit));
                        perSecond = limit;
                    }
                    else if (ruleLeast.IsNone && TryLevel(part, out LogLevel level))
                    {
                        ruleLeast = level;
                    }
                    else
                    {
                        return F.Fail<LogRules>(NotALevel(part));
                    }
                }

                rules.Add(new LogRule(pattern, ruleLeast, perSecond, ruleStack));
            }

            return F.Success(new LogRules(least, stack, rules.ToArray()));
        }

        /// <summary>The least level <paramref name="spec"/> is captured at, <see cref="LogLevel.None"/> when it is off.</summary>
        public LogLevel LeastFor(LogEventSpec spec) => MostSpecific(spec, Least, static rule => rule.Least);

        /// <summary>
        /// The level from which a record of <paramref name="spec"/> carries its stack, as the most
        /// specific rule that says one says, or <see cref="StackLevel"/>: <see cref="LogLevel.None"/> for none.
        /// </summary>
        public LogLevel StackLevelFor(LogEventSpec spec) => MostSpecific(spec, StackLevel, static rule => rule.StackLevel);

        /// <summary>How many records of <paramref name="spec"/> a second are kept, 0 for no limit: the most specific rule's limit, or the spec's own.</summary>
        public int PerSecondFor(LogEventSpec spec) => MostSpecific(spec, spec.PerSecond, static rule => rule.PerSecond);

        /// <summary>
        /// What the most specific rule matching <paramref name="spec"/> that says anything of it says,
        /// or <paramref name="otherwise"/>: of two rules alike, the later wins.
        /// </summary>
        private T MostSpecific<T>(LogEventSpec spec, T otherwise, Func<LogRule, Optional<T>> of)
        {
            T said = otherwise;
            int best = -1;
            foreach (LogRule rule in _rules)
            {
                int specificity = LogPattern.Specificity(rule.Pattern, spec);
                if (specificity >= 0 && specificity >= best && of(rule).HasValue(out T value))
                {
                    best = specificity;
                    said = value;
                }
            }

            return said;
        }

        /// <summary>The rules whose patterns match none of <paramref name="specs"/>, which is how a misspelt category is caught.</summary>
        public IReadOnlyList<LogRule> MatchingNothing(IReadOnlyList<LogEventSpec> specs)
        {
            var nothing = new List<LogRule>();
            foreach (LogRule rule in _rules)
            {
                bool matches = false;
                for (int i = 0; i < specs.Count && !matches; i++)
                    matches = LogPattern.Specificity(rule.Pattern, specs[i]) >= 0;

                if (!matches)
                    nothing.Add(rule);
            }

            return nothing;
        }

        /// <summary>The text <see cref="Parse"/> reads back as these rules.</summary>
        public override string ToString()
        {
            var text = new StringBuilder(Name(Least));
            if (StackLevel != DefaultStackLevel)
                text.Append("; ").Append(StackPart(StackLevel));
            foreach (LogRule rule in _rules)
                text.Append("; ").Append(rule);

            return text.ToString();
        }

        /// <summary>A level's name as rules spell it: <c>trace</c>, <c>debug</c>, <c>information</c>, <c>warning</c>, <c>error</c>, <c>critical</c>, or <c>off</c>.</summary>
        public static string Name(LogLevel level) => level switch
        {
            LogLevel.Trace => "trace",
            LogLevel.Debug => "debug",
            LogLevel.Information => "information",
            LogLevel.Warning => "warning",
            LogLevel.Error => "error",
            LogLevel.Critical => "critical",
            LogLevel.None => "off",
            _ => ((int)level).ToString(CultureInfo.InvariantCulture),
        };

        /// <summary>A stack level as rules spell it: <c>stack&gt;=error</c>.</summary>
        internal static string StackPart(LogLevel level) => $"{StackWord}>={Name(level)}";

        private const string StackWord = "stack";

        /// <summary>
        /// Whether <paramref name="text"/> is a stack level, <c>stack&gt;=</c> and a word, ignoring case
        /// and with spaces around <c>&gt;=</c>, and if so the word, which may not be a level.
        /// </summary>
        private static bool IsStack(string text, out string level)
        {
            level = "";
            if (!text.StartsWith(StackWord, StringComparison.OrdinalIgnoreCase))
                return false;

            string rest = text.Substring(StackWord.Length).TrimStart();
            if (!rest.StartsWith(">=", StringComparison.Ordinal))
                return false;

            level = rest.Substring(2).Trim();
            return true;
        }

        /// <summary>A level from its name in rules, ignoring case; <c>info</c> and <c>warn</c> are read too.</summary>
        public static bool TryLevel(string text, out LogLevel level)
        {
            switch (text.ToLowerInvariant())
            {
                case "trace": level = LogLevel.Trace; return true;
                case "debug": level = LogLevel.Debug; return true;
                case "information" or "info": level = LogLevel.Information; return true;
                case "warning" or "warn": level = LogLevel.Warning; return true;
                case "error": level = LogLevel.Error; return true;
                case "critical": level = LogLevel.Critical; return true;
                case "off" or "none": level = LogLevel.None; return true;
                default: level = default; return false;
            }
        }

        /// <summary>Equal when the least, the stack level and every rule, in order, are.</summary>
        public bool Equals(LogRules? other)
        {
            if (other is null || Least != other.Least || StackLevel != other.StackLevel || _rules.Length != other._rules.Length)
                return false;

            for (int i = 0; i < _rules.Length; i++)
            {
                if (!_rules[i].Equals(other._rules[i]))
                    return false;
            }

            return true;
        }

        /// <inheritdoc />
        public override bool Equals(object? obj) => obj is LogRules other && Equals(other);

        /// <inheritdoc />
        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Least);
            hash.Add(StackLevel);
            foreach (LogRule rule in _rules)
                hash.Add(rule);

            return hash.ToHashCode();
        }

        /// <summary>Per spec, the least level it is captured at, its limit, and the level from which it carries its stack: what a store's calls read.</summary>
        internal CompiledRules Compile(LogEventSpec[] specs)
        {
            byte[] least = new byte[specs.Length];
            int[] perSecond = new int[specs.Length];
            byte[] stack = new byte[specs.Length];
            for (int i = 0; i < specs.Length; i++)
            {
                least[i] = (byte)LeastFor(specs[i]);
                perSecond[i] = PerSecondFor(specs[i]);
                stack[i] = (byte)StackLevelFor(specs[i]);
            }

            return new CompiledRules(least, perSecond, stack);
        }

        private static Error NotALevel(string text) =>
            F.Error($"'{text}' is not a level, which is trace, debug, information, warning, error, critical or off", (int)LogRulesError.NotALevel);
    }

    /// <summary>One rule: for the events <paramref name="Pattern"/> names, a least level, a limit, a stack level, or more than one of them.</summary>
    /// <param name="Pattern"><c>*</c>, a category, a category and everything under it (<c>game.*</c>), or a key.</param>
    /// <param name="Least">The least level captured, <see cref="LogLevel.None"/> for off, or none to keep what a less specific rule says.</param>
    /// <param name="PerSecond">The most records a second kept, 0 for no limit, or none to keep the event's own.</param>
    /// <param name="StackLevel">The level from which a record carries its stack, <see cref="LogLevel.None"/> for none, or none to keep what a less specific rule says.</param>
    public sealed record LogRule(string Pattern, Optional<LogLevel> Least, Optional<int> PerSecond, Optional<LogLevel> StackLevel = default)
    {
        /// <summary>The rule as <see cref="LogRules.Parse"/> reads it: <c>physics/Contact=debug,10/s,stack&gt;=warning</c>.</summary>
        public override string ToString()
        {
            var parts = new List<string>(3);
            if (Least.HasValue(out LogLevel least))
                parts.Add(LogRules.Name(least));
            if (PerSecond.HasValue(out int perSecond))
                parts.Add($"{perSecond.ToString(CultureInfo.InvariantCulture)}/s");
            if (StackLevel.HasValue(out LogLevel stackLevel))
                parts.Add(LogRules.StackPart(stackLevel));

            return $"{Pattern}={string.Join(",", parts)}";
        }
    }

    /// <summary>Why rules' text does not parse: the <see cref="Error.Code"/> of the error <see cref="LogRules.Parse"/> fails with.</summary>
    public enum LogRulesError
    {
        /// <summary>A bare item, or what follows a pattern's <c>=</c>, that is not a level.</summary>
        NotALevel = 1,

        /// <summary>A pattern that is none of the four kinds.</summary>
        NotAPattern,

        /// <summary>A limit that is not a whole number of records a second, or a rule with two.</summary>
        NotALimit,

        /// <summary>A second bare level.</summary>
        LeastTwice,

        /// <summary>A second bare stack level, or a rule with two.</summary>
        StackLevelTwice,
    }

    /// <summary>What a store's calls read, made by <see cref="LogRules.Compile"/>.</summary>
    /// <param name="Least">Per spec, the least level it is captured at.</param>
    /// <param name="PerSecond">Per spec, the most records a second kept, 0 for no limit.</param>
    /// <param name="StackLevel">Per spec, the level from which its records carry their stack.</param>
    internal sealed record CompiledRules(byte[] Least, int[] PerSecond, byte[] StackLevel);

    /// <summary>How a rule's pattern matches a spec.</summary>
    internal static class LogPattern
    {
        /// <summary>Whether <paramref name="pattern"/> is one of the four kinds.</summary>
        public static bool IsValid(string pattern)
        {
            if (pattern == "*")
                return true;
            if (pattern.EndsWith(".*", StringComparison.Ordinal))
                return LogNames.IsCategory(pattern.Substring(0, pattern.Length - 2));

            int slash = pattern.IndexOf('/');
            return slash < 0
                ? LogNames.IsCategory(pattern)
                : LogNames.IsCategory(pattern.Substring(0, slash)) && LogNames.IsEventName(pattern.Substring(slash + 1));
        }

        /// <summary>
        /// How specifically <paramref name="pattern"/> names <paramref name="spec"/>, or -1 when it does
        /// not: a key beats a category, which beats every prefix, of which the longer beats the shorter,
        /// which beats <c>*</c>.
        /// </summary>
        public static int Specificity(string pattern, LogEventSpec spec)
        {
            if (pattern == "*")
                return 0;

            if (pattern.EndsWith(".*", StringComparison.Ordinal))
            {
                ReadOnlySpan<char> prefix = pattern.AsSpan(0, pattern.Length - 2);
                bool under = spec.Category.AsSpan().StartsWith(prefix)
                    && (spec.Category.Length == prefix.Length || spec.Category[prefix.Length] == '.');
                return under ? 1 + prefix.Length : -1;
            }

            if (pattern.IndexOf('/') >= 0)
                return pattern == spec.Key ? int.MaxValue : -1;

            return pattern == spec.Category ? int.MaxValue - 1 : -1;
        }
    }
}
