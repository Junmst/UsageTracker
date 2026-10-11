using UsageTrackerNative;
using Xunit;

namespace UsageTrackerNative.Tests;

public sealed class ClassificationAndTimeTests
{
    [Fact]
    public void DateBoundaryUsesFourAmAsTheBusinessDayStart()
    {
        Assert.Equal(new DateTime(2026, 9, 28), UsageTimeRange.GetTimeDistributionDate(new DateTime(2026, 9, 29, 3, 59, 59)));
        Assert.Equal(new DateTime(2026, 9, 29), UsageTimeRange.GetTimeDistributionDate(new DateTime(2026, 9, 29, 4, 0, 0)));
    }

    [Fact]
    public void OverlapDurationClipsCrossDaySession()
    {
        var duration = UsageTimeRange.GetOverlapDuration(
            new DateTime(2026, 9, 28, 23, 30, 0),
            new DateTime(2026, 9, 29, 4, 30, 0),
            new DateTime(2026, 9, 29, 4, 0, 0),
            new DateTime(2026, 9, 30, 4, 0, 0));

        Assert.Equal(TimeSpan.FromMinutes(30), duration);
    }

    [Fact]
    public void DirectMajorChildIsIncludedInKeywordClassification()
    {
        var definitions = new List<SubjectDefinition>
        {
            new() { Name = "国考", Children = ["申论"] }
        };
        var rules = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["申论"] = ["msedge * 申论"]
        };

        var result = ClassificationResolver.Resolve("msedge.exe", "申论 - Microsoft Edge", definitions, rules);

