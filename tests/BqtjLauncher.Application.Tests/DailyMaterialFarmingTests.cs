using System.IO;
using BqtjLauncher.Domain;

namespace BqtjLauncher.Application.Tests;

/// <summary>受控战斗夹具覆盖刷取策略；不把定时等待当作真实战斗或拾取验证。</summary>
public sealed class DailyMaterialFarmingTests
{
    private static readonly int[] QuotaChecks = [10, 11, 12];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThreeEmptyScreensCannotReplaceConfirmedQuotas(bool president)
    {
        var battle = new Battle();
        battle.Results.Enqueue(DailyMaterialRoundResult.UnconfirmedEmpty);
        battle.Results.Enqueue(DailyMaterialRoundResult.UnconfirmedEmpty);
        battle.Results.Enqueue(DailyMaterialRoundResult.UnconfirmedEmpty);
        battle.Results.Enqueue(DailyMaterialRoundResult.QuotaFull);
        var stages = new List<string>();
        var count = await new DailyMaterialFarming(battle).RunAsync(new("天空顶", "地狱四", null, president), stages.Add, default);
        Assert.Equal(13, count); Assert.Equal(12, battle.Replays);
        Assert.DoesNotContain(stages, stage => stage.Contains("保底", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConsecutiveHiddenDropsWithUnreadableQuotasKeepTheBattleUntilStopped()
    {
        using var stop = new CancellationTokenSource();
        var battle = new Battle();
        for (var index = 0; index < 9; index++) battle.Results.Enqueue(DailyMaterialRoundResult.UnconfirmedEmpty);
        battle.OnCollected = () => { if (battle.Collected == 18) stop.Cancel(); };
        var stages = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DailyMaterialFarming(battle)
            .RunAsync(new("天空顶", "地狱四", null), stages.Add, stop.Token));
        Assert.Equal(18, battle.Collected); Assert.Equal(0, battle.Exits);
        Assert.DoesNotContain(stages, text => text.Contains("保底", StringComparison.Ordinal));
        Assert.DoesNotContain(stages, text => text.Contains("均已刷满", StringComparison.Ordinal));
    }
    [Fact]
    public async Task SeeingMaterialsAfterRoundTenDoesNotOpenTheQuotaPanel()
    {
        using var stop = new CancellationTokenSource();
        var battle = new Battle { InitialMaterials = true };
        battle.OnCollected = () => { if (battle.Collected == 12) stop.Cancel(); };
        battle.Quotas.Enqueue(new(new(10, 10, true), new(5, 5, true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DailyMaterialFarming(battle)
            .RunAsync(new("天空顶", "地狱四", null), _ => { }, stop.Token));
        Assert.Empty(battle.CheckedAfter);
    }
    [Fact]
    public async Task StartsQuotaChecksFromTheTenthRoundAndNeedsBothLimits()
    {
        var battle = new Battle();
        battle.Quotas.Enqueue(new(new(10, 10, true), new(4, 5, false)));
        battle.Quotas.Enqueue(new(new(9, 10, false), new(5, 5, true)));
        battle.Quotas.Enqueue(new(new(10, 10, true), new(5, 5, true)));
        var count = await new DailyMaterialFarming(battle).RunAsync(DailyMaterialFarmOptions.Parse("天空顶", "地狱四", "刷满"), _ => { }, default);
        Assert.Equal(12, count);
        Assert.Equal(QuotaChecks, battle.CheckedAfter);
        Assert.Equal(11, battle.Replays); Assert.Equal(1, battle.Exits);
        Assert.Equal("enter:天空顶:地狱四", battle.Actions[0]); Assert.Equal("exit", battle.Actions[^1]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(10)]
    public async Task CountModeRunsExactlyRequestedNumberWithoutQuotaDependency(int runs)
    {
        var battle = new Battle();
        var count = await new DailyMaterialFarming(battle).RunAsync(new("天空顶", "地狱四", runs), _ => { }, default);
        Assert.Equal(runs, count); Assert.Equal(runs - 1, battle.Replays);
        Assert.Empty(battle.CheckedAfter); Assert.Equal(1, battle.Exits);
    }

    [Fact]
    public async Task StopAfterBattleNeverReplaysOrExitsTheRetainedGame()
    {
        using var stop = new CancellationTokenSource();
        var battle = new Battle { OnCollected = stop.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new DailyMaterialFarming(battle).RunAsync(new("天空顶", "地狱四", null), _ => { }, stop.Token));
        Assert.Equal(0, battle.Replays); Assert.Equal(0, battle.Exits);
    }

    [Fact]
    public async Task FailureToConfirmPickupDoesNotReplayAndLoseTheDrop()
    {
        var battle = new Battle { FailPickup = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DailyMaterialFarming(battle).RunAsync(new("天空顶", "地狱四", 1), _ => { }, default));
        Assert.Equal(0, battle.Replays); Assert.Equal(0, battle.Exits); Assert.Empty(battle.CheckedAfter);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1次")]
    [InlineData("9999999999999")]
    [InlineData("八次")]
    public void InvalidTargetCannotStart(string target)
        => Assert.Throws<InvalidDataException>(() => DailyMaterialFarmOptions.Parse("天空顶", "地狱四", target));

    private sealed class Battle : IDailyMaterialBattle
    {
        public List<string> Actions { get; } = [];
        public List<int> CheckedAfter { get; } = [];
        public Queue<DailyMaterialQuota> Quotas { get; } = new();
        public Queue<DailyMaterialRoundResult> Results { get; } = new();
        public int Collected, Replays, Exits;
        public bool FailPickup;
        public bool InitialMaterials;
        public Action? OnCollected;
        public Task EnterAsync(string map, string difficulty, CancellationToken token)
        { Actions.Add("enter:" + map + ":" + difficulty); return Task.CompletedTask; }
        public Task<DailyMaterialRoundResult> FightAndCollectAsync(bool checkQuotaWhenEmpty, CancellationToken token)
        {
            if (FailPickup) throw new InvalidOperationException("虚构：未确认拾取");
            Collected++; Actions.Add("collect"); OnCollected?.Invoke();
            if (!checkQuotaWhenEmpty || InitialMaterials) return Task.FromResult(DailyMaterialRoundResult.Collected);
            CheckedAfter.Add(Collected);
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : Quotas.Count > 0 && Quotas.Dequeue().IsFull
                ? DailyMaterialRoundResult.QuotaFull : DailyMaterialRoundResult.Collected);
        }
        public Task ReplayAsync(CancellationToken token) { Replays++; Actions.Add("replay"); return Task.CompletedTask; }
        public Task ExitAsync(CancellationToken token) { Exits++; Actions.Add("exit"); return Task.CompletedTask; }
    }
}
