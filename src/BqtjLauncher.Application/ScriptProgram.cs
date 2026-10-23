using System.Text;

namespace BqtjLauncher.Application;

/// <summary>一条中文语句的原始位置；CSV参数允许带逗号的名称，不通过字符串全局替换改引用。</summary>
public sealed record ScriptStatement(int Line, string[] Words);

/// <summary>中文CSV语句的词法与定向改名工具，编辑器、校验器和引用更新共用同一解释。</summary>
public static class ScriptSyntax
{
    public static readonly string[] Operations = ["日常操作", "读取存档名称", "启动存档", "刷取每日材料", "退出存档", "关闭窗口", "调用", "并行开始", "并行结束", "方案设置"];
    public static string Quote(string value) => value.Contains(',') || value.Contains('"') || value != value.Trim()
        ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    public static string Statement(params string[] words) => string.Join(",", words.Select(Quote));

    /// <summary>仅解析文本，不接触账号库或游戏；错误保留行号供编辑器定位。</summary>
    public static IReadOnlyList<ScriptStatement> Parse(string source)
    {
        var result = new List<ScriptStatement>();
        var lines = source.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var fields = new List<string>();
            var field = new StringBuilder();
            var quoted = false;
            var closed = false;
            for (var offset = 0; offset < line.Length; offset++)
            {
                var value = line[offset];
                if (value == '"')
                {
                    if (quoted && offset + 1 < line.Length && line[offset + 1] == '"') { field.Append('"'); offset++; }
                    else if (quoted) { quoted = false; closed = true; }
                    else if (field.ToString().Trim().Length == 0 && !closed) { field.Clear(); quoted = true; }
                    else throw Error(index + 1, "引号位置无效。");
                }
                else if (value == ',' && !quoted) { fields.Add(field.ToString().Trim()); field.Clear(); closed = false; }
                else if (closed && !char.IsWhiteSpace(value)) throw Error(index + 1, "引号后只能接逗号。");
                else field.Append(value);
            }
            if (quoted) throw Error(index + 1, "双引号没有闭合。");
            fields.Add(field.ToString().Trim());
            result.Add(new(index + 1, fields.ToArray()));
        }
        return result;
    }

    /// <summary>常量文本可原样保存；编译时再检查格式，使错误草稿不会因转换成字典丢失。</summary>
    public static Dictionary<string, string> ParseConstants(string source)
    {
        var result = new Dictionary<string, string>();
        foreach (var line in source.Replace("\r", "", StringComparison.Ordinal).Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            var parts = line.Split('=', 2);
            if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1])
                || !result.TryAdd(parts[0].Trim(), parts[1].Trim()))
                throw new InvalidDataException("常量格式为 名称=值，名称不能重复；不需要时可以留空。");
        }
        return result;
    }

    public static InvalidDataException Error(int line, string message) => new($"第{line}行：{message}");

    /// <summary>开头的声明同步列表属性；保存错误草稿时只应用有效声明，编译时严格检查位置和重复项。</summary>
    public static void ApplySettings(ScriptPlan plan, bool strict = true)
    {
        if (plan.Version != 2) return;
        var started = false;
        var seen = new HashSet<string>();
        var lines = plan.Source.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            try
            {
                var statement = Parse(lines[index]).SingleOrDefault();
                if (statement is null) continue;
                var words = statement.Words;
                if (words[0] != "方案设置") { started = true; continue; }
                if (started || words.Length != 3 || words[1] is not ("子方案" or "面板可见")
                    || words[2] is not ("是" or "否") || !seen.Add(words[1]))
                    throw new InvalidDataException("方案设置须在开头，每项一次，例如 方案设置,子方案,是 或 方案设置,面板可见,否。");
                if (words[1] == "子方案") plan.IsSubPlan = words[2] == "是";
                else plan.ShowOnDashboard = words[2] == "是";
            }
            catch (InvalidDataException) when (!strict) { /* 草稿保留错误声明，执行前再报具体行号。 */ }
            catch (InvalidDataException error) { throw Error(index + 1, error.Message); }
        }
    }

    /// <summary>只更新参数位置，避免把同名账号、注释或其他子方案文字误替换。</summary>
    public static string Rename(string source, string oldName, string newName, bool planReference)
    {
        var lines = source.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            // 已保存草稿可以有语法错误；只重写能解析的引用行，其余保留原文供用户继续编辑。
            ScriptStatement? statement;
            try { statement = Parse(lines[index]).SingleOrDefault(); }
            catch (InvalidDataException) { continue; }
            if (statement is null) continue;
            var words = statement.Words;
            var isAccountOperation = words[0] is "日常操作" or "读取存档名称" or "启动存档" or "退出存档" or "关闭窗口"
                || (words[0] == "刷取每日材料" && words.Length == 6);
            if (words.Length < 2 || (planReference ? words[0] != "调用" : !isAccountOperation) || words[1] != oldName) continue;
            words[1] = newName;
            lines[index] = Statement(words);
        }
        return string.Join("\n", lines);
    }
}

