namespace BqtjLauncher.Application;

/// <summary>账号附带的单账号日常及首次选档回写；源码始终是之后编辑和执行的唯一依据。</summary>
public static class AccountDailyPlans
{
    public const string PendingSave = "待选择存档";

    /// <summary>按账号ID生成未选档的日常，首次运行再由界面补齐存档编号。</summary>
    public static ScriptPlan Create(ScriptAccount account) => new()
    {
        Version = 2,
        Name = account.Name + "－日常",
        DefaultAccountId = account.Id,
        IsSubPlan = true,
        ShowOnDashboard = true,
        AccountBindings = new() { ["本账号"] = account.Id },
        Source = """
            # 此方案只绑定当前账号的ID，账号改名不改变绑定。
            # 第一次运行会弹出两列四行存档选择，选择后直接写入下面的日常操作语句。
            # 也可把“待选择存档”手动改为存档1～存档8；结束保留窗口。
            方案设置,子方案,是
            方案设置,面板可见,是
            日常操作,本账号,待选择存档
            """
    };

    /// <summary>同一方案、同一账号的待选行共用一次选择；同一子方案被重复调用也不重复询问。</summary>
    public static IReadOnlyList<ScriptSaveSelection> RequiredSelections(CompiledScript script)
        => script.Commands.Where(command => command.SaveNumber == 0 && command.Operation is "日常操作" or "启动存档")
            .GroupBy(command => (command.PlanId, command.Account.Id))
            .Select(group => new ScriptSaveSelection(group.Key.PlanId, group.First().Path, group.First().Account,
                group.Select(command => command.Line).Distinct().ToArray())).ToArray();

    /// <summary>只替换编译时确定的存档参数，保留注释、绑定与其他行；取消时不会调用此方法。</summary>
    public static ScriptPlan ApplySelection(ScriptPlan plan, ScriptSaveSelection selection, int number)
    {
        if (number is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(number));
        if (plan.Id != selection.PlanId) throw new InvalidOperationException("选档目标不是本方案。");
        var result = ScriptJson.Clone(plan);
        var lines = result.Source.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        foreach (var line in selection.Lines)
        {
            var words = ScriptSyntax.Parse(lines[line - 1]).Single().Words;
            words[2] = "存档" + number;
            lines[line - 1] = ScriptSyntax.Statement(words);
        }
        result.Source = string.Join("\n", lines);
        return result;
    }
}

/// <summary>启动前的选档请求，只包含方案、账号身份和待配置行，不包含凭据。</summary>
public sealed record ScriptSaveSelection(Guid PlanId, string PlanName, ScriptAccount Account, IReadOnlyList<int> Lines);
