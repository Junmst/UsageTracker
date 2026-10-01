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
