namespace BqtjLauncher.Domain.Tests;

/// <summary>掉落上限只用虚构同帧文本；两组均有绿色和有效数字才能结束。</summary>
public sealed class DailyMaterialQuotaTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReachingOrExceedingTheLimitStillRequiresGreenNumbers(bool green)
    {
        var quota = DailyMaterialQuota.Parse([new("冷冻宝石 208/205", green), new("冥王图鉴 29/27", green)]);
        Assert.NotNull(quota); Assert.Equal(green, quota.IsFull);
    }

    [Theory]
    [InlineData("冷冻宝石")]
    [InlineData("守护宝石")]
    [InlineData("冷冻宝石/守护宝石")]
    public void BothIndependentLimitsMustBeFull(string gems)
    {
        var quota = DailyMaterialQuota.Parse([new(gems + " 10/10", true), new("冥王图鉴 5/5", true)]);
        Assert.True(quota!.IsFull);
        Assert.False(DailyMaterialQuota.Parse([new(gems + " 9/10", true), new("冥王图鉴 5/5", true)])!.IsFull);
        Assert.False(DailyMaterialQuota.Parse([new(gems + " 10/10", true), new("冥王图鉴 4/5", true)])!.IsFull);
    }

    [Theory]
    [InlineData("10/10", false)]
    [InlineData("9/10", true)]
    public void NeitherGreenNorCountAloneIsCompletion(string count, bool green)
        => Assert.False(DailyMaterialQuota.Parse([new("冷冻宝石 " + count, green), new("冥王图鉴 5/5", true)])!.IsFull);

    [Theory]
    [InlineData("冷冻宝石 10/0")]
    [InlineData("冷冻宝石 -10/10")]
    [InlineData("冷冻宝石 1O/10")]
    [InlineData("冷冻宝石 1/10 5/5")]
    [InlineData("冷冻宝石 冥王图鉴 10/10")]
    public void AmbiguousOrInvalidCounterRequiresNewObservation(string text)
        => Assert.Null(DailyMaterialQuota.Parse([new(text, true), new("冥王图鉴 5/5", true)]));

    [Fact]
    public void SharedGemAliasesCannotProvideContradictoryCounts()
    {
        Assert.Null(DailyMaterialQuota.Parse([new("冷冻宝石 10/10", true), new("守护宝石 9/10", false), new("冥王图鉴 5/5", true)]));
        Assert.Null(DailyMaterialQuota.Parse([new("冷冻宝石 10/10", true)]));
    }

    [Fact]
    public void SharedGemLabelMayAppearOnASeparateLineWithoutRepeatingTheCounter()
    {
        var quota = DailyMaterialQuota.Parse([new("冷冻宝石（22/205）", false), new("守护宝石", false), new("冥王图鉴（3/27）", false)]);
        Assert.NotNull(quota); Assert.Equal(22, quota.Gems.Collected); Assert.Equal(205, quota.Gems.Limit);
        Assert.False(quota.IsFull);
    }
}
