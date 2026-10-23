using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using BqtjLauncher.Application;

namespace BqtjLauncher.Desktop;

/// <summary>三栏中文方案编辑器；保存后命令栏切换为只读运行监控，窗口关闭仍不停止根运行。</summary>
public partial class ScriptManagerWindow : Window, IDisposable
{
    private readonly ScriptWorkspace _workspace;
    private IReadOnlyList<ScriptAccount> _accounts = [];
    private ScriptPlan _plan = NewTextPlan();
    private bool _loading;
    private bool _painting;
    private bool _editing = true;
    private bool _dirty;
    private bool _refreshingAccounts;

    public ScriptManagerWindow(ScriptWorkspace workspace)
    {
        _workspace = workspace;
        InitializeComponent();
        SourceEditor.Document = new FlowDocument { PagePadding = new Thickness(8) };
        SourceEditor.SizeChanged += (_, _) => UpdatePageWidth();
        CommandsList.ItemsSource = ScriptSyntax.Operations.Where(operation => operation is not ("并行结束" or "方案设置")).ToArray();
        CommandsList.SelectedIndex = 0;

        _workspace.Changed += Workspace_Changed;
        _workspace.SavesRead += Saves_Read;
        Closing += HideOnClose;
        Loaded += async (_, _) => { FitToWorkArea(); await RefreshAccountsAsync(); };
        Activated += async (_, _) => await RefreshAccountsAsync();
        GlobalConstantsText.Text = _workspace.ReadGlobalConstants();
        LoadPlan(NewTextPlan(), true);
    }

    private static ScriptPlan NewTextPlan() => new() { Version = 2, IsSubPlan = false, Source = "方案设置,子方案,否\n方案设置,面板可见,是\n# 从右侧插入操作；英文逗号分隔参数，每行一条语句。" };

