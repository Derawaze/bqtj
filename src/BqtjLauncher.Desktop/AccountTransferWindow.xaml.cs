using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using BqtjLauncher.Application;
using Microsoft.Win32;

namespace BqtjLauncher.Desktop;

/// <summary>独立账号管理窗口：左侧搜索、多选及原位编辑，右侧批量导入与人工审核。</summary>
public partial class AccountTransferWindow : Window
{
    private readonly AccountTransferService _service;
    private readonly List<AccountImportConflict> _pending = [];
    private ICollectionView? _accountsView;
    private bool _busy;
    private bool _reviewing;
    private bool _editRequested;
    private bool _savingCell;
    private bool _editing;
    private bool _updatingSelection;
    private string _importSummary = string.Empty;

    public AccountTransferWindow(AccountTransferService service)
    {
        InitializeComponent();
        _service = service;
        Loaded += async (_, _) => await RefreshAccountsAsync();
        Closing += (_, e) => e.Cancel = _busy;
        Closed += (_, _) => { ImportText.Clear(); Accounts.ItemsSource = null; _pending.Clear(); };
    }

    /// <summary>重新打开已有工具时刷新列表；正在编辑或审核时保留当前操作。</summary>
    internal async void Reactivated()
    {
        if (!_busy && !_reviewing && !_editing) await RefreshAccountsAsync();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        Actions.IsEnabled = ImportText.IsEnabled = ListActions.IsEnabled = SearchInput.IsEnabled = Accounts.IsEnabled = !busy;
        ReviewButton.IsEnabled = _pending.Count > 0;
        ReviewButton.Content = _pending.Count > 0 ? $"审核重复账号（{_pending.Count}）" : "审核重复账号";
    }

    /// <summary>文件仅在用户选择后读取，内容留在工具内，取消不创建或修改账号。</summary>
    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (!FinishEditing()) return;
        var dialog = new OpenFileDialog { Title = "选择账号 TXT", Filter = "文本文件|*.txt|所有文件|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        SetBusy(true);
        try { ImportText.Text = await File.ReadAllTextAsync(dialog.FileName); Feedback.Text = "文件已读取，点击“开始导入”保存账号。"; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Feedback.Text = "无法读取文件，请检查文件位置和权限。"; }
        finally { SetBusy(false); }
    }