/// <summary>编译后的树只包含冻结绑定与内置操作；顺序、并行和调用共用一个根运行实例。</summary>
public abstract record ScriptNode;
public sealed record ScriptBlock(bool Parallel, IReadOnlyList<ScriptNode> Children) : ScriptNode;
public sealed record ScriptCommand(int Index, int Line, string Path, string Operation, ScriptAccount Account,
    int SaveNumber, decimal Speed, ScriptDefinition? LegacyDefinition = null, string SaveName = "", Guid PlanId = default,
    DailyMaterialFarmOptions? Farming = null) : ScriptNode
{
    /// <summary>可选的游戏内官方脚本名；空值按“日常操作复制、日常操作”顺序选择。</summary>
    public string? OfficialScriptName { get; init; }
    /// <summary>短式操作继承前一启动语句的选档位置，首次回写不能误改地图或难度参数。</summary>
    public int SaveSelectionLine { get; init; } = Line;
    public string Label => $"{Path} · 第{Line}行 · {Operation}－{Account.Name}" + (SaveNumber == 0 ? "" : $"－存档{SaveNumber}") + (SaveName.Length == 0 ? "" : "－" + SaveName)
        + (Farming is null ? "" : $"－{Farming.Map}－{Farming.Difficulty}－{(Farming.Runs is { } runs ? runs + "次" : "刷满")}");
}
public sealed record CompiledScript(string Name, ScriptBlock Root, IReadOnlyList<ScriptCommand> Commands, int ConcurrentAccounts);