    /// <summary>窗口适配当前显示器工作区，高DPI下仍能看到标题栏和关闭按钮。</summary>
    private void FitToWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(handle, 2), ref info)) return;
        if (HwndSource.FromHwnd(handle)?.CompositionTarget is not { } target) return;
        var area = new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        area.Transform(target.TransformFromDevice);
        MinWidth = Math.Min(MinWidth, Math.Max(1, area.Width - 16));
        MinHeight = Math.Min(MinHeight, Math.Max(1, area.Height - 16));
        Width = Math.Min(Width, area.Width - 16); Height = Math.Min(Height, area.Height - 16);
        Left = Math.Clamp(Left, area.Left + 8, area.Right - Width - 8);
        Top = Math.Clamp(Top, area.Top + 8, area.Bottom - Height - 8);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    private async Task RefreshAccountsAsync()
    {
        if (_refreshingAccounts) return;
        _refreshingAccounts = true;
        try
        {
            var previous = (Accounts.SelectedItem as ScriptAccount)?.Id ?? _plan.DefaultAccountId;
            var updated = await _workspace.AccountsAsync();
            if (SyncReferences.IsChecked == true)
                foreach (var account in updated.Where(account => _accounts.Any(old => old.Id == account.Id && old.Name != account.Name)))
                {
                    _workspace.RenameAccountReferences(account.Id, account.Name);
                    foreach (var alias in _plan.AccountBindings.Where(pair => pair.Value == account.Id).Select(pair => pair.Key).ToArray())
                        SetSource(ScriptSyntax.Rename(GetSource(), alias, account.Name, false));
                    _plan.AccountBindings[account.Name] = account.Id;
                }
            _accounts = updated;
            Accounts.ItemsSource = updated;
            // 读取账号列表期间可能刚从主面板打开另一份日常，采用返回时的方案归属。
            var preferred = _plan.DefaultAccountId ?? previous;
            Accounts.SelectedItem = updated.FirstOrDefault(account => account.Id == preferred) ?? (updated.Count > 0 ? updated[0] : null);
        }
        catch (Exception error) { Status.Text = error.Message; }
        finally { _refreshingAccounts = false; }
    }

    private void Workspace_Changed(object? sender, EventArgs args)
    {
        if (PlansList is null) return;
        _loading = true;
        try
        {
            // 首次选档回写文件后，同步只读预览；正在编辑的草稿仍由用户决定是否保存。
            if (!_editing && !_dirty && _workspace.Plans.FirstOrDefault(plan => plan.Id == _plan.Id) is { } saved && saved.Source != _plan.Source)
            {
                _plan = ScriptJson.Clone(saved);
                SetSource(saved.Source);
            }
            var query = SearchPlans.Text.Trim();
            PlansList.ItemsSource = _workspace.Plans.Where(plan => plan.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
            PlansList.SelectedItem = _workspace.Plans.FirstOrDefault(plan => plan.Id == _plan.Id);
            SubPlans.ItemsSource = _workspace.Plans.Where(plan => plan.Id != _plan.Id && plan.IsSubPlan)
                .Select(plan => new SubPlanChoice(plan, _workspace.Plans.Count(other => other.Name == plan.Name) > 1 ? plan.Name + " [" + plan.Id.ToString("N")[..6] + "]" : plan.Name)).ToArray();
            if (SubPlans.Items.Count > 0) SubPlans.SelectedIndex = 0;
            ProgressList.ItemsSource = _workspace.Coordinator.Updates;
            RunIdentity.Text = string.IsNullOrEmpty(_workspace.Coordinator.RunningName) ? "尚未开始运行。保存后可点击运行方案。"
                : $"本次快照：{_workspace.Coordinator.RunningName}\n" + (_workspace.Coordinator.IsPaused ? "手动暂停 · 继续当前语句，已完成语句不重跑"
                    : _workspace.Coordinator.IsRunning ? "执行中 · 编辑不会改变本次运行" : _workspace.Coordinator.CanContinue ? "运行已中断 · 可继续本次快照，重试未完成语句" : "本轮已结束 · 结果保留供查看");
            StartButton.IsEnabled = !_workspace.IsBusy;
            StopButton.IsEnabled = _workspace.IsBusy;
            PauseButton.IsEnabled = _workspace.Coordinator.CanPause;
            ContinueButton.IsEnabled = _workspace.Coordinator.CanContinue && (!_workspace.IsBusy || _workspace.Coordinator.IsPaused);
            if (!string.IsNullOrEmpty(_workspace.PreparationStage)) RunIdentity.Text = _workspace.PreparationStage;
            if (!string.IsNullOrEmpty(_workspace.PlanErrors)) Status.Text = _workspace.PlanErrors;
        }
        finally { _loading = false; }
    }

    private void LoadPlan(ScriptPlan plan, bool edit)
    {
        _loading = true;
        try
        {
            _plan = ScriptJson.Clone(plan);
            PlanName.Text = plan.Name;
            // 从当前账号入口编辑日常时，参数面板也选中绑定账号，避免插入到另一个账号。
            if (plan.DefaultAccountId is { } accountId && _accounts.FirstOrDefault(account => account.Id == accountId) is { } selected)
                Accounts.SelectedItem = selected;
            Speed.Text = plan.Speed.ToString(CultureInfo.InvariantCulture);
            Concurrency.Text = plan.ConcurrentAccounts.ToString(CultureInfo.InvariantCulture);
            ConstantsText.Text = plan.ConstantsSource ?? string.Join("\n", plan.Constants.Select(pair => pair.Key + "=" + pair.Value));
            var source = plan.Version == 1 ? ImportLegacy(plan) : plan.Source;
            if (plan.Version == 2 && !source.Contains("方案设置,", StringComparison.Ordinal))
                source = $"方案设置,子方案,{(plan.IsSubPlan ? "是" : "否")}\n方案设置,面板可见,{(plan.ShowOnDashboard ? "是" : "否")}\n" + source;
            SetSource(source);
            ValidationText.Text = plan.Version == 1 ? "旧版方案保留账号并行语义。点击“复制方案”转换为中文顺序语句，再安排并行。" : "";
            _dirty = false;

        }
        finally { _loading = false; }
        SetMode(edit && plan.Version == 2);
        Workspace_Changed(this, EventArgs.Empty);
        Status.Text = plan.IsBuiltIn
            ? "内置模板可直接编辑；首次使用请配置账号常量，修改会保存在这个方案中。"
            : "双击方案或编辑区继续编辑；保存后可在右侧查看本次运行。";
    }

    private string ImportLegacy(ScriptPlan plan)
    {
        foreach (var task in plan.Tasks)
            _plan.AccountBindings[task.AccountName + " [" + task.ProfileId.ToString("N")[..6] + "]"] = task.ProfileId;
        return string.Join("\n", plan.Tasks.SelectMany(task =>
        {
            var alias = task.AccountName + " [" + task.ProfileId.ToString("N")[..6] + "]";
            var operation = plan.ScriptId == "official-daily" ? "日常操作" : "旧版自定义脚本（请按原方案运行）";
            return task.CloseAfterFinish
                ? new[] { ScriptSyntax.Statement(operation, alias, "存档" + task.SaveNumber), ScriptSyntax.Statement("关闭窗口", alias) }
                : [ScriptSyntax.Statement(operation, alias, "存档" + task.SaveNumber)];
        }));
    }

    /// <summary>切换前允许保存、放弃或取消；保存语法错误草稿的规则与手动保存一致。</summary>
    private async Task<bool> ConfirmSaveAsync()
    {
        if (!_dirty) return true;
        var choice = MessageBox.Show(this, "当前方案尚未保存，是否确认保存？\n是：保存后继续；否：不保存继续；取消：留在当前方案。",
            "保存方案", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return choice == MessageBoxResult.No || choice == MessageBoxResult.Yes && await SaveAsync();
    }
    private async void NewPlan_Click(object sender, RoutedEventArgs args) { if (await ConfirmSaveAsync()) LoadPlan(NewTextPlan(), true); }
    private async void Copy_Click(object sender, RoutedEventArgs args)
    {
        var source = PlansList.SelectedItem as ScriptPlan;
        if (!await ConfirmSaveAsync()) return;
        source = _workspace.Plans.FirstOrDefault(plan => plan.Id == source?.Id) ?? source;
        if (source is null) return;
        if (source.Version == 1 && source.ScriptId != "official-daily") { Status.Text = "旧版自定义脚本可按原方案运行，暂不转换为日常语句。"; return; }
        var copy = ScriptJson.Clone(source);
        if (copy.Version == 1) { LoadPlan(copy, false); copy = ScriptJson.Clone(_plan); copy.Source = GetSource(); copy.Version = 2; }
        copy.Id = Guid.NewGuid(); copy.Name += " 副本"; copy.IsBuiltIn = false; copy.DefaultAccountId = null;
        if (copy.Constants.ContainsKey("主号") && Accounts.SelectedItem is ScriptAccount account)
        {
            var alias = AccountKey(account);
            copy.Constants["主号"] = alias;
            copy.AccountBindings[alias] = account.Id;
        }
        LoadPlan(copy, true); _dirty = true;
        Status.Text = "副本可以编辑；旧版任务复制为顺序语句，需要并行时请插入并行块。";
    }
    private async void Plan_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (_loading || PlansList.SelectedItem is not ScriptPlan plan || plan.Id == _plan.Id) return;
        if (await ConfirmSaveAsync()) LoadPlan(_workspace.Plans.FirstOrDefault(candidate => candidate.Id == plan.Id) ?? plan, false);
        else Workspace_Changed(this, EventArgs.Empty);
    }
    /// <summary>主面板直接编辑选中账号的日常方案，仍遵守当前草稿的保存确认。</summary>
    public async Task OpenPlanAsync(Guid id)
    {
        if (id == _plan.Id) { SetMode(true); return; }
        if (!await ConfirmSaveAsync()) return;
        if (_workspace.Plans.FirstOrDefault(plan => plan.Id == id) is { } selected) LoadPlan(selected, true);
    }
    private void SearchPlans_Changed(object sender, TextChangedEventArgs args) { if (PlansList is not null) Workspace_Changed(this, EventArgs.Empty); }

    private void SetMode(bool edit)
    {
        _editing = edit && _plan.Version == 2;
        CommandsPanel.Visibility = _editing ? Visibility.Visible : Visibility.Collapsed;
        MonitorPanel.Visibility = _editing ? Visibility.Collapsed : Visibility.Visible;
        InsertButton.Visibility = CommandsPanel.Visibility;
        StopButton.Visibility = _editing ? Visibility.Collapsed : Visibility.Visible;
        PauseButton.Visibility = ContinueButton.Visibility = StopButton.Visibility;
        SourceEditor.IsReadOnly = !_editing;
        PlanName.IsReadOnly = Speed.IsReadOnly = Concurrency.IsReadOnly = !_editing;
        SaveButton.IsEnabled = ValidateButton.IsEnabled = _plan.Version == 2;
        SyncReferences.IsEnabled = _editing;
        EditorState.Text = _editing
            ? "编辑草稿 · 普通语句顺序执行；并行块中的账号分支同时运行。"
            : (_dirty || !_workspace.Plans.Any(plan => plan.Id == _plan.Id)
                ? "未保存草稿 · 只读预览；右侧监控独立，点击“编辑方案”继续修改。"
                : $"已保存方案：{_plan.Name} · 语句只读，点击“编辑方案”可修改。");
        Highlight();
    }
    private void Edit_Click(object sender, RoutedEventArgs args) => SetMode(true);
    private void Plan_DoubleClick(object sender, MouseButtonEventArgs args)
    {
        if (ItemsControl.ContainerFromElement(PlansList, args.OriginalSource as DependencyObject) is ListBoxItem) SetMode(true);
    }
    private void Editor_DoubleClick(object sender, MouseButtonEventArgs args) { if (!_editing) SetMode(true); }
    private void Command_DoubleClick(object sender, MouseButtonEventArgs args)
    {
        if (ItemsControl.ContainerFromElement(CommandsList, args.OriginalSource as DependencyObject) is ListBoxItem)
            Insert_Click(sender, args);
    }
    private string GetSource() => new TextRange(SourceEditor.Document.ContentStart, SourceEditor.Document.ContentEnd).Text.Replace("\r", "", StringComparison.Ordinal).TrimEnd('\n');
    private void SetSource(string source)
    {
        _painting = true;
        try
        {
            SourceEditor.Document.Blocks.Clear();
            foreach (var line in source.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
                SourceEditor.Document.Blocks.Add(new Paragraph(new Run(line)) { Margin = new Thickness(0), LineHeight = 22 });
        }
        finally { _painting = false; }
        Highlight();
    }
    private void Source_Changed(object sender, TextChangedEventArgs args)
    {
        if (_painting || SourceEditor is null || LineNumbers is null) return;
        if (!_loading && _editing) _dirty = true;
        if (args.UndoAction is UndoAction.Undo or UndoAction.Redo)
        {
            LineNumbers.Text = string.Join("\n", Enumerable.Range(1, Math.Max(1, GetSource().Split('\n').Length)));
            return;
        }
        Highlight();
    }
    private void Metadata_Changed(object sender, TextChangedEventArgs args) { if (!_loading && _editing) _dirty = true; }

    /// <summary>只高亮每行命令，保留正文、选择和光标；不引入额外脚本环境或大型编辑器依赖。</summary>
    private void Highlight()
    {
        if (_painting || SourceEditor is null || LineNumbers is null) return;
        _painting = true;
        try
        {
            var number = 0;
            SourceEditor.BeginChange();
            foreach (var paragraph in SourceEditor.Document.Blocks.OfType<Paragraph>().ToArray())
            {
                number++;
                paragraph.Margin = new Thickness(0); paragraph.LineHeight = 22;
                var range = new TextRange(paragraph.ContentStart, paragraph.ContentEnd);
                range.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.LightGray);
                var text = range.Text.TrimEnd('\r', '\n');
                var command = text.Split(',')[0].Trim();
                if (text.TrimStart().StartsWith('#')) range.ApplyPropertyValue(TextElement.ForegroundProperty, Brushes.SlateGray);
                else if (ScriptSyntax.Operations.Contains(command))
                {
                    var begin = paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
                    var end = begin;
                    for (var index = 0; index < text.IndexOf(command, StringComparison.Ordinal) + command.Length; index++)
                        end = end?.GetNextInsertionPosition(LogicalDirection.Forward);
                    if (begin is not null && end is not null)
                        new TextRange(begin, end).ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(Color.FromRgb(85, 211, 193)));
                }
            }
            LineNumbers.Text = string.Join("\n", Enumerable.Range(1, Math.Max(1, number)));
            UpdatePageWidth();
        }
        finally { SourceEditor.EndChange(); _painting = false; }
    }
    private void UpdatePageWidth()
    {
        var source = GetSource();
        var text = new FormattedText(source.Length == 0 ? " " : source, CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, new Typeface(SourceEditor.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            SourceEditor.FontSize, Brushes.White, VisualTreeHelper.GetDpi(SourceEditor).PixelsPerDip);
        SourceEditor.Document.PageWidth = Math.Max(200, Math.Max(SourceEditor.ActualWidth - 20, text.WidthIncludingTrailingWhitespace + 24));
    }
    private void Editor_ScrollChanged(object sender, ScrollChangedEventArgs args)
        => LineNumbers.RenderTransform = new TranslateTransform(0, -args.VerticalOffset);

    private ScriptPlan ReadPlan()
    {
        if (_plan.Version == 1) return ScriptJson.Clone(_plan);
        var plan = ScriptJson.Clone(_plan);
        plan.Name = PlanName.Text.Trim(); plan.Source = GetSource();
        if (!decimal.TryParse(Speed.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var speed)) throw new InvalidDataException("倍率格式无效。");
        if (!int.TryParse(Concurrency.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var concurrency)) throw new InvalidDataException("并发数格式无效。");
        plan.Speed = speed; plan.ConcurrentAccounts = concurrency;
        ScriptSyntax.ApplySettings(plan, strict: false);
        plan.ConstantsSource = ConstantsText.Text;
        // 常量原文属于草稿，格式暂错也保留；执行编译器会再次校验，保存不会启动任务。
        try { plan.Constants = ScriptSyntax.ParseConstants(plan.ConstantsSource); }
        catch (InvalidDataException) { plan.Constants = []; }
        if (SyncReferences.IsChecked == true)
            foreach (var old in _plan.Constants.Where(pair => !plan.Constants.ContainsKey(pair.Key)))
            {
                var replacements = plan.Constants.Where(pair => pair.Value == old.Value && !_plan.Constants.ContainsKey(pair.Key)).ToArray();
                if (replacements.Length != 1) continue;
                try { plan.Source = RenameConstant(plan.Source, old.Key, replacements[0].Key); }
                catch (InvalidDataException) { /* 错误语句原样保存，待用户修正后再校验。 */ }
            }
        return plan;
    }

    private static string RenameConstant(string source, string old, string name)
    {
        var lines = source.Split('\n');
        foreach (var statement in ScriptSyntax.Parse(source))
        {
            var changed = false;
            for (var index = 1; index < statement.Words.Length; index++)
                if (statement.Words[index] == "$" + old) { statement.Words[index] = "$" + name; changed = true; }
            if (changed) lines[statement.Line - 1] = ScriptSyntax.Statement(statement.Words);
        }
        return string.Join("\n", lines);
    }

    /// <summary>单独保存所有方案共享的默认常量；不把全局编辑混入当前方案草稿。</summary>
    private void SaveGlobals_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            _workspace.SaveGlobalConstants(GlobalConstantsText.Text);
            Status.Text = "全局常量已保存，所有方案下次运行生效；本方案同名值优先。";
        }
        catch (Exception error) { ValidationText.Text = error.Message; }
    }
    private async void Validate_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var compiled = await _workspace.CompileAsync(ReadPlan(), allowSaveSelection: true);
            ValidationText.Text = $"校验通过：{compiled.Commands.Count}条操作，{compiled.Commands.Select(command => command.Account.Id).Distinct().Count()}个账号。";
        }
        catch (Exception error) { ValidationText.Text = error.Message; }
    }
    private async Task<bool> SaveAsync()
    {
        try
        {
            var plan = ReadPlan();
            string? validationError = null;
            try { await _workspace.CompileAsync(plan, allowSaveSelection: true); }
            catch (Exception error) { validationError = error.Message; }
            _workspace.Save(plan, SyncReferences.IsChecked == true);
            LoadPlan(plan, false);
            ValidationText.Text = validationError ?? "";
            Status.Text = validationError is null ? "方案已保存；右侧已切换为本次运行监控。" : "草稿已保存，校验未通过；运行前请修正。";
            return true;
        }
        catch (Exception error) { ValidationText.Text = error.Message; return false; }
    }
    private async void SavePlan_Click(object sender, RoutedEventArgs args) => await SaveAsync();
    private async void RunPlan_Click(object sender, RoutedEventArgs args)
    {
        if (_plan.Version == 2 && !await SaveAsync()) return;
        SetMode(false);
        try
        {
            var running = _workspace.RunAsync(_plan);
            Workspace_Changed(this, EventArgs.Empty);
            await running;
            Workspace_Changed(this, EventArgs.Empty);
            Status.Text = "本次运行已结束；查看右侧逐条结果。";
        }
        catch (OperationCanceledException) { Status.Text = "已取消选择存档，脚本未启动。"; }
        catch (Exception error) { ValidationText.Text = error.Message; Status.Text = "方案未启动：" + error.Message; }
    }
    private void Stop_Click(object sender, RoutedEventArgs args) { _workspace.Stop(); Status.Text = "停止外部操作；游戏内置脚本可能仍在运行。"; }
    private void Pause_Click(object sender, RoutedEventArgs args)
    {
        _workspace.Coordinator.Pause();
        Status.Text = "已暂停外部操作；游戏内置脚本和自动战斗可能仍在运行。点击继续可从当前步骤恢复。";
    }
    private async void Continue_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var continuing = _workspace.Coordinator.ContinueAsync();
            Workspace_Changed(this, EventArgs.Empty);
            Status.Text = "继续本次运行快照；已完成语句不重跑。";
            await continuing;
            Workspace_Changed(this, EventArgs.Empty);
        }
        catch (Exception error) { Status.Text = error.Message; }
    }
    private void DeletePlan_Click(object sender, RoutedEventArgs args)
    {
        if (PlansList.SelectedItem is not ScriptPlan plan) return;
        if (MessageBox.Show(this, $"删除方案“{plan.Name}”？引用它的方案将提示缺失。", "删除方案", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _workspace.Delete(plan.Id); LoadPlan(NewTextPlan(), true);
    }

    private string AccountKey(ScriptAccount account) => _accounts.Count(candidate => candidate.Name == account.Name) > 1
        ? account.Name + " [" + account.Id.ToString("N")[..6] + "]" : account.Name;
    private void Account_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (Accounts.SelectedItem is not ScriptAccount account) return;
        AccountAlias.Text = AccountKey(account);
        ShowSaves(account.Id);
    }
    private void ShowSaves(Guid id)
    {
        var selected = Math.Max(0, SaveSlots.SelectedIndex);
        var slots = _workspace.SaveNames.GetValueOrDefault(id) ?? [];
        SaveSlots.ItemsSource = Enumerable.Range(1, 8).Select(number =>
            new SaveChoice(number, slots.FirstOrDefault(slot => slot.Number == number)?.Name ?? "未读取", slots.FirstOrDefault(slot => slot.Number == number)?.NeedsReview ?? false)).ToArray();
        SaveSlots.SelectedIndex = selected;
    }
    private void Save_Changed(object sender, SelectionChangedEventArgs args)
    { if (SaveSlots.SelectedItem is SaveChoice save) { SaveName.Text = save.Name; SaveName.BorderBrush = save.NeedsReview ? Brushes.Goldenrod : new SolidColorBrush(Color.FromRgb(53, 69, 92)); } }
    private void Saves_Read(Guid id, IReadOnlyList<SaveSlot> slots) { if ((Accounts.SelectedItem as ScriptAccount)?.Id == id) ShowSaves(id); }
    private void CorrectName_Click(object sender, RoutedEventArgs args)
    {
        if (Accounts.SelectedItem is not ScriptAccount account || SaveSlots.SelectedItem is not SaveChoice selected) return;
        var names = ((IEnumerable<SaveChoice>)SaveSlots.ItemsSource).Select(save => new SaveSlot(save.Number, save.Number == selected.Number ? SaveName.Text.Trim() : save.Name)).ToArray();
        _workspace.SetSaveNames(account.Id, names);
        Status.Text = "存档名称已校正，存档号与运行快照不变。";
    }
    private void Command_Changed(object sender, SelectionChangedEventArgs args)
    {
        if (CommandHelp is null) return;
        var operation = CommandsList.SelectedItem as string;
        AccountParameters.Visibility = operation is "调用" or "并行开始" ? Visibility.Collapsed : Visibility.Visible;
        SaveParameters.Visibility = operation is "日常操作" or "启动存档" or "刷取每日材料" ? Visibility.Visible : Visibility.Collapsed;
        CallParameters.Visibility = operation == "调用" ? Visibility.Visible : Visibility.Collapsed;
        FarmingParameters.Visibility = operation == "刷取每日材料" ? Visibility.Visible : Visibility.Collapsed;
        DailyParameters.Visibility = operation == "日常操作" ? Visibility.Visible : Visibility.Collapsed;
        CommandHelp.Text = operation switch
        {
            "日常操作" => "进入指定存档，优先运行日常操作复制，其次日常操作。可填写游戏内脚本名；完成仍需本次进度变化和标题绿勾，保存由游戏执行。",
            "读取存档名称" => "读取八档名称，临时二倍速，完成保留窗口供同次方案继续使用。",
            "启动存档" => "进入指定存档并保留，不自动启动日常。",
            "刷取每日材料" => "天空顶正常模式，默认二倍速。战后N/W同时关闭AI并上升，黄点辅助判断到顶；W最多1.5秒。第8轮起且通关首屏无材料才查双限额。需协议7组件，当前供开发联调。",
            "退出存档" => "退出当前角色并确认，随后回到选档入口。",
            "关闭窗口" => "关闭本次运行已接管或创建的精确账号窗口。",
            "调用" => "插入已保存子方案；改名可选择同步全部引用。",
            _ => "插入并行块，将不同账号的子方案放在块内；同账号任务放进同一子方案。"
        };
    }
    private void Insert_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var operation = (string?)CommandsList.SelectedItem ?? "日常操作";
            string line;
            if (operation == "并行开始") line = "并行开始\n\n并行结束";
            else if (operation == "调用")
            {
                if (SubPlans.SelectedItem is not SubPlanChoice child) throw new InvalidDataException("先保存一个子方案，再选择插入。");
                if (_plan.PlanBindings.TryGetValue(child.Label, out var previous) && previous != child.Plan.Id)
                    throw new InvalidDataException("子方案别名已被使用，请先校正原引用。");
                _plan.PlanBindings[child.Label] = child.Plan.Id;
                line = ScriptSyntax.Statement(operation, child.Label);
            }
            else
            {
                if (Accounts.SelectedItem is not ScriptAccount account) throw new InvalidDataException("请选择账号。");
                var alias = AccountAlias.Text.Trim();
                if (alias.Length == 0) throw new InvalidDataException("账号别名不能为空。");
                if (_plan.AccountBindings.TryGetValue(alias, out var previous) && previous != account.Id)
                    throw new InvalidDataException("此别名已绑定另一个账号，请使用不同别名。");
                _plan.AccountBindings[alias] = account.Id;
                line = operation == "刷取每日材料"
                    ? ScriptSyntax.Statement(operation, alias, "存档" + (((SaveChoice?)SaveSlots.SelectedItem)?.Number ?? 1),
                        ((ComboBoxItem)FarmingMap.SelectedItem).Content.ToString()!, FarmingDifficulty.Text.Trim(), FarmingTarget.Text.Trim())
                    : operation is "日常操作" or "启动存档"
                    ? ScriptSyntax.Statement(operation, alias, "存档" + (((SaveChoice?)SaveSlots.SelectedItem)?.Number ?? 1))
                    : ScriptSyntax.Statement(operation, alias);
                if (operation == "日常操作" && !string.IsNullOrWhiteSpace(OfficialDailyName.Text))
                    line += "," + ScriptSyntax.Quote(OfficialDailyName.Text.Trim());
            }
            // 空白行直接复用；已有内容的行在其后插入，避免拆开现有参数或留下多余空行。
            var paragraph = SourceEditor.CaretPosition.Paragraph;
            var additions = line.Split('\n').Select(value => new Paragraph(new Run(value)) { Margin = new Thickness(0), LineHeight = 22 }).ToArray();
            Block? anchor = paragraph;
            if (paragraph is not null && string.IsNullOrWhiteSpace(new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text))
            {
                paragraph.Inlines.Clear();
                paragraph.Inlines.Add(new Run(line.Split('\n')[0]));
                additions = additions.Skip(1).ToArray();
            }
            foreach (var addition in additions)
            {
                if (anchor is null) SourceEditor.Document.Blocks.Add(addition);
                else SourceEditor.Document.Blocks.InsertAfter(anchor, addition);
                anchor = addition;
            }
            SourceEditor.CaretPosition = (anchor ?? SourceEditor.Document.Blocks.LastBlock).ContentEnd;
            SourceEditor.Focus(); _dirty = true; Highlight();
        }
        catch (Exception error) { ValidationText.Text = error.Message; }
    }
    private sealed record SubPlanChoice(ScriptPlan Plan, string Label)
    {
        public string Name => Label + (Plan.IsBuiltIn ? "（内置模板）" : "");
        public override string ToString() => Name;
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs args) => Process.Start(new ProcessStartInfo(_workspace.Root) { UseShellExecute = true });
    private void Refresh_Click(object sender, RoutedEventArgs args) => _workspace.Refresh();
    /// <summary>窗口拖入是文件夹投放的快捷入口，只复制JSON和图片，不载入EXE或DLL。</summary>
    private void Files_Drop(object sender, DragEventArgs args)
    {
        if (!args.Data.GetDataPresent(DataFormats.FileDrop)) return;
        try
        {
            foreach (var source in (string[])args.Data.GetData(DataFormats.FileDrop))
            {
                if (File.Exists(source) && Path.GetExtension(source).Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(source));
                    if (json.RootElement.TryGetProperty("source", out _) || json.RootElement.TryGetProperty("tasks", out _))
                    {
                        var plan = JsonSerializer.Deserialize<ScriptPlan>(json.RootElement, ScriptJson.Options)
                            ?? throw new InvalidDataException("方案内容为空。");
                        _workspace.Save(plan);
                        continue;
                    }
                    var destination = Path.Combine(_workspace.Root, Path.GetFileName(source));
                    if (!Path.GetFullPath(source).Equals(destination, StringComparison.OrdinalIgnoreCase)) File.Copy(source, destination, true);
                }
                else if (Directory.Exists(source) && File.Exists(Path.Combine(source, "script.json")))
                {
                    var destination = Path.Combine(_workspace.Root, Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar)));
                    if (Path.GetFullPath(source).Equals(destination, StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories).Where(file => Path.GetExtension(file).ToLowerInvariant() is ".json" or ".png" or ".jpg" or ".jpeg" or ".bmp"))
                    {
                        var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, true);
                    }
                }
                else throw new InvalidDataException("请拖入JSON或含script.json的脚本目录。");
            }
            _workspace.Refresh(); Status.Text = "方案或JSON脚本包已导入；方案可在左侧选择。";
        }
        catch (Exception error) { Status.Text = error.Message; }
    }


    private void HideOnClose(object? sender, CancelEventArgs args) { args.Cancel = true; Hide(); }

    public void Dispose()
    {
        _workspace.Changed -= Workspace_Changed;
        _workspace.SavesRead -= Saves_Read;
        GC.SuppressFinalize(this);
    }
}

/// <summary>存档下拉项，显示名称不参与实际选档身份。</summary>
public sealed record SaveChoice(int Number, string Name, bool NeedsReview = false)
{
    public string Label => $"存档{Number}－{Name}" + (NeedsReview ? "（请校正）" : "");
    public override string ToString() => Label;
}
