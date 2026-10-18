using System.Text.Json;
using BqtjLauncher.Application;

namespace BqtjLauncher.Infrastructure;

/// <summary>发现脚本包并原子保存用户方案；文件变化只刷新目录，绝不自动执行脚本。</summary>
public sealed class ScriptFileStore : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Timer _debounce;
    private static readonly string[] StepTypes = ["officialDaily", "enterSave", "waitText", "clickText",
        "waitImage", "clickImage", "click", "wait", "waitOfficialCompletion"];
    public string Root { get; }
    public event EventHandler? Changed;

    public ScriptFileStore(string root)
    {
        Root = System.IO.Path.GetFullPath(root);
        Directory.CreateDirectory(System.IO.Path.Combine(Root, "plans"));
        _debounce = new(_ => Changed?.Invoke(this, EventArgs.Empty), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new(Root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Deleted += OnChanged;
        _watcher.Renamed += OnChanged;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnChanged(object sender, FileSystemEventArgs args) => _debounce.Change(300, Timeout.Infinite);

    /// <summary>全局常量独立保存，覆盖更新和方案复制都不替换用户配置。</summary>
    public string ReadGlobalConstants()
    {
        var path = System.IO.Path.Combine(Root, "config", "constants.txt");
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    /// <summary>先校验再原子替换，错误配置不覆盖已经保存的全局值。</summary>
    public void SaveGlobalConstants(string source)
    {
        _ = ScriptSyntax.ParseConstants(source);
        var directory = System.IO.Path.Combine(Root, "config");
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, "constants.txt");
        File.WriteAllText(path + ".tmp", source);
        File.Move(path + ".tmp", path, overwrite: true);
    }
    public IReadOnlyList<ScriptCatalogItem> ListScripts()
    {
        var files = Directory.GetFiles(Root, "*.json").Concat(
            Directory.GetDirectories(Root).Where(path => !System.IO.Path.GetFileName(path).Equals("plans", StringComparison.OrdinalIgnoreCase))
            .Select(path => System.IO.Path.Combine(path, "script.json")).Where(File.Exists)).Order();
        var result = new List<ScriptCatalogItem>();
        foreach (var path in files)
        {
            try
            {
                var definition = JsonSerializer.Deserialize<ScriptDefinition>(File.ReadAllText(path), ScriptJson.Options)
                    ?? throw new InvalidDataException("脚本定义为空。");
                definition.Directory = System.IO.Path.GetDirectoryName(path)!;
                ValidateDefinition(definition);
                result.Add(new(path, definition, null));
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
            { result.Add(new(path, null, error is JsonException ? "JSON格式无效。" : error.Message)); }
        }
        var duplicateIds = result.Where(item => item.Definition is not null).GroupBy(item => item.Definition!.Id)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();
        return result.Select(item => item.Definition is not null && duplicateIds.Contains(item.Definition.Id)
            ? item with { Error = "脚本标识重复，请修改id。" } : item).ToArray();
    }

    /// <summary>只接纳明确支持的步骤；图片位置必须留在脚本包内。</summary>
    public static void ValidateDefinition(ScriptDefinition definition)
    {
        if (definition.Version != 1) throw new InvalidDataException("仅支持version=1的脚本。");
        if (string.IsNullOrWhiteSpace(definition.Id) || string.IsNullOrWhiteSpace(definition.Name))
            throw new InvalidDataException("脚本缺少id或name。");
        if (definition.Flow is null || definition.Flow.Count == 0 || definition.Flow.Any(step => step is null))
            throw new InvalidDataException("flow必须包含有效步骤。");
        if (!definition.Flow.Any(step => step.Id == definition.Entry))
            throw new InvalidDataException("entry必须指向flow中的步骤id。");
        if (definition.Flow.Any(step => string.IsNullOrWhiteSpace(step.Id)) || definition.Flow.GroupBy(step => step.Id).Any(group => group.Count() > 1))
            throw new InvalidDataException("步骤id不能为空或重复。");
        foreach (var step in definition.Flow)
        {
            if (!StepTypes.Contains(step.Type)) throw new InvalidDataException($"步骤 {step.Id} 的type不受支持。");
            if (step.TimeoutSeconds <= 0 || step.Milliseconds < 0 || step.Roi is null || step.Roi.Length != 4
                || step.Roi[0] < 0 || step.Roi[1] < 0 || step.Roi[2] <= 0 || step.Roi[3] <= 0
                || (long)step.Roi[0] + step.Roi[2] > 950 || (long)step.Roi[1] + step.Roi[3] > 600)
                throw new InvalidDataException($"步骤 {step.Id} 的区域或等待参数无效。");
            if (step.Type is "waitText" or "clickText" && string.IsNullOrWhiteSpace(step.Text))
                throw new InvalidDataException($"步骤 {step.Id} 缺少text。");
            if (step.Type is "waitImage" or "clickImage" && !File.Exists(ResolveImage(definition, step.Image)))
                throw new InvalidDataException($"步骤 {step.Id} 的图片不存在。");
            if (step.Type == "click" && (step.X is < 0 or >= 950 || step.Y is < 0 or >= 600))
                throw new InvalidDataException($"步骤 {step.Id} 的点击坐标超出画面。");
        }
    }

    public static string ResolveImage(ScriptDefinition definition, string image)
    {
        if (string.IsNullOrWhiteSpace(image) || System.IO.Path.IsPathRooted(image))
            throw new InvalidDataException("图片应为脚本包内的相对路径。");
        var root = System.IO.Path.GetFullPath(definition.Directory) + System.IO.Path.DirectorySeparatorChar;
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, image));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("图片不能位于脚本包外。");
        return path;
    }

    public IReadOnlyList<ScriptPlan> ListPlans(out IReadOnlyList<string> errors)
    {
        var plans = new List<ScriptPlan>();
        var problems = new List<string>();
        foreach (var path in Directory.GetFiles(System.IO.Path.Combine(Root, "plans"), "*.json").Order())
        {
            try
            {
                var plan = JsonSerializer.Deserialize<ScriptPlan>(File.ReadAllText(path), ScriptJson.Options)
                    ?? throw new InvalidDataException("方案为空。");
                ScriptJson.Validate(plan, requireExecutable: false);
                ScriptSyntax.ApplySettings(plan, strict: false);
                plans.Add(plan);
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or IOException)
            { problems.Add(System.IO.Path.GetFileName(path) + "：" + (error is JsonException ? "JSON格式无效。" : error.Message)); }
        }
        errors = problems;
        return plans;
    }

    /// <summary>示例按版本初始化；保留已编辑/已删除方案，仅移除退役的读取名称模板。</summary>
    public void EnsureBuiltInPlans()
    {
        var directory = System.IO.Path.Combine(Root, "plans");
        var marker = System.IO.Path.Combine(directory, ".built-ins-v2");
        if (File.Exists(marker)) return;
        var firstUse = !File.Exists(System.IO.Path.Combine(directory, ".built-ins-v1"));
        var existing = ListPlans(out _).ToDictionary(plan => plan.Id);
        foreach (var plan in BuiltInScriptPlans.Create())
        {
            if (existing.TryGetValue(plan.Id, out var old))
            {
                if (old.IsBuiltIn) WritePlan(BuiltInScriptPlans.Upgrade(old, plan));
            }
            else if (firstUse || plan.Id == BuiltInScriptPlans.GlobalConstantsId) WritePlan(plan);
        }
        if (existing.TryGetValue(BuiltInScriptPlans.RetiredReadNamesId, out var retired) && retired.IsBuiltIn) DeletePlan(retired.Id);
        File.WriteAllText(System.IO.Path.Combine(directory, ".built-ins-v1"), "1\n");
        File.WriteAllText(marker, "2\n");
    }

    /// <summary>账号首次出现时附带一份可编辑日常；初始化记录避免删除后又自动生成。</summary>
    public bool EnsureAccountDailyPlans(IReadOnlyList<ScriptAccount> accounts)
    {
        var directory = System.IO.Path.Combine(Root, "config");
        var marker = System.IO.Path.Combine(directory, "account-daily-created.json");
        var created = File.Exists(marker)
            ? JsonSerializer.Deserialize<HashSet<Guid>>(File.ReadAllText(marker), ScriptJson.Options)! : [];
        var existing = ListPlans(out _).Where(plan => plan.DefaultAccountId is not null).Select(plan => plan.DefaultAccountId!.Value).ToHashSet();
        var changed = false;
        foreach (var account in accounts)
        {
            if (!created.Add(account.Id)) continue;
            if (!existing.Contains(account.Id)) WritePlan(AccountDailyPlans.Create(account));
            changed = true;
        }
        if (changed)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(marker + ".tmp", JsonSerializer.Serialize(created, ScriptJson.Options));
            File.Move(marker + ".tmp", marker, overwrite: true);
        }
        return changed;
    }
    public void SavePlan(ScriptPlan plan, bool updateReferences = false)
    {
        ScriptJson.Validate(plan, requireExecutable: false);
        ScriptSyntax.ApplySettings(plan, strict: false);
        var existing = ListPlans(out _);
        var old = existing.SingleOrDefault(candidate => candidate.Id == plan.Id);
        var changes = new List<ScriptPlan>();
        if (updateReferences && old is not null && old.Name != plan.Name)
        {
            foreach (var other in existing.Where(candidate => candidate.Id != plan.Id && candidate.PlanBindings.ContainsValue(plan.Id)))
            {
                RenameReferences(other, plan.Id, plan.Name, true);
                changes.Add(other);
            }
        }
        // 先检查所有引用冲突，再写文件；保留稳定ID，未选择同步时旧别名仍可正常调用。
        foreach (var other in changes) WritePlan(other);
        WritePlan(plan);
    }

    /// <summary>账号改名可由用户选择同步引用文字；不改变账号ID、存档号或本轮运行快照。</summary>
    public void RenameAccountReferences(Guid id, string name)
    {
        var plans = ListPlans(out _).Where(plan => plan.AccountBindings.ContainsValue(id)).ToArray();
        foreach (var plan in plans) RenameReferences(plan, id, name, false);
        foreach (var plan in plans) WritePlan(plan);
    }

    private static void RenameReferences(ScriptPlan plan, Guid id, string name, bool isPlan)
    {
        var bindings = isPlan ? plan.PlanBindings : plan.AccountBindings;
        if (bindings.TryGetValue(name, out var conflict) && conflict != id)
            throw new InvalidDataException("新名称与已有引用别名冲突：" + name);
        foreach (var alias in bindings.Where(pair => pair.Value == id).Select(pair => pair.Key).ToArray())
            plan.Source = ScriptSyntax.Rename(plan.Source, alias, name, isPlan);
        // 旧别名仍作为稳定ID的兼容入口，常量或用户手写引用不会被改绑到同名对象。
        bindings[name] = id;
    }

    private void WritePlan(ScriptPlan plan)
    {
        var path = System.IO.Path.Combine(Root, "plans", plan.Id.ToString("N") + ".json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(plan, ScriptJson.Options));
        File.Move(temporary, path, overwrite: true);
    }

    public IReadOnlyDictionary<Guid, IReadOnlyList<SaveSlot>> ReadSaveNames()
    {
        var directory = System.IO.Path.Combine(Root, "catalog");
        if (!Directory.Exists(directory)) return new Dictionary<Guid, IReadOnlyList<SaveSlot>>();
        return Directory.GetFiles(directory, "*.json")
            .Where(path => Guid.TryParse(System.IO.Path.GetFileNameWithoutExtension(path), out _))
            .ToDictionary(path => Guid.Parse(System.IO.Path.GetFileNameWithoutExtension(path)),
                path => (IReadOnlyList<SaveSlot>)(JsonSerializer.Deserialize<SaveSlot[]>(File.ReadAllText(path), ScriptJson.Options) ?? []));
    }

    /// <summary>仅保存账号ID和角色显示名称，不包含平台凭据；覆盖更新保留catalog目录。</summary>
    public void SaveNames(Guid id, IReadOnlyList<SaveSlot> slots)
    {
        var directory = System.IO.Path.Combine(Root, "catalog");
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, id.ToString("N") + ".json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(slots, ScriptJson.Options));
        File.Move(path + ".tmp", path, true);
    }

    public void DeletePlan(Guid id) => File.Delete(System.IO.Path.Combine(Root, "plans", id.ToString("N") + ".json"));

    public void Dispose() { _watcher.Dispose(); _debounce.Dispose(); }
}
