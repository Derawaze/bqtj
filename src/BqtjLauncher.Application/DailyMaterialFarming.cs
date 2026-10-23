namespace BqtjLauncher.Application;

/// <summary>单次刷关参数；地图仅天空顶，难度保留游戏原文交给固定布局，null次数表示两组材料刷满。</summary>
public sealed record DailyMaterialFarmOptions(string Map, string Difficulty, int? Runs, bool HasPresident = false)
{
    public const string DefaultDifficulty = "地狱四";

    public static DailyMaterialFarmOptions Parse(string map, string difficulty, string target)
    {
        if (map != "天空顶") throw new InvalidDataException("当前刷材料只支持天空顶。");
        // 难度名称由游戏适配器映射固定按钮位置，未登记的名称在任何游戏操作前拒绝。
        if (string.IsNullOrWhiteSpace(difficulty)) throw new InvalidDataException("请填写游戏中的关卡难度名称。");
        int? runs = null;
        if (target != "刷满")
        {
            var value = target.EndsWith('次') ? target[..^1] : target;
            if (!int.TryParse(value, out var count) || count <= 0) throw new InvalidDataException("刷取目标为刷满或正整数次数，例如8次。");
            runs = count;
        }
        return new(map, difficulty, runs);
    }
}

/// <summary>区分已确认上限与无法读取的空屏；空屏可能是遮挡，不能据此结束刷满。</summary>
public enum DailyMaterialRoundResult { Collected, QuotaFull, UnconfirmedEmpty }

/// <summary>游戏适配边界：战斗方法只有在本轮完成且目标掉落拾取确认后才能返回。</summary>
public interface IDailyMaterialBattle
{
    Task EnterAsync(string map, string difficulty, CancellationToken token);
    /// <summary>允许查询后，无总统仅首屏无材料时查；有总统等自动拾取后每轮查，两种模式都不能把读数失败当已满。</summary>
    Task<DailyMaterialRoundResult> FightAndCollectAsync(bool checkQuotaWhenEmpty, CancellationToken token);
    Task ReplayAsync(CancellationToken token);
    Task ExitAsync(CancellationToken token);
}

/// <summary>共用刷取调度；关卡适配器报告当轮结果，统一处理查询时机、重玩和退出。</summary>
public sealed class DailyMaterialFarming(IDailyMaterialBattle battle)
{
    // 按当前难度的实际限额读取结束，不按掉落数量或连续空屏估算已满。
    public const int FirstQuotaCheckRound = 10;

    public async Task<int> RunAsync(DailyMaterialFarmOptions options, Action<string> stage, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        stage("进入寒光市－" + options.Map + "－正常－" + options.Difficulty);
        await battle.EnterAsync(options.Map, options.Difficulty, token).ConfigureAwait(false);
        var completed = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            stage($"第{completed + 1}次：等待自动战斗完成" + (options.HasPresident ? "，总统自动拾取" : "并拾取关键掉落"));
            var outcome = await battle.FightAndCollectAsync(options.Runs is null && completed + 1 >= FirstQuotaCheckRound, token).ConfigureAwait(false);
            completed++;
            token.ThrowIfCancellationRequested();
            // 顶部抽奖栏可连续遮住材料；读数失败与任何次数的空屏都不能替代双限额证据。
            if (outcome == DailyMaterialRoundResult.UnconfirmedEmpty) stage("限额未确认，空屏不能判断刷满，继续刷取");
            var finished = options.Runs is { } count ? completed >= count : outcome == DailyMaterialRoundResult.QuotaFull;
            token.ThrowIfCancellationRequested();
            if (finished)
            {
                stage(options.Runs is not null ? $"已完成{completed}次，退出关卡" : "两项材料均已刷满，退出关卡");
                await battle.ExitAsync(token).ConfigureAwait(false);
                return completed;
            }
            stage("重玩关卡并确认");
            await battle.ReplayAsync(token).ConfigureAwait(false);
        }
    }
}