/// <summary>中文语句、账号/方案引用及并行占用的唯一编译入口；不会执行任何游戏操作。</summary>
public sealed class ScriptProgramCompiler(IReadOnlyList<ScriptPlan> plans, IReadOnlyList<ScriptAccount> accounts,
    IReadOnlyList<ScriptDefinition> definitions, IReadOnlyDictionary<Guid, IReadOnlyList<SaveSlot>>? saveNames = null,
    IReadOnlyDictionary<string, string>? globalConstants = null)
{
    // 每次编译固定全局默认值，方案内同名常量覆盖它；运行树不再访问可变的配置文件。
    private readonly Dictionary<string, string> _globalConstants = globalConstants is null ? [] : new(globalConstants);
    private readonly List<ScriptCommand> _commands = [];
    private readonly HashSet<Guid> _chain = [];
    private bool _allowSaveSelection;

    /// <summary>解析全部子方案并固定账号；首次可解析的文字绑定写回根草稿，供保存时持久化。</summary>
    public CompiledScript Compile(ScriptPlan root, bool allowSaveSelection = false)
    {
        _commands.Clear(); _chain.Clear();
        _allowSaveSelection = allowSaveSelection;
        var tree = CompilePlan(root, root.Name);
        if (_commands.Count == 0) throw new InvalidDataException("方案没有可执行操作。");
        return new(root.Name, tree, _commands.ToArray(), root.ConcurrentAccounts);
    }

    private ScriptBlock CompilePlan(ScriptPlan plan, string path)
    {
        ScriptJson.Validate(plan);
        if (!_chain.Add(plan.Id)) throw new InvalidDataException("子方案存在循环调用：" + path);
        try
        {
            if (plan.Version == 1) return CompileLegacy(plan, path);
            ScriptSyntax.ApplySettings(plan);
            if (plan.ConstantsSource is not null) plan.Constants = ScriptSyntax.ParseConstants(plan.ConstantsSource);
            var lines = ScriptSyntax.Parse(plan.Source).Where(line => line.Words[0] != "方案设置").ToArray();
            var position = 0;
            return ReadBlock(plan, path, lines, ref position, false, false);
        }
        finally { _chain.Remove(plan.Id); }
    }

    /// <summary>递归读取显式并行块；普通行保持原顺序，调用展开后再检查账号占用交集。</summary>
    private ScriptBlock ReadBlock(ScriptPlan plan, string path, IReadOnlyList<ScriptStatement> lines, ref int position, bool parallel, bool nested)
    {
        var children = new List<ScriptNode>();
        ScriptCommand? currentSave = null;
        while (position < lines.Count)
        {
            var statement = lines[position++];
            var words = statement.Words;
            var operation = words[0];
            if (operation == "并行结束")
            {
                RequireCount(statement, 1);
                if (!nested) throw ScriptSyntax.Error(statement.Line, "没有对应的并行开始。");
                return CheckParallel(new(parallel, children), statement.Line);
            }
            if (operation == "并行开始")
            {
                RequireCount(statement, 1);
                children.Add(ReadBlock(plan, path, lines, ref position, true, true));
                currentSave = null;
                continue;
            }
            if (!ScriptSyntax.Operations.Contains(operation)) throw ScriptSyntax.Error(statement.Line, "未知语句：" + operation);
            if (operation == "调用")
            {
                RequireCount(statement, 2);
                var name = ResolveConstant(plan, words[1], statement.Line);
                ScriptPlan? child;
                if (plan.PlanBindings.TryGetValue(name, out var id)) child = plans.SingleOrDefault(candidate => candidate.Id == id);
                else
                {
                    var matches = plans.Where(candidate => candidate.Name == name).ToArray();
                    if (matches.Length > 1) throw ScriptSyntax.Error(statement.Line, "子方案重名，请从右侧选择并插入。");
                    child = matches.SingleOrDefault();
                    if (child is not null) plan.PlanBindings[name] = child.Id;
                }
                if (child is null) throw ScriptSyntax.Error(statement.Line, "子方案不存在：" + name);
                child = ScriptJson.Clone(child);
                ScriptSyntax.ApplySettings(child);
                if (!child.IsSubPlan) throw ScriptSyntax.Error(statement.Line, "该方案未设为子方案：" + name);
                children.Add(CompilePlan(child, path + " / " + name));
                currentSave = null;
                continue;
            }
            if (operation == "刷取每日材料" && words.Length == 4)
            {
                if (parallel || currentSave is null)
                    throw ScriptSyntax.Error(statement.Line, "短式刷材料语句前需在同一串行方案内启动存档；也可显式填写账号、存档。");
                var options = ReadFarming(plan, statement, 1);
                var farming = Add(statement.Line, path, operation, currentSave.Account, currentSave.SaveNumber, plan.Speed, planId: plan.Id,
                    farming: options, selectionLine: currentSave.SaveSelectionLine);
                children.Add(farming);
                continue;
            }
            var withSave = operation is "日常操作" or "启动存档" or "刷取每日材料";
            RequireCount(statement, operation == "刷取每日材料" ? 6 : operation == "日常操作" && words.Length == 4 ? 4 : withSave ? 3 : 2);
            var accountName = ResolveConstant(plan, words[1], statement.Line);
            ScriptAccount? account;
            if (plan.AccountBindings.TryGetValue(accountName, out var accountId)) account = accounts.SingleOrDefault(candidate => candidate.Id == accountId);
            else
            {
                var matches = accounts.Where(candidate => candidate.Name == accountName).ToArray();
                if (matches.Length > 1) throw ScriptSyntax.Error(statement.Line, "账号重名，请从右侧选择明确的账号后插入。");
                account = matches.SingleOrDefault();
                if (account is not null) plan.AccountBindings[accountName] = account.Id;
            }
            if (account is null) throw ScriptSyntax.Error(statement.Line, "账号不存在或已删除：" + accountName);
            var number = 0;
            if (withSave)
            {
                var save = ResolveConstant(plan, words[2], statement.Line);
                if (save == AccountDailyPlans.PendingSave)
                {
                    if (!_allowSaveSelection) throw ScriptSyntax.Error(statement.Line, "首次运行需要选择存档，请点击运行方案。");
                }
                else if (!int.TryParse(save.StartsWith("存档", StringComparison.Ordinal) ? save[2..] : save, out number) || number is < 1 or > 8)
                    throw ScriptSyntax.Error(statement.Line, "存档必须为存档1～存档8。");
            }
            var command = Add(statement.Line, path, operation, account, number, plan.Speed, planId: plan.Id,
                farming: operation == "刷取每日材料" ? ReadFarming(plan, statement, 3) : null,
                officialScriptName: operation == "日常操作" && words.Length == 4 ? ResolveConstant(plan, words[3], statement.Line) : null);
            children.Add(command);
            if (withSave && !parallel) currentSave = command;
            else if (currentSave?.Account.Id == account.Id) currentSave = null;
        }
        if (nested) throw new InvalidDataException(path + "：并行开始缺少并行结束。");
        return CheckParallel(new(parallel, children), 1);
    }

    private DailyMaterialFarmOptions ReadFarming(ScriptPlan plan, ScriptStatement statement, int offset)
    {
        try
        {
            return DailyMaterialFarmOptions.Parse(ResolveConstant(plan, statement.Words[offset], statement.Line),
                ResolveConstant(plan, statement.Words[offset + 1], statement.Line), ResolveConstant(plan, statement.Words[offset + 2], statement.Line));
        }
        catch (InvalidDataException error) { throw ScriptSyntax.Error(statement.Line, error.Message); }
    }

    private static void RequireCount(ScriptStatement statement, int count)
    {
        if (statement.Words.Length != count || statement.Words.Any(string.IsNullOrWhiteSpace))
            throw ScriptSyntax.Error(statement.Line, $"{statement.Words[0]}需要{count - 1}个参数，用英文逗号分隔。");
    }

    private string ResolveConstant(ScriptPlan plan, string value, int line)
    {
        var visited = new HashSet<string>();
        while (value.StartsWith('$'))
        {
            if (!visited.Add(value) || (!plan.Constants.TryGetValue(value[1..], out var resolved)
                && !_globalConstants.TryGetValue(value[1..], out resolved)))
                throw ScriptSyntax.Error(line, "常量缺失或循环引用：" + value);
            value = resolved;
        }
        return value;
    }

    private ScriptCommand Add(int line, string path, string operation, ScriptAccount account, int number, decimal speed, ScriptDefinition? definition = null, Guid planId = default,
        DailyMaterialFarmOptions? farming = null, int? selectionLine = null, string? officialScriptName = null)
    {
        var command = new ScriptCommand(_commands.Count, line, path, operation, account, number, speed, definition,
            saveNames?.GetValueOrDefault(account.Id)?.FirstOrDefault(slot => slot.Number == number)?.Name ?? "", planId, farming)
        { SaveSelectionLine = selectionLine ?? line, OfficialScriptName = officialScriptName };
        _commands.Add(command); return command;
    }

    private static ScriptBlock CheckParallel(ScriptBlock block, int line)
    {
        if (!block.Parallel) return block;
        var used = new HashSet<Guid>();
        foreach (var branch in block.Children)
        {
            var ids = Commands(branch).Select(command => command.Account.Id).Distinct();
            foreach (var id in ids)
                if (!used.Add(id)) throw ScriptSyntax.Error(line, "两个并行分支使用同一账号，请合并为同一个串行子方案。");
        }
        return block;
    }

    public static IEnumerable<ScriptCommand> Commands(ScriptNode node)
        => node is ScriptCommand command ? [command] : ((ScriptBlock)node).Children.SelectMany(Commands);

    /// <summary>v1配置保留原来的账号并行、账号内顺序及逐项关窗，不通过文本导入偷偷改变语义。</summary>
    private ScriptBlock CompileLegacy(ScriptPlan plan, string path)
    {
        var original = definitions.SingleOrDefault(candidate => candidate.Id == plan.ScriptId)
            ?? throw new InvalidDataException(path + "：旧版脚本不存在。");
        var definition = ScriptJson.Clone(original);
        definition.Directory = original.Directory;
        foreach (var image in original.Images) definition.Images[image.Key] = image.Value.ToArray();
        var branches = new List<ScriptNode>();
        foreach (var group in plan.Tasks.Select((task, index) => (task, index)).GroupBy(item => item.task.ProfileId))
        {
            var steps = new List<ScriptNode>();
            foreach (var (task, index) in group)
            {
                var account = accounts.SingleOrDefault(candidate => candidate.Id == task.ProfileId)
                    ?? throw new InvalidDataException(path + "：账号已删除。");
                steps.Add(Add(index + 1, path, "旧版脚本", account, task.SaveNumber, plan.Speed, definition));
                if (task.CloseAfterFinish) steps.Add(Add(index + 1, path, "关闭窗口", account, 0, plan.Speed));
            }
            branches.Add(new ScriptBlock(false, steps));
        }
        return new(true, branches);
    }
}
