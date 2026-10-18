using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BqtjLauncher.Application;

/// <summary>可移植脚本定义；只描述允许的视觉步骤，不包含账号凭据或任意程序入口。</summary>
public sealed class ScriptDefinition
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Entry { get; set; } = "";
    public List<ScriptStep> Flow { get; set; } = [];
    [JsonIgnore] public string Directory { get; set; } = "";
    [JsonIgnore] public Dictionary<string, byte[]> Images { get; } = [];
}

/// <summary>线性流程中的识别、输入或等待；坐标采用950×600游戏画面，ROI为x/y/宽/高。</summary>
public sealed class ScriptStep
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Text { get; set; } = "";
    public string Image { get; set; } = "";
    public int[] Roi { get; set; } = [0, 0, 950, 600];
    public int X { get; set; }
    public int Y { get; set; }
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public int Milliseconds { get; set; } = 1000;
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>用户方案明文保存；账号ID和存档号是执行身份，名称仅用于显示。</summary>
public sealed class ScriptPlan
{
    public int Version { get; set; } = 1;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "新方案";
    public string ScriptId { get; set; } = "official-daily";
    public decimal Speed { get; set; } = 2;
    public int ConcurrentAccounts { get; set; }
    public List<ScriptPlanTask> Tasks { get; set; } = [];
    public string Source { get; set; } = "";
    // 账号自带日常只按稳定ID关联，用于主面板的当前账号入口，不存账号密码。
    public Guid? DefaultAccountId { get; set; }
    public bool IsBuiltIn { get; set; }
    public bool ShowOnDashboard { get; set; } = true;
    // 旧方案默认仍可被调用；新方案通过源码开头的设置明确选择用途。
    public bool IsSubPlan { get; set; } = true;
    [JsonIgnore]
    public string ListDisplayName => Name + (IsBuiltIn ? "（内置模板）" : "")
        + (DefaultAccountId is { } account ? " [" + account.ToString("N")[..6] + "]" : "");
    public string? ConstantsSource { get; set; }
    public Dictionary<string, Guid> AccountBindings { get; set; } = [];
    public Dictionary<string, Guid> PlanBindings { get; set; } = [];
    public Dictionary<string, string> Constants { get; set; } = [];
    public override string ToString() => ListDisplayName;
}

/// <summary>同账号存档按列表顺序执行；关窗选项逐项保存，不因队列分组而改变。</summary>
public sealed class ScriptPlanTask : INotifyPropertyChanged
{
    public Guid ProfileId { get; set; }
    public string AccountName { get; set; } = "";
    public int SaveNumber { get; set; } = 1;
    private string _saveName = "";
    /// <summary>存档名被重新识别或手动校正时，通知列表刷新这一列，不重建队列和运行快照。</summary>
    public string SaveName
    {
        get => _saveName;
        set
        {
            if (_saveName == value) return;
            _saveName = value;
            PropertyChanged?.Invoke(this, new(nameof(SaveName)));
            PropertyChanged?.Invoke(this, new(nameof(Label)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool CloseAfterFinish { get; set; } = true;
    [JsonIgnore] public string AccountDisplay => $"{AccountName} [{ProfileId.ToString("N")[..6]}]";
    [JsonIgnore] public string Label => $"{AccountDisplay}－存档{SaveNumber}－{SaveName}";
}

public sealed record ScriptCatalogItem(string Path, ScriptDefinition? Definition, string? Error)
{
    public string Name => Definition?.Name ?? System.IO.Path.GetFileName(Path);
    public string State => Error ?? "可运行";
}
public sealed record ScriptAccount(Guid Id, string Name, string Username)
{
    public override string ToString() => string.IsNullOrEmpty(Username) ? $"{Name} [{Id.ToString("N")[..6]}]" : $"{Name} · {Username}";
}
public sealed record SaveSlot(int Number, string Name, bool NeedsReview = false);
public enum ScriptRunState { Waiting, Running, Paused, Completed, Stopped }
public sealed record ScriptRunUpdate(Guid ProfileId, int TaskIndex, string Label, ScriptRunState State, string Stage)
{
    public string StateDisplay => State switch
    {
        ScriptRunState.Waiting => "等待执行",
        ScriptRunState.Running => "执行中",
        ScriptRunState.Paused => "已暂停",
        ScriptRunState.Completed => "已完成",
        _ => "已停止"
    };
}

/// <summary>脚本面板只读账号身份，不读取密码。</summary>
public interface IScriptAccounts
{
    Task<IReadOnlyList<ScriptAccount>> ListScriptAccountsAsync();
}

/// <summary>统一配置格式与快照；运行期间文件修改不改变已经开始的任务。</summary>
public static class ScriptJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;

    /// <summary>保存只校验文档结构；执行时才要求语句完整可编译，允许持久保存未完成草稿。</summary>
    public static void Validate(ScriptPlan plan, bool requireExecutable = true)
    {
        if (plan.Version is not (1 or 2) || plan.Id == Guid.Empty || string.IsNullOrWhiteSpace(plan.Name)
            || string.IsNullOrWhiteSpace(plan.ScriptId)) throw new InvalidDataException("方案版本、标识或名称无效。");
        _ = Domain.SpeedMultiplier.Create(plan.Speed);
        if (plan.ConcurrentAccounts < 0) throw new InvalidDataException("并发数必须为0（不限制）或正整数。");
        if (plan.Version == 2)
        {
            if (requireExecutable && string.IsNullOrWhiteSpace(plan.Source)) throw new InvalidDataException("请插入至少一条语句。");
            return;
        }
        if (plan.Tasks is null || plan.Tasks.Count == 0) throw new InvalidDataException("请添加至少一个账号存档。");
        if (plan.Tasks.Any(task => task is null || task.ProfileId == Guid.Empty || task.SaveNumber is < 1 or > 8))
            throw new InvalidDataException("账号ID或存档号无效，存档号应为1～8。");
        if (plan.Tasks.GroupBy(task => (task.ProfileId, task.SaveNumber)).Any(group => group.Count() > 1))
            throw new InvalidDataException("同一方案中请勿重复添加同账号的同一个存档。");
    }
}
