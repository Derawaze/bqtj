using System.Collections.ObjectModel;
using System.IO;
using BqtjLauncher.Application;
using BqtjLauncher.Infrastructure;

namespace BqtjLauncher.Desktop;

/// <summary>面板与独立脚本窗口共用目录和调度器；窗口关闭不取消正在运行的方案。</summary>
public sealed class ScriptWorkspace : IAsyncDisposable
{
    private readonly ScriptFileStore _files;
    private readonly IScriptAccounts _accounts;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<Task> _scans = [];
    public ScriptCoordinator Coordinator { get; }
    private bool _preparing;
    private CancellationTokenSource? _preparationStop;
    public string PreparationStage { get; private set; } = "";
    public bool IsBusy => _preparing || Coordinator.IsRunning;
    public Func<ScriptSaveSelection, Task<int?>>? SelectSaveAsync { get; set; }
    public string Root => _files.Root;
    public ObservableCollection<ScriptCatalogItem> Scripts { get; } = [];
    public ObservableCollection<ScriptPlan> Plans { get; } = [];
    public ObservableCollection<ScriptPlan> QuickPlans { get; } = [];
    public string PlanErrors { get; private set; } = "";
    public event EventHandler? Changed;
    public event Action<Guid, IReadOnlyList<SaveSlot>>? SavesRead;
    public Dictionary<Guid, IReadOnlyList<SaveSlot>> SaveNames { get; } = [];

    public ScriptWorkspace(ScriptFileStore files, IScriptAccounts accounts, ScriptCoordinator coordinator)
    {
        _files = files; _accounts = accounts; Coordinator = coordinator;
        _files.Changed += FilesChanged;
        Coordinator.Changed += CoordinatorChanged;
        Coordinator.SavesRead += CoordinatorSavesRead;
        foreach (var pair in files.ReadSaveNames()) SaveNames[pair.Key] = pair.Value;
        Refresh();
    }

