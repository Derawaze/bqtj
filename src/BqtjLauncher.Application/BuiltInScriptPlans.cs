namespace BqtjLauncher.Application;

/// <summary>随启动器提供的可编辑示例；更新只迁移未配置的旧默认内容，保留用户自己的修改。</summary>
public static class BuiltInScriptPlans
{
    public static readonly Guid RetiredReadNamesId = Guid.Parse("1de341e0-296e-4ed2-a35b-3f4ab9aa4102");
    public static readonly Guid GlobalConstantsId = Guid.Parse("1de341e0-296e-4ed2-a35b-3f4ab9aa4104");
    public const string Help = """
        # 方案设置说明：子方案=是时可被其他方案调用，也能单独运行；面板可见单独控制快捷列表。
        # 方案设置必须放在操作前；是/否按需修改，每项只能出现一次。
        # 日常操作会进入指定存档，点击助理，运行游戏内置「日常操作复制」并等待完成。
        # 最终冷却和保存由游戏脚本执行，启动器不会补点保存；同账号下一存档复用窗口。
        # 日常结束默认保留窗口，需要关闭时添加“关闭窗口,账号名称”。
        """;

    public static IReadOnlyList<ScriptPlan> Create() =>
    [
        new()
        {
            Id = Guid.Parse("1de341e0-296e-4ed2-a35b-3f4ab9aa4101"), Version = 2,
            Name = "单账号多档日常", IsBuiltIn = true, ShowOnDashboard = false,
            Source = Help + "\n" + """
                方案设置,子方案,是
                方案设置,面板可见,否
                日常操作,账号名称,存档1
                日常操作,账号名称,存档2
                关闭窗口,账号名称
                """
        },
        new()
        {
            Id = Guid.Parse("1de341e0-296e-4ed2-a35b-3f4ab9aa4103"), Version = 2,
            Name = "并行调用两个子方案", IsBuiltIn = true, ShowOnDashboard = false,
            Source = Help + "\n" + """
                # 先复制“单账号多档日常”为下面两个名称，分别填好各自的账号和存档。
                # 并行分支不能使用同一个账号；每个子方案内的多个存档按行顺序执行。
                方案设置,子方案,是
                方案设置,面板可见,否
                并行开始
                调用,单账号多档日常一
                调用,单账号多档日常二
                并行结束
                """
        },
        new()
        {
            Id = GlobalConstantsId, Version = 2, Name = "全局常量示例",
            IsBuiltIn = true, ShowOnDashboard = false,
            Source = Help + "\n" + """
                # 在右侧展开“全局常量 / 方案覆盖”，全局默认值每行填写一项：
                # 示例账号=账号名称
                # 示例存档=存档1
                # 点击“保存全局常量”，所有方案都可使用 $示例账号 和 $示例存档。
                # 把“账号名称”换成你的账号显示名称；同名账号请从选择器插入并绑定。
                # 本方案同名常量优先于全局值；修改不会影响已开始的运行。
                方案设置,子方案,是
                方案设置,面板可见,否
                日常操作,$示例账号,$示例存档
                """
        }
    ];

    /// <summary>已编辑模板只补说明；旧占位默认内容改为当前示例，不覆盖真实配置或绑定。</summary>
    public static ScriptPlan Upgrade(ScriptPlan existing, ScriptPlan current)
    {
        var result = ScriptJson.Clone(existing);
        var body = "";
        try { body = string.Join("\n", ScriptSyntax.Parse(existing.Source).Where(line => line.Words[0] != "方案设置").Select(line => ScriptSyntax.Statement(line.Words))); }
        catch (InvalidDataException) { /* 错误草稿也保留，只在开头补上说明。 */ }
        var oldDefault = !existing.Source.Contains('#') && existing.AccountBindings.Count == 0 && existing.PlanBindings.Count == 0
            && (existing.ConstantsSource is null || existing.ConstantsSource.Trim() is "" or "主号=请绑定账号")
            && existing.Constants.All(pair => pair.Key == "主号" && pair.Value == "请绑定账号")
            && (body == "日常操作,$主号,存档1\n日常操作,$主号,存档2\n关闭窗口,$主号"
                || body == "并行开始\n调用,主号日常\n调用,小号日常\n并行结束");
        if (oldDefault)
        {
            result.Source = current.Source;
            result.Constants.Clear();
            result.ConstantsSource = null;
        }
        else if (!result.Source.StartsWith("# 方案设置说明：", StringComparison.Ordinal)) result.Source = Help + "\n" + result.Source;
        return result;
    }
}