    /// <summary>所有非重复项完成后才弹出审核，不因一条重复阻断后续行。</summary>
    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (!FinishEditing()) return;
        if (string.IsNullOrWhiteSpace(ImportText.Text)) { Feedback.Text = "请粘贴账号文本或先读取 TXT。"; return; }
        SetBusy(true);
        var text = ImportText.Text;
        try
        {
            var result = await Task.Run(() => _service.ImportAsync(text));
            _pending.AddRange(result.Conflicts);
            await LoadAccountsAsync();
            var details = string.Join("；", result.Issues.Take(4).Select(issue => $"第{issue.LineNumber}行：{issue.Message}"));
            _importSummary = $"新增 {result.AddedCount} 条，格式错误 {result.Issues.Count} 条。{details}";
            Feedback.Text = $"{_importSummary} 待审核 {_pending.Count} 条。";
        }
        catch (Exception) { Feedback.Text = "导入未完成，请刷新列表检查已保存的账号，再重试剩余记录。"; }
        finally { SetBusy(false); }
        if (_pending.Count > 0) await ReviewPendingAsync();
    }

    private async void Review_Click(object sender, RoutedEventArgs e)
    { if (FinishEditing()) await ReviewPendingAsync(); }

    /// <summary>默认预填新数据，只有点击替换才写入；关闭或稍后审核保留剩余队列。</summary>
    private async Task ReviewPendingAsync()
    {
        _reviewing = true;
        var replaced = 0;
        var skipped = 0;
        try
        {
            while (_pending.Count > 0)
            {
                var dialog = new AccountImportReviewWindow(_service, _pending[0]) { Owner = this };
                var accepted = dialog.ShowDialog() == true;
                if (dialog.Deferred) break;
                _pending.RemoveAt(0);
                if (accepted) replaced++; else skipped++;
            }
            SetBusy(false);
            if (replaced > 0) await RefreshAccountsAsync();
            Feedback.Text = $"{_importSummary} 审核：替换 {replaced} 条，跳过 {skipped} 条，待审核 {_pending.Count} 条。";
        }
        finally { _reviewing = false; }
    }

    /// <summary>用户选择文件位置后写入明文，不上传；无凭据的启动记录不参与导出。</summary>
    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!FinishEditing()) return;
        var dialog = new SaveFileDialog
        {
            Title = "导出账号（TXT 包含明文密码）",
            Filter = "文本文件|*.txt",
            DefaultExt = ".txt",
            FileName = "启动器账号.txt",
        };
        if (dialog.ShowDialog(this) != true) return;
        SetBusy(true);
        try
        {
            var accounts = await Task.Run(_service.ListAsync);
            var text = AccountTransferFormat.Export(accounts);
            await File.WriteAllTextAsync(dialog.FileName, text, new UTF8Encoding(false));
            Feedback.Text = $"已导出 {accounts.Count} 条账号。TXT 包含明文密码，请妥善保存。";
        }
        catch (ArgumentException exception) { Feedback.Text = exception.Message; }
        catch (Exception) { Feedback.Text = "导出失败，请检查账号库及目标文件是否可写。"; }
        finally { SetBusy(false); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    { if (FinishEditing()) await RefreshAccountsAsync(); }

    private async Task RefreshAccountsAsync()
    {
        SetBusy(true);
        try { await LoadAccountsAsync(); }
        catch (Exception) { Feedback.Text = "读取账号列表失败，请检查本地账号库后重试。"; }
        finally { SetBusy(false); }
    }

    private async Task LoadAccountsAsync()
    {
        var selected = Accounts.SelectedItems.Cast<AccountRow>().Select(row => row.Id).ToHashSet();
        var rows = (await Task.Run(_service.ListAccountsAsync)).Select(account => new AccountRow(account)).ToList();
        _accountsView = CollectionViewSource.GetDefaultView(rows);
        _accountsView.Filter = value =>
        {
            var row = (AccountRow)value;
            var search = SearchInput.Text.Trim();
            return row.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) || row.Username.Contains(search, StringComparison.OrdinalIgnoreCase);
        };
        Accounts.ItemsSource = _accountsView;
        foreach (var row in rows.Where(row => selected.Contains(row.Id) && _accountsView.Contains(row))) Accounts.SelectedItems.Add(row);
        UpdateSelection();
    }

    /// <summary>按名称或账号筛选，切换搜索条件时清空选择，避免误删隐藏项。</summary>
    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        if (_accountsView is null) return;
        Accounts.CancelEdit(DataGridEditingUnit.Cell);
        Accounts.CancelEdit(DataGridEditingUnit.Row);
        _editing = false;
        Accounts.UnselectAll();
        _accountsView.Refresh();
        UpdateSelection();
    }

    private void Accounts_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        if (_accountsView is null) return;
        var count = Accounts.Items.Count;
        var selected = Accounts.SelectedItems.Count;
        SelectionSummary.Text = $"共 {count} 条，已选 {selected} 条";
        DeleteButton.IsEnabled = selected > 0;
        _updatingSelection = true;
        SelectAllInput.IsChecked = selected == 0 ? false : selected == count ? true : null;
        _updatingSelection = false;
    }

    private void SelectAll_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingSelection) return;
        if (SelectAllInput.IsChecked == true) Accounts.SelectAll();
        else if (SelectAllInput.IsChecked == false) Accounts.UnselectAll();
    }

    /// <summary>勾选只改变该行选择，不清除其他已选行。</summary>
    private void RowCheck_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>((DependencyObject)sender) is { } row) row.IsSelected = !row.IsSelected;
        e.Handled = true;
    }

    /// <summary>确认的 ID 快照一次性删除，SQLite 同事务清除对应凭据。</summary>
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!FinishEditing()) return;
        var ids = Accounts.SelectedItems.Cast<AccountRow>().Select(row => row.Id).ToArray();
        if (ids.Length == 0) return;
        if (MessageBox.Show(this, $"删除选中的 {ids.Length} 条本地账号及保存的密码？", "确认批量删除",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            await Task.Run(() => _service.DeleteAsync(ids));
            _pending.RemoveAll(conflict => ids.Contains(conflict.Existing.Id));
            await LoadAccountsAsync();
            Feedback.Text = $"已删除 {ids.Length} 条本地账号。";
        }
        catch (Exception) { Feedback.Text = "删除失败，请刷新列表后重试。"; }
        finally { SetBusy(false); }
    }

    private void Accounts_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        e.Cancel = !_editRequested && !Keyboard.IsKeyDown(Key.F2);
        if (!e.Cancel) _editing = true;
    }

    private void Accounts_DoubleClick(object sender, MouseButtonEventArgs e) => RequestEdit(e);
    private void Accounts_RightClick(object sender, MouseButtonEventArgs e) => RequestEdit(e);

    /// <summary>双击和右键直接唤起当前单元格的编辑框；名称、账号、密码采用同一提交路径。</summary>
    private void RequestEdit(MouseButtonEventArgs e)
    {
        if (_busy || FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject) is not { } cell || cell.Column.IsReadOnly) return;
        if (!FinishEditing()) return;
        Accounts.CurrentCell = new DataGridCellInfo(cell.DataContext, cell.Column);
        _editRequested = true;
        Accounts.BeginEdit();
        _editRequested = false;
        e.Handled = true;
    }

    private bool FinishEditing() => !_busy && Accounts.CommitEdit(DataGridEditingUnit.Cell, true) && Accounts.CommitEdit(DataGridEditingUnit.Row, true);

    /// <summary>先原子保存，再结束单元格编辑；失败回到原值，避免界面显示未保存的账号或密码。</summary>
    private async void Accounts_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Cancel) { _editing = false; return; }
        e.Cancel = true;
        if (_savingCell) return;
        var row = (AccountRow)e.Row.Item;
        var value = ((TextBox)e.EditingElement).Text;
        var original = row.Snapshot;
        var updated = e.Column.SortMemberPath switch
        {
            nameof(AccountRow.DisplayName) => original with { DisplayName = value },
            nameof(AccountRow.Username) => original with { Username = value },
            _ => original with { Password = value },
        };
        _savingCell = true;
        SetBusy(true);
        var saved = original;
        try
        {
            saved = await Task.Run(() => _service.SaveManagedAccountAsync(updated));
            Accounts.CancelEdit(DataGridEditingUnit.Cell);
            Accounts.CancelEdit(DataGridEditingUnit.Row);
            Feedback.Text = "已保存账号修改。";
        }
        catch (AccountAlreadyExistsException) { Feedback.Text = "该4399账号已存在，修改未保存。请使用不同账号或审核重复项。"; }
        catch (ArgumentException) { Feedback.Text = "名称需为1–40字；请完整填写账号和密码，已保存密码留空保留。账号最多256字，密码最多1024字。"; }
        catch (Exception) { Feedback.Text = "修改未保存，请刷新列表检查本地账号库。"; }
        finally
        {
            Accounts.CancelEdit(DataGridEditingUnit.Cell);
            Accounts.CancelEdit(DataGridEditingUnit.Row);
            row.Update(saved);
            _accountsView!.Refresh();
            _editing = _savingCell = false;
            SetBusy(false);
            UpdateSelection();
        }
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T found) return found;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    /// <summary>行内保存成功后才替换快照并通知界面；凭据不进入对象字符串。</summary>
    private sealed class AccountRow(SavedAccount snapshot) : INotifyPropertyChanged
    {
        public SavedAccount Snapshot { get; private set; } = snapshot;
        public Guid Id => Snapshot.Id;
        public string DisplayName { get; set; } = snapshot.DisplayName;
        public string Username { get; set; } = snapshot.Username;
        public string Password { get; set; } = snapshot.Password;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Update(SavedAccount account)
        {
            Snapshot = account;
            DisplayName = account.DisplayName;
            Username = account.Username;
            Password = account.Password;
            PropertyChanged?.Invoke(this, new(nameof(DisplayName)));
            PropertyChanged?.Invoke(this, new(nameof(Username)));
            PropertyChanged?.Invoke(this, new(nameof(Password)));
        }
        public override string ToString() => "[账号管理行凭据已隐藏]";
    }
}