    private void FilesChanged(object? sender, EventArgs args)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(() => Refresh());
    }
    private void CoordinatorChanged(object? sender, EventArgs args)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    public void Refresh()
    {
        Scripts.Clear();
        foreach (var item in _files.ListScripts()) Scripts.Add(item);
        var plans = _files.ListPlans(out var errors);
        Plans.Clear();
        foreach (var plan in plans) Plans.Add(plan);
        QuickPlans.Clear();
        foreach (var plan in plans.Where(plan => plan.ShowOnDashboard)) QuickPlans.Add(plan);
        PlanErrors = string.Join(Environment.NewLine, errors);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>补齐新建或导入账号的自带日常，不重建用户已经删除的默认方案。</summary>
    public async Task<IReadOnlyList<ScriptAccount>> AccountsAsync()
    {
        var accounts = await _accounts.ListScriptAccountsAsync();
        if (_files.EnsureAccountDailyPlans(accounts)) Refresh();
        return accounts;
    }
    public void Save(ScriptPlan plan, bool updateReferences = false) { _files.SavePlan(plan, updateReferences); Refresh(); }
    public void RenameAccountReferences(Guid id, string name) { _files.RenameAccountReferences(id, name); Refresh(); }
    public void Delete(Guid id) { _files.DeletePlan(id); Refresh(); }
    public string ReadGlobalConstants() => _files.ReadGlobalConstants();
    public void SaveGlobalConstants(string source) => _files.SaveGlobalConstants(source);

    /// <summary>编辑器和主面板共用编译及运行入口；先固定调用树，再执行任何游戏操作。</summary>
    public async Task<CompiledScript> CompileAsync(ScriptPlan plan, bool allowSaveSelection = false)
    {
        var all = Plans.Where(candidate => candidate.Id != plan.Id).Append(plan).ToArray();
        var definitions = Scripts.Where(item => item.Error is null && item.Definition is not null)
                        .Select(item =>
            {
                var copy = ScriptJson.Clone(item.Definition!);
                copy.Directory = item.Definition!.Directory;
                return copy;
            }).ToArray();
        foreach (var definition in definitions)
            foreach (var step in definition.Flow.Where(step => step.Type is "waitImage" or "clickImage"))
                definition.Images[step.Image] = File.ReadAllBytes(Path.Combine(definition.Directory, step.Image));
        var globals = ScriptSyntax.ParseConstants(_files.ReadGlobalConstants());
        return new ScriptProgramCompiler(all, await AccountsAsync(), definitions, SaveNames, globals).Compile(plan, allowSaveSelection);
    }

    /// <summary>先校验调用树，首次选档前自动读取并缓存名称；全部选档确认后才回写方案。</summary>
    public async Task RunAsync(ScriptPlan plan)
    {
        if (IsBusy) throw new InvalidOperationException("已有方案正在准备或运行。");
        _preparing = true;
        using var preparation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _preparationStop = preparation;
        PreparationStage = "正在校验方案";
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            if (plan.Version == 1)
            {
                var script = Scripts.SingleOrDefault(item => item.Definition?.Id == plan.ScriptId && item.Error is null)?.Definition
                    ?? throw new InvalidOperationException("方案对应的脚本不可运行，请检查脚本列表。");
                await Coordinator.RunAsync(plan, script, _lifetime.Token);
                return;
            }
            var preview = await CompileAsync(plan, allowSaveSelection: true);
            Coordinator.ValidateCapabilities(preview);
            var pending = AccountDailyPlans.RequiredSelections(preview);
            if (pending.Count > 0)
            {
                var changes = new Dictionary<Guid, ScriptPlan>();
                var scanned = new HashSet<Guid>();
                foreach (var selection in pending)
                {
                    if (scanned.Add(selection.Account.Id))
                    {
                        PreparationStage = "自动读取“" + selection.Account.Name + "”的存档名称";
                        Changed?.Invoke(this, EventArgs.Empty);
                        var slots = await Coordinator.ReadSavesAsync(selection.Account.Id, _ => { }, preparation.Token, keepWindow: true);
                        SetSaveNames(selection.Account.Id, slots);
                    }
                    PreparationStage = "请选择“" + selection.Account.Name + "”的存档";
                    Changed?.Invoke(this, EventArgs.Empty);
                    var number = SelectSaveAsync is null ? null : await SelectSaveAsync(selection);
                    preparation.Token.ThrowIfCancellationRequested();
                    if (number is null) throw new OperationCanceledException("已取消选择存档，脚本未启动。");
                    var source = changes.GetValueOrDefault(selection.PlanId)
                        ?? (selection.PlanId == plan.Id ? plan : Plans.Single(candidate => candidate.Id == selection.PlanId));
                    changes[selection.PlanId] = AccountDailyPlans.ApplySelection(source, selection, number.Value);
                }
                preparation.Token.ThrowIfCancellationRequested();
                foreach (var updated in changes.Values) _files.SavePlan(updated);
                if (changes.TryGetValue(plan.Id, out var configured)) plan = configured;
                Refresh();
            }
            preparation.Token.ThrowIfCancellationRequested();
            var program = await CompileAsync(plan);
            PreparationStage = "";
            var running = Coordinator.RunAsync(program, preparation.Token);
            Changed?.Invoke(this, EventArgs.Empty);
            await running;
        }
        finally { _preparing = false; _preparationStop = null; PreparationStage = ""; Changed?.Invoke(this, EventArgs.Empty); }
    }

    /// <summary>准备阶段的扫描与正式队列共用停止入口，保留已经打开的游戏窗口。</summary>
    public void Stop() { _preparationStop?.Cancel(); Coordinator.Stop(); }
    private void CoordinatorSavesRead(Guid id, IReadOnlyList<SaveSlot> slots)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted)
            dispatcher.BeginInvoke(() => SetSaveNames(id, slots));
    }

    /// <summary>存档资料与方案源码分开保存，校正名称不改正在运行的身份和顺序。</summary>
    public void SetSaveNames(Guid id, IReadOnlyList<SaveSlot> slots)
    {
        SaveNames[id] = slots;
        _files.SaveNames(id, slots);
        SavesRead?.Invoke(id, slots);
    }

    public Task<IReadOnlyList<SaveSlot>> ReadSavesAsync(Guid account, Action<string> stage, CancellationToken token)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        var scan = ReadCoreAsync(account, stage, linked);
        _scans.Add(scan);
        _scans.RemoveAll(task => task.IsCompleted);
        return scan;
    }

    private async Task<IReadOnlyList<SaveSlot>> ReadCoreAsync(Guid account, Action<string> stage, CancellationTokenSource linked)
    {
        using (linked) return await Coordinator.ReadSavesAsync(account, stage, linked.Token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _files.Changed -= FilesChanged;
        Coordinator.Changed -= CoordinatorChanged;
        Coordinator.SavesRead -= CoordinatorSavesRead;
        _lifetime.Cancel();
        await Coordinator.DisposeAsync().ConfigureAwait(false);
        try { await Task.WhenAll(_scans).ConfigureAwait(false); }
        catch (Exception) { /* 退出只结束自身扫描；窗口关闭交由原会话生命周期处理。 */ }
        _lifetime.Dispose();
    }
}
