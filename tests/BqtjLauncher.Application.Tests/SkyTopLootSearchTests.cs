using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

/// <summary>搜索按本关候选点顺序执行，有步数及时长上限；不依赖游戏速度推断找到材料。</summary>
public sealed class SkyTopLootSearchTests
{
    [Fact]
    public void SearchVisitsUpperRightBeforeRightAndStopsAfterAcknowledgedPulses()
    {
        var search = new SkyTopLootSearch();
        for (var i = 0; i < 16; i++)
        {
            var step = search.Next(TimeSpan.FromMilliseconds(i * 300));
            Assert.False(step.Finished); Assert.Equal(i < 6 ? 'W' : i < 12 ? 'S' : 'D', step.Key);
            Assert.Equal(i < 12 ? 'D' : (char?)null, step.SecondKey);
            Assert.Equal(120, step.Milliseconds);
            search.Applied();
        }
        var finished = search.Next(TimeSpan.FromSeconds(8));
        Assert.True(finished.Finished); Assert.Null(finished.Key);
    }

    [Fact]
    public void SlowRecognitionStillStopsAtTheActiveTimeLimit()
    {
        var search = new SkyTopLootSearch();
        search.Next(TimeSpan.FromSeconds(5));
        Assert.Throws<TimeoutException>(() => search.Next(TimeSpan.FromSeconds(35)));
    }

    [Fact]
    public void AnUnacknowledgedOrRejectedInputDoesNotConsumeSearchSteps()
    {
        var search = new SkyTopLootSearch();
        for (var i = 0; i < 10; i++) Assert.Equal('W', search.Next(TimeSpan.FromMilliseconds(i * 100)).Key);
        for (var i = 0; i < 6; i++) search.Applied();
        Assert.Equal('S', search.Next(TimeSpan.FromSeconds(2)).Key);
    }
}
