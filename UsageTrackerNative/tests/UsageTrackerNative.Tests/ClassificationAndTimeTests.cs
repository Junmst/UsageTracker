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
            ["申论"] = ["msedge & 申论"]
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
        Assert.True(SearchExpressionMatcher.IsMatch("edge & !private", term => term.Equals("edge", StringComparison.OrdinalIgnoreCase)));
        Assert.False(SearchExpressionMatcher.IsMatch("edge & !private", term => term is "edge" or "private"));
    }
}