        Assert.Equal("申论", result);
    }

    [Fact]
    public void ManualClassificationOverridesAutomaticRule()
    {
        var definitions = new List<SubjectDefinition> { new() { Name = "工具" } };
        var rules = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["工具"] = ["msedge"]
        };
        var manual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["msedge.exe|学习页面"] = "国考"
        };

        var result = ClassificationResolver.Resolve("msedge.exe", "学习页面", definitions, rules, manual);

        Assert.Equal("国考", result);
    }

    [Fact]
    public void LongestKeywordWinsWhenMultipleRulesMatch()
    {
        // 标题"计算机专项知识"同时命中"计算机"与"计算机专项知识"，长词更精准，归"专项"。
        var definitions = new List<SubjectDefinition>
        {
            new() { Name = "通用" },
            new() { Name = "专项" },
        };
        var rules = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["通用"] = ["计算机"],
            ["专项"] = ["计算机专项知识"],
        };

        var result = ClassificationResolver.Resolve("msedge.exe", "计算机专项知识", definitions, rules);
        Assert.Equal("专项", result);
    }

    [Fact]
    public void LongSingleKeywordBeatsCompoundShortTerms()
    {
        // 复合短词"计算机*学习"（命中词项最大仅 3 字）不得压过单个长词"计算机专项知识"（7 字）。
        var definitions = new List<SubjectDefinition>
        {
            new() { Name = "泛学习" },
            new() { Name = "专项" },
        };
        var rules = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["泛学习"] = ["计算机*学习"],
            ["专项"] = ["计算机专项知识"],
        };

        var result = ClassificationResolver.Resolve("msedge.exe", "计算机专项知识学习", definitions, rules);
        Assert.Equal("专项", result);
    }

    [Fact]
    public void ScopedManualSubjectDoesNotRewriteHistory()
    {
        var definitions = new List<SubjectDefinition> { new() { Name = "专项" } };
        var rules = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var key = ClassificationResolver.BuildKey("msedge.exe", "计算机专项知识");
        var effective = new DateTime(2026, 10, 4, 4, 0, 0); // 业务日界：当日凌晨 4 点
        var scopes = new Dictionary<string, List<ManualSubjectScope>>
        {
            [key] = [new ManualSubjectScope(effective, "专项")],
        };

        // 生效点之前：无手动节点 → 关键词未命中 → 保留记录自身快照分类
        var before = ClassificationResolver.ResolveAt("msedge.exe", "计算机专项知识", definitions, rules, scopes, new DateTime(2026, 10, 3, 10, 0, 0), "旧分类");
        // 03:59 仍属前一业务日
        var boundaryBefore = ClassificationResolver.ResolveAt("msedge.exe", "计算机专项知识", definitions, rules, scopes, new DateTime(2026, 10, 4, 3, 59, 0), "旧分类");
        // 当日 04:00 起及未来：手动分类生效
        var boundaryAfter = ClassificationResolver.ResolveAt("msedge.exe", "计算机专项知识", definitions, rules, scopes, effective, "旧分类");
        var today = ClassificationResolver.ResolveAt("msedge.exe", "计算机专项知识", definitions, rules, scopes, new DateTime(2026, 10, 4, 9, 0, 0), "旧分类");
        var future = ClassificationResolver.ResolveAt("msedge.exe", "计算机专项知识", definitions, rules, scopes, new DateTime(2026, 11, 1, 9, 0, 0), "旧分类");

        Assert.Equal("旧分类", before);
        Assert.Equal("旧分类", boundaryBefore);
        Assert.Equal("专项", boundaryAfter);
        Assert.Equal("专项", today);
        Assert.Equal("专项", future);
    }

    [Fact]
    public void ScopedChangePreservesEarlierManualClassification()
    {
        // 全历史时期曾手动归为“通用”，当日改归“专项”：昨天及更早仍是“通用”，当日起变“专项”。
        var definitions = new List<SubjectDefinition> { new() { Name = "通用" }, new() { Name = "专项" } };
        var rules = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var key = ClassificationResolver.BuildKey("msedge.exe", "计算机专项知识");
        var scopes = new Dictionary<string, List<ManualSubjectScope>>
        {
            [key] =
            [
                new ManualSubjectScope(DateTime.MinValue, "通用"),
                new ManualSubjectScope(new DateTime(2026, 10, 4, 4, 0, 0), "专项"),
            ],
        };

        var yesterday = ClassificationResolver.ResolveAt("msedge.exe", "计算机专项知识", definitions, rules, scopes, new DateTime(2026, 10, 3, 20, 0, 0), null);
        var today = ClassificationResolver.ResolveAt("msedge.exe", "计算机专项知识", definitions, rules, scopes, new DateTime(2026, 10, 4, 12, 0, 0), null);

        Assert.Equal("通用", yesterday);
        Assert.Equal("专项", today);
    }

    [Fact]
    public void ScopedClearFallsBackToKeywordFromEffectiveDateOnly()
    {
        // 当日“清除分类”：历史保持原手动分类“通用”，当日起回落到关键词匹配（命中“专项”）。
        var definitions = new List<SubjectDefinition> { new() { Name = "通用" }, new() { Name = "专项" } };
        var rules = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["专项"] = ["计算机"],
        };
        var key = ClassificationResolver.BuildKey("msedge.exe", "计算机专项知识");
        var scopes = new Dictionary<string, List<ManualSubjectScope>>
        {
            [key] =
            [
                new ManualSubjectScope(DateTime.MinValue, "通用"),
                new ManualSubjectScope(new DateTime(2026, 10, 4, 4, 0, 0), null), // 显式清除节点
            ],
        };

        var yesterday = ClassificationResolver.ResolveAt("msedge.exe", "计算机专项知识", definitions, rules, scopes, new DateTime(2026, 10, 3, 20, 0, 0), "通用");
        var today = ClassificationResolver.ResolveAt("msedge.exe", "计算机专项知识", definitions, rules, scopes, new DateTime(2026, 10, 4, 12, 0, 0), "通用");

        Assert.Equal("通用", yesterday);
        Assert.Equal("专项", today);
    }

    [Fact]
    public void GlobalManualScopeAppliesAcrossAllTime()
    {
        var definitions = new List<SubjectDefinition> { new() { Name = "专项" } };
        var rules = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var key = ClassificationResolver.BuildKey("msedge.exe", "计算机专项知识");
        var scopes = new Dictionary<string, List<ManualSubjectScope>>
        {
            [key] = [new ManualSubjectScope(DateTime.MinValue, "专项")],
        };

        var past = ClassificationResolver.ResolveAt("msedge.exe", "计算机专项知识", definitions, rules, scopes, new DateTime(2020, 1, 1, 0, 0, 0), "旧分类");
        var now = ClassificationResolver.ResolveAt("msedge.exe", "计算机专项知识", definitions, rules, scopes, DateTime.Now, "旧分类");

        Assert.Equal("专项", past);
        Assert.Equal("专项", now);
    }

    [Fact]
    public void WinningKeywordRuleTextIsReturnedVerbatimWithOperators()
    {
        // 详情面板要原样展示命中规则（连接符一个符号不差）。
        var definitions = new List<SubjectDefinition> { new() { Name = "计算机" } };
        var rules = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["计算机"] = ["计算机*公考"],
        };

        var hit = SearchExpressionMatcher.ResolveKeywordRule(definitions, rules, "msedge.exe", "计算机专项知识 公考刷题");

        Assert.NotNull(hit);
        Assert.Equal("计算机", hit!.Subject);
        Assert.Equal("计算机*公考", hit.Rule);
    }

    [Fact]
    public void CharacterCountRanksLatinKeywordLongerThanChineseKeyword()
    {
        // 记录既有口径：按字符数而非显示宽度比较。"codebuddy"(9 字符) 长于"计算机专项知识"(7 字符)。
        var definitions = new List<SubjectDefinition> { new() { Name = "计算机" }, new() { Name = "编程助手" } };
        var rules = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["计算机"] = ["计算机专项知识"],
            ["编程助手"] = ["codebuddy"],
        };

        var hit = SearchExpressionMatcher.ResolveKeywordRule(definitions, rules, "codebuddy.exe", "计算机专项知识 codebuddy");

        Assert.NotNull(hit);
        Assert.Equal("编程助手", hit!.Subject);
        Assert.Equal("codebuddy", hit.Rule);
    }

    [Fact]
    public void DetailedResolutionReportsKeywordManualStoredAndNoneSources()
    {
        var definitions = new List<SubjectDefinition> { new() { Name = "计算机" } };
        var rules = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["计算机"] = ["计算机*公考"],
        };
        var now = DateTime.Now;

        // 关键词命中
        var keyword = ClassificationResolver.ResolveDetailedAt("msedge.exe", "计算机 公考", definitions, rules, null, now, null);
        Assert.Equal(SubjectResolutionSource.Keyword, keyword.Source);
        Assert.Equal("计算机", keyword.Subject);
        Assert.Equal("计算机*公考", keyword.MatchedRule);

        // 手动时间线节点优先，且不返回关键词
        var key = ClassificationResolver.BuildKey("msedge.exe", "计算机 公考");
        var scopes = new Dictionary<string, List<ManualSubjectScope>>
        {
            [key] = [new ManualSubjectScope(DateTime.MinValue, "手动分类")],
        };
        var manual = ClassificationResolver.ResolveDetailedAt("msedge.exe", "计算机 公考", definitions, rules, scopes, now, null);
        Assert.Equal(SubjectResolutionSource.Manual, manual.Source);
        Assert.Equal("手动分类", manual.Subject);
        Assert.Null(manual.MatchedRule);

        // 无手动、无关键词：回落记录上的历史快照
        var stored = ClassificationResolver.ResolveDetailedAt("notepad.exe", "随便标题", definitions, rules, null, now, "旧分类");
        Assert.Equal(SubjectResolutionSource.Stored, stored.Source);
        Assert.Equal("旧分类", stored.Subject);

        // 全空
        var none = ClassificationResolver.ResolveDetailedAt("notepad.exe", "随便标题", definitions, rules, null, now, null);
        Assert.Equal(SubjectResolutionSource.None, none.Source);
        Assert.Null(none.Subject);
    }

    [Fact]
    public void CompoundExpressionMatchesWithNegation()
    {
        Assert.True(SearchExpressionMatcher.IsMatch("edge * !private", term => term.Equals("edge", StringComparison.OrdinalIgnoreCase)));
        Assert.False(SearchExpressionMatcher.IsMatch("edge * !private", term => term is "edge" or "private"));
    }

    [Fact]
    public void NewOperatorsPlusStarMinusFollowSetSemantics()
    {
        // + 或：命中任一即可
        Assert.True(SearchExpressionMatcher.IsMatch("咸鱼+哔哩哔哩", term => term == "咸鱼"));
        Assert.True(SearchExpressionMatcher.IsMatch("咸鱼+哔哩哔哩", term => term == "哔哩哔哩"));
        Assert.False(SearchExpressionMatcher.IsMatch("咸鱼+哔哩哔哩", term => term == "其他"));

        // * 与：必须同时命中
        Assert.True(SearchExpressionMatcher.IsMatch("msedge * 申论", term => term is "msedge" or "申论"));
        Assert.False(SearchExpressionMatcher.IsMatch("msedge * 申论", term => term == "msedge"));

        // - 差集：命中 a 且不命中 b；a-b-c 连续剔除
        Assert.True(SearchExpressionMatcher.IsMatch("咸鱼-哔哩哔哩", term => term == "咸鱼"));
        Assert.False(SearchExpressionMatcher.IsMatch("咸鱼-哔哩哔哩", term => term is "咸鱼" or "哔哩哔哩"));
        Assert.True(SearchExpressionMatcher.IsMatch("a-b-c", term => term == "a"));
        Assert.False(SearchExpressionMatcher.IsMatch("a-b-c", term => term is "a" or "c"));

        // 优先级：* 高于 - 高于 +；a+b*c-d == a 或 ((b 且 c) 但非 d)
        Assert.True(SearchExpressionMatcher.IsMatch("a+b*c-d", term => term == "a"));
        Assert.False(SearchExpressionMatcher.IsMatch("a+b*c-d", term => term is "b" or "d"));
        Assert.True(SearchExpressionMatcher.IsMatch("a+b*c-d", term => term is "b" or "c"));

        // 括号分组与全角运算符
        Assert.True(SearchExpressionMatcher.IsMatch("(a+b)*c", term => term is "a" or "c"));
        Assert.False(SearchExpressionMatcher.IsMatch("(a+b)*c", term => term == "a"));
        Assert.True(SearchExpressionMatcher.IsMatch("咸鱼－哔哩哔哩", term => term == "咸鱼"));
        Assert.True(SearchExpressionMatcher.IsMatch("咸鱼＋b站", term => term == "b站"));
        Assert.True(SearchExpressionMatcher.IsMatch("msedge＊申论", term => term is "msedge" or "申论"));
    }

    [Fact]
    public void LegacyPipeAndAmpersandOperatorsRemainSupported()
    {
        // 已保存的旧关键词规则继续有效
        Assert.True(SearchExpressionMatcher.IsMatch("edge & !private", term => term is "edge"));
        Assert.False(SearchExpressionMatcher.IsMatch("edge & !private", term => term is "edge" or "private"));
        Assert.True(SearchExpressionMatcher.IsMatch("a|b", term => term == "b"));
        Assert.Equal(4, SearchExpressionMatcher.GetTermCount("咸鱼+国考*b站-哔哩哔哩"));
    }
}
