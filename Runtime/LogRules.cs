using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Tutan.Functional;

namespace Tutan.Logging
{
    /// <summary>
    /// What a store captures: a level every event is held to, and rules that change it, or limit how
    /// often an event is kept, for the events a pattern names. Rules are data, compiled with the
    /// catalog into one byte per event, which is all a call reads to decide it is off.
    /// </summary>
    public sealed class LogRules : IEquatable<LogRules>
    {
        private readonly LogRule[] _rules;
        private readonly ReadOnlyCollection<LogRule> _ruleList;

        /// <summary>Rules holding every event to <paramref name="least"/>, but where <paramref name="rules"/> say otherwise.</summary>
        /// <param name="least">The least level captured where no rule says otherwise.</param>
        /// <param name="rules">The rules. Where two patterns match an event, the more specific wins, and of two alike, the later.</param>
        public LogRules(LogLevel least, params LogRule[] rules)
        {
            if (rules is null)
                throw new ArgumentNullException(nameof(rules));

            Least = least;
            _rules = (LogRule[])rules.Clone();
            foreach (LogRule rule in _rules)
            {
                if (rule is null)
                    throw new ArgumentNullException(nameof(rules), "One of the rules is null.");
            }

            _ruleList = Array.AsReadOnly(_rules);
        }

        /// <summary>
        /// Debug and above, and no rules: a store captures what a developer would want when something
        /// goes wrong, and the sinks, which write from Information, write it only as an error's context.
        /// </summary>
        public static LogRules Default { get; } = new LogRules(LogLevel.Debug);

        /// <summary>The least level captured where no rule says otherwise.</summary>
        public LogLevel Least { get; }

        /// <summary>The rules, in the order they were given.</summary>
        public IReadOnlyList<LogRule> Rules => _ruleList;

        /// <summary>
        /// Rules from their text: items separated by <c>;</c>, of which one may be a bare level, the
        /// least, and the others <c>pattern=what</c>, where what is a level, <c>off</c>, a limit such
        /// as <c>10/s</c>, or a level and a limit separated by a comma.
        /// <code>
        /// debug; game.save=trace; ai.*=warning; game.input/Moved=off; physics/Contact=10/s
        /// </code>
        /// A pattern is <c>*</c>, a category (<c>game.save</c>), a category and everything under it
        /// (<c>game.*</c>), or a key (<c>game.save/Saved</c>). Text with no bare level keeps
        /// <see cref="Default"/>'s.
        /// </summary>
        public static Result<LogRules> Parse(string text)
        {
            if (text is null)
                throw new ArgumentNullException(nameof(text));

            LogLevel least = Default.Least;
            bool leastGiven = false;
            var rules = new List<LogRule>();

            foreach (string raw in text.Split(';'))
            {
                string item = raw.Trim();
                if (item.Length == 0)
                    continue;

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
                foreach (string rawPart in item.Substring(equals + 1).Split(','))
                {
                    string part = rawPart.Trim();
                    if (part.EndsWith("/s", StringComparison.Ordinal))
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

                rules.Add(new LogRule(pattern, ruleLeast, perSecond));
            }

            return F.Success(new LogRules(least, rules.ToArray()));
        }

        /// <summary>The least level <paramref name="spec"/> is captured at, <see cref="LogLevel.None"/> when it is off.</summary>
        public LogLevel LeastFor(LogEventSpec spec)
        {
            LogLevel least = Least;
            int best = -1;
            foreach (LogRule rule in _rules)
            {
                int specificity = LogPattern.Specificity(rule.Pattern, spec);
                if (specificity >= 0 && specificity >= best && rule.Least.HasValue(out LogLevel level))
                {
                    best = specificity;
                    least = level;
                }
            }

            return least;
        }

        /// <summary>How many records of <paramref name="spec"/> a second are kept, 0 for no limit: the most specific rule's limit, or the spec's own.</summary>
        public int PerSecondFor(LogEventSpec spec)
        {
            int perSecond = spec.PerSecond;
            int best = -1;
            foreach (LogRule rule in _rules)
            {
                int specificity = LogPattern.Specificity(rule.Pattern, spec);
                if (specificity >= 0 && specificity >= best && rule.PerSecond.HasValue(out int limit))
                {
                    best = specificity;
                    perSecond = limit;
                }
            }

            return perSecond;
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

        /// <summary>Equal when the least and every rule, in order, are.</summary>
        public bool Equals(LogRules? other)
        {
            if (other is null || Least != other.Least || _rules.Length != other._rules.Length)
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
            foreach (LogRule rule in _rules)
                hash.Add(rule);

            return hash.ToHashCode();
        }

        /// <summary>One byte per spec, the least level it is captured at, and its limit: what a store's calls read.</summary>
        internal CompiledRules Compile(LogEventSpec[] specs)
        {
            byte[] least = new byte[specs.Length];
            int[] perSecond = new int[specs.Length];
            for (int i = 0; i < specs.Length; i++)
            {
                least[i] = (byte)LeastFor(specs[i]);
                perSecond[i] = PerSecondFor(specs[i]);
            }

            return new CompiledRules(least, perSecond);
        }

        private static Error NotALevel(string text) =>
            F.Error($"'{text}' is not a level, which is trace, debug, information, warning, error, critical or off", (int)LogRulesError.NotALevel);
    }

    /// <summary>One rule: for the events <paramref name="Pattern"/> names, a least level, a limit, or both.</summary>
    /// <param name="Pattern"><c>*</c>, a category, a category and everything under it (<c>game.*</c>), or a key.</param>
    /// <param name="Least">The least level captured, <see cref="LogLevel.None"/> for off, or none to keep what a less specific rule says.</param>
    /// <param name="PerSecond">The most records a second kept, 0 for no limit, or none to keep the event's own.</param>
    public sealed record LogRule(string Pattern, Optional<LogLevel> Least, Optional<int> PerSecond)
    {
        /// <summary>The rule as <see cref="LogRules.Parse"/> reads it: <c>physics/Contact=debug,10/s</c>.</summary>
        public override string ToString()
        {
            string level = Least.HasValue(out LogLevel least) ? LogRules.Name(least) : "";
            string limit = PerSecond.HasValue(out int perSecond) ? $"{perSecond.ToString(CultureInfo.InvariantCulture)}/s" : "";
            string comma = level.Length > 0 && limit.Length > 0 ? "," : "";
            return $"{Pattern}={level}{comma}{limit}";
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
    }

    /// <summary>What a store's calls read, made by <see cref="LogRules.Compile"/>.</summary>
    internal sealed class CompiledRules
    {
        public CompiledRules(byte[] least, int[] perSecond)
        {
            Least = least;
            PerSecond = perSecond;
        }

        public byte[] Least { get; }

        public int[] PerSecond { get; }
    }

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
