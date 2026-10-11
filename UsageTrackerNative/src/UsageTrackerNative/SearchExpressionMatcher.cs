using System;
using System.Linq;

namespace UsageTrackerNative;

public static class SearchExpressionMatcher
{
    // 运算符：+ 或（兼容旧 |）、* 与（兼容旧 &）、- 差集（a-b 表示命中 a 且不命中 b）、! 非、() 分组；
    // 同时接受全角 ＋－＊（），方便中文输入法直接录入。
    private static readonly char[] OperatorChars =
        ['+', '＋', '*', '＊', '-', '－', '|', '&', '!', '！', '(', ')', '（', '）'];

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
            foreach (var directChild in definition.Children)
            {
                AddPrimaryTitleMatchingRules(matches, directChild, keywordRules, titleParts, order++);
            }

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
            .OrderByDescending(match => match.MatchedTermMaxLength)
            .ThenByDescending(match => match.TermCount)
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
        => ResolveKeywordRule(definitions, keywordRules, processName, windowTitle)?.Subject;

    // 与 ResolveSubjectByKeywordRules 同一套排序规则，但额外返回获胜规则的原文
    //（仅去除首尾空白，+、*、-、() 等逻辑连接符原样保留），供界面展示“命中了哪条关键词”。
    public static KeywordRuleHit? ResolveKeywordRule(
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
            foreach (var directChild in definition.Children)
            {
                AddMatchingRules(matches, directChild, keywordRules, processName, titleParts, order++);
            }

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

        var winner = matches
            .OrderByDescending(match => match.MatchPriority)
            .ThenByDescending(match => match.MatchedTermMaxLength)
            .ThenByDescending(match => match.TermCount)
            .ThenByDescending(match => match.ExpressionLength)
            .ThenBy(match => match.SubjectOrder)
            .ThenBy(match => match.RuleOrder)
            .FirstOrDefault();

        return winner is null ? null : new KeywordRuleHit(winner.Subject, winner.Rule, winner.MatchPriority == 2);
    }

    // 命中词项的最大字面长度：多个关键词都命中时，覆盖文本越长越精准。
    // 例：标题"计算机专项知识"，单词"计算机专项知识"(7) 应胜过"计算机"(3)，
    // 也应胜过复合短词"计算机*学习"(命中词项最大仅 3/2)。
    // 否定词无需特判：它若出现在文本中整条规则就不命中；不出现时 Contains 自然为 false。
    private static int GetLongestMatchedTermLength(string expression, params string[] domains)
    {
        var terms = expression.Split(OperatorChars, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var best = 0;
        foreach (var term in terms)
        {
            if (term.Length <= best)
            {
                continue;
            }
            foreach (var domain in domains)
            {
                if (domain.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    best = term.Length;
                    break;
                }
            }
        }
        return best;
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

            var matchedTermMaxLength = matchPriority == 2
                ? GetLongestMatchedTermLength(rule, titleParts.PrimaryTitle)
                : GetLongestMatchedTermLength(rule, processName, titleParts.FullTitle);
            matches.Add(new SubjectKeywordRuleMatch(subject, subjectOrder, ruleOrder, GetTermCount(rule), rule.Trim().Length, matchPriority, matchedTermMaxLength, rule.Trim()));
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

            var matchedTermMaxLength = GetLongestMatchedTermLength(rule, titleParts.PrimaryTitle);
            matches.Add(new SubjectKeywordRuleMatch(subject, subjectOrder, ruleOrder, GetTermCount(rule), rule.Trim().Length, 2, matchedTermMaxLength, rule.Trim()));
        }
    }

    private sealed record SubjectKeywordRuleMatch(string Subject, int SubjectOrder, int RuleOrder, int TermCount, int ExpressionLength, int MatchPriority, int MatchedTermMaxLength, string Rule);

    // 关键词规则命中结果：Subject 为归属分类，Rule 为用户配置的规则原文（连接符原样保留），
    // MatchedInPrimaryTitle 表示是否命中浏览器主标题（优先级高于进程名/完整标题命中）。
    public sealed record KeywordRuleHit(string Subject, string Rule, bool MatchedInPrimaryTitle);

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

        // +（或，优先级最低）
        private bool ParseOr()
        {
            var result = ParseDifference();
            while (true)
            {
                SkipWhiteSpace();
                if (!IsOrOperator(Current))
                {
                    return result;
                }

                _position++;
                result = ParseDifference() || result;
            }
        }

        // -（差集，左结合）：a-b 等价于 a 且非 b；a-b-c 等价于 a 且非 b 且非 c
        private bool ParseDifference()
        {
            var result = ParseAnd();
            while (true)
            {
                SkipWhiteSpace();
                if (!IsMinusOperator(Current))
                {
                    return result;
                }

                _position++;
                var excluded = ParseAnd();
                result = result && !excluded;
            }
        }

        // *（与，优先级最高的二元运算）
        private bool ParseAnd()
        {
            var result = ParseNot();
            while (true)
            {
                SkipWhiteSpace();
                if (!IsAndOperator(Current))
                {
                    return result;
                }

                _position++;
                result = ParseNot() && result;
            }
        }

        private bool ParseNot()
        {
            SkipWhiteSpace();
            var negate = false;
            while (CurrentIs('!') || CurrentIs('！'))
            {
                negate = !negate;
                _position++;
                SkipWhiteSpace();
            }

            var value = ParsePrimary();
            return negate ? !value : value;
        }

        private bool ParsePrimary()
        {
            SkipWhiteSpace();
            if (CurrentIs('(') || CurrentIs('（'))
            {
                _position++;
                var value = ParseOr();
                SkipWhiteSpace();
                if (CurrentIs(')') || CurrentIs('）'))
                {
                    _position++;
                }

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

        private bool CurrentIs(char value) => !IsEnd && Current == value;

        private bool IsEnd => _position >= _text.Length;

        private char Current => IsEnd ? '\0' : _text[_position];

        private static bool IsOperator(char value) => OperatorChars.Contains(value);

        private static bool IsOrOperator(char value) => value is '+' or '＋' or '|';

        private static bool IsAndOperator(char value) => value is '*' or '＊' or '&';

        private static bool IsMinusOperator(char value) => value is '-' or '－';
    }
}
