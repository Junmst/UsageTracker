using System;
using System.Linq;

namespace UsageTrackerNative;

public static class SearchExpressionMatcher
{
    private static readonly char[] OperatorChars = ['&', '|', '!', '！', '(', ')', '（', '）'];

    public static bool IsMatch(string? expression, Func<string, bool> matchesTerm)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return true;
        }

        var parser = new Parser(expression, matchesTerm);
        return parser.Parse();
    }

    public static int GetTermCount(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return 0;
        }

        return expression
            .Split(OperatorChars, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(term => !string.IsNullOrWhiteSpace(term));
    }

    public static bool IsCompoundExpression(string? expression)
    {
        return GetTermCount(expression) > 1;
    }

    public static string? ResolvePrimaryTitleSubjectByKeywordRules(
        IEnumerable<SubjectDefinition> definitions,
        IReadOnlyDictionary<string, List<string>> keywordRules,
        string windowTitle)
    {
        var titleParts = BrowserWindowTitleParts.From(windowTitle);
        if (string.Equals(titleParts.PrimaryTitle, titleParts.FullTitle, StringComparison.Ordinal))
        {
            return null;
        }

        var matches = new List<SubjectKeywordRuleMatch>();
        var order = 0;
        foreach (var definition in definitions)
        {
            foreach (var parent in definition.Parents)
            {
                foreach (var child in parent.Children)
                {
                    AddPrimaryTitleMatchingRules(matches, child, keywordRules, titleParts, order++);
                }

                AddPrimaryTitleMatchingRules(matches, parent.Name, keywordRules, titleParts, order++);
            }

            AddPrimaryTitleMatchingRules(matches, definition.Name, keywordRules, titleParts, order++);
        }

        return matches
            .OrderByDescending(match => match.TermCount)
            .ThenByDescending(match => match.ExpressionLength)
            .ThenBy(match => match.SubjectOrder)
            .ThenBy(match => match.RuleOrder)
            .FirstOrDefault()
            ?.Subject;
    }

    public static string? ResolveSubjectByKeywordRules(
        IEnumerable<SubjectDefinition> definitions,
        IReadOnlyDictionary<string, List<string>> keywordRules,
        string processName,
        string windowTitle)
    {
        var matches = new List<SubjectKeywordRuleMatch>();
        var order = 0;
        var titleParts = BrowserWindowTitleParts.From(windowTitle);

        foreach (var definition in definitions)
        {
            foreach (var parent in definition.Parents)
            {
                foreach (var child in parent.Children)
                {
                    AddMatchingRules(matches, child, keywordRules, processName, titleParts, order++);
                }

                AddMatchingRules(matches, parent.Name, keywordRules, processName, titleParts, order++);
            }

            AddMatchingRules(matches, definition.Name, keywordRules, processName, titleParts, order++);
        }

        return matches
            .OrderByDescending(match => match.MatchPriority)
            .ThenByDescending(match => match.TermCount)
            .ThenByDescending(match => match.ExpressionLength)
            .ThenBy(match => match.SubjectOrder)
            .ThenBy(match => match.RuleOrder)
            .FirstOrDefault()
            ?.Subject;
    }

    private static void AddMatchingRules(
        List<SubjectKeywordRuleMatch> matches,
        string subject,
        IReadOnlyDictionary<string, List<string>> keywordRules,
        string processName,
        BrowserWindowTitleParts titleParts,
        int subjectOrder)
    {
        if (!keywordRules.TryGetValue(subject, out var rules))
        {
            return;
        }

        for (var ruleOrder = 0; ruleOrder < rules.Count; ruleOrder++)
        {
            var rule = rules[ruleOrder];
            if (string.IsNullOrWhiteSpace(rule))
            {
                continue;
            }

            var matchPriority = GetRuleMatchPriority(rule, processName, titleParts);
            if (matchPriority <= 0)
            {
                continue;
            }

            matches.Add(new SubjectKeywordRuleMatch(subject, subjectOrder, ruleOrder, GetTermCount(rule), rule.Trim().Length, matchPriority));
        }
    }

    private static int GetRuleMatchPriority(string rule, string processName, BrowserWindowTitleParts titleParts)
    {
        if (IsMatch(rule, term => titleParts.PrimaryTitle.Contains(term, StringComparison.OrdinalIgnoreCase)))
        {
            return 2;
        }

        return IsMatch(rule, term => processName.Contains(term, StringComparison.OrdinalIgnoreCase)
            || titleParts.FullTitle.Contains(term, StringComparison.OrdinalIgnoreCase))
            ? 1
            : 0;
    }

    private static void AddPrimaryTitleMatchingRules(
        List<SubjectKeywordRuleMatch> matches,
        string subject,
        IReadOnlyDictionary<string, List<string>> keywordRules,
        BrowserWindowTitleParts titleParts,
        int subjectOrder)
    {
        if (!keywordRules.TryGetValue(subject, out var rules))
        {
            return;
        }

        for (var ruleOrder = 0; ruleOrder < rules.Count; ruleOrder++)
        {
            var rule = rules[ruleOrder];
            if (string.IsNullOrWhiteSpace(rule) || !IsMatch(rule, term => titleParts.PrimaryTitle.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            matches.Add(new SubjectKeywordRuleMatch(subject, subjectOrder, ruleOrder, GetTermCount(rule), rule.Trim().Length, 2));
        }
    }

    private sealed record SubjectKeywordRuleMatch(string Subject, int SubjectOrder, int RuleOrder, int TermCount, int ExpressionLength, int MatchPriority);

    private readonly record struct BrowserWindowTitleParts(string FullTitle, string PrimaryTitle)
    {
        public static BrowserWindowTitleParts From(string windowTitle)
        {
            var fullTitle = NormalizeTitle(windowTitle);
            var primaryTitle = ExtractPrimaryTitle(fullTitle);
            return new BrowserWindowTitleParts(fullTitle, string.IsNullOrWhiteSpace(primaryTitle) ? fullTitle : primaryTitle);
        }

        private static string NormalizeTitle(string windowTitle)
        {
            return (windowTitle ?? string.Empty)
                .Replace("\u200B", string.Empty, StringComparison.Ordinal)
                .Replace("\u200C", string.Empty, StringComparison.Ordinal)
                .Replace("\u200D", string.Empty, StringComparison.Ordinal)
                .Replace("\uFEFF", string.Empty, StringComparison.Ordinal)
                .Trim();
        }

        private static string ExtractPrimaryTitle(string fullTitle)
        {
            var parts = fullTitle.Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || !IsBrowserShellName(parts[^1]))
            {
                return fullTitle;
            }

            var titlePartCount = parts.Length - 1;
            if (titlePartCount > 1 && IsLikelyBrowserPartitionName(parts[^2]))
            {
                titlePartCount--;
            }

            return string.Join(" - ", parts.Take(titlePartCount)).Trim();
        }

        private static bool IsBrowserShellName(string value)
        {
            return value.Contains("Edge", StringComparison.OrdinalIgnoreCase)
                || value.Contains("Chrome", StringComparison.OrdinalIgnoreCase)
                || value.Contains("Firefox", StringComparison.OrdinalIgnoreCase)
                || value.Contains("浏览器", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLikelyBrowserPartitionName(string value)
        {
            var trimmed = value.Trim();
            return trimmed.Length is > 0 and <= 16
                && !trimmed.Contains(".", StringComparison.Ordinal)
                && !trimmed.Contains("://", StringComparison.Ordinal)
                && !trimmed.Contains("/", StringComparison.Ordinal)
                && !trimmed.Contains("\\", StringComparison.Ordinal);
        }
    }

    private sealed class Parser
    {
        private readonly string _text;
        private readonly Func<string, bool> _matchesTerm;
        private int _position;

        public Parser(string text, Func<string, bool> matchesTerm)
        {
            _text = text;
            _matchesTerm = matchesTerm;
        }

        public bool Parse()
        {
            SkipWhiteSpace();
            if (IsEnd)
            {
                return true;
            }

            var result = ParseOr();
            SkipWhiteSpace();
            return result && IsEnd;
        }

        private bool ParseOr()
        {
            var result = ParseAnd();
            while (true)
            {
                SkipWhiteSpace();
                if (!Consume('|'))
                {
                    return result;
                }

                result = ParseAnd() || result;
            }
        }

        private bool ParseAnd()
        {
            var result = ParseNot();
            while (true)
            {
                SkipWhiteSpace();
                if (!Consume('&'))
                {
                    return result;
                }

                result = ParseNot() && result;
            }
        }

        private bool ParseNot()
        {
            SkipWhiteSpace();
            var negate = false;
            while (Consume('!') || Consume('！'))
            {
                negate = !negate;
                SkipWhiteSpace();
            }

            var value = ParsePrimary();
            return negate ? !value : value;
        }

        private bool ParsePrimary()
        {
            SkipWhiteSpace();
            if (Consume('(') || Consume('（'))
            {
                var value = ParseOr();
                SkipWhiteSpace();
                _ = Consume(')') || Consume('）');
                return value;
            }

            var term = ReadTerm();
            return !string.IsNullOrWhiteSpace(term) && _matchesTerm(term.Trim());
        }

        private string ReadTerm()
        {
            var start = _position;
            while (!IsEnd && !IsOperator(Current))
            {
                _position++;
            }

            return _text[start.._position];
        }

        private void SkipWhiteSpace()
        {
            while (!IsEnd && char.IsWhiteSpace(Current))
            {
                _position++;
            }
        }

        private bool Consume(char value)
        {
            if (IsEnd || Current != value)
            {
                return false;
            }

            _position++;
            return true;
        }

        private bool IsEnd => _position >= _text.Length;

        private char Current => _text[_position];

        private static bool IsOperator(char value)
        {
            return OperatorChars.Contains(value);
        }
    }
}
