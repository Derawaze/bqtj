using System.ComponentModel;
using System.Windows;
using BqtjLauncher.Application;

namespace BqtjLauncher.Desktop;

/// <summary>固定账号的名称、总统权益及凭据编辑窗口，密码默认掩码，用户可切换可见密码，取消不写入。</summary>
public partial class AccountEditorWindow : Window
{
    private readonly IAccountEditor _editor;
    private readonly Guid _accountId;
    private bool _saving;
    public Guid SavedAccountId { get; private set; }
    public bool AccountToolOpened { get; private set; }

    public AccountEditorWindow(IAccountEditor editor, Guid accountId, string displayName, bool hasPresident = false)
    {
        InitializeComponent();
        _editor = editor;
        _accountId = accountId;
        SavedAccountId = accountId;
        PresidentInput.IsChecked = hasPresident;
        PasswordHint.Text = "自动登录时请同时填写4399账号和密码；两项同时留空，可在游戏中手动登录。";
        if (accountId == Guid.Empty)
        {
            Title = "新增账号";
            ClearInput.Visibility = Visibility.Collapsed;
            BatchToolButton.Visibility = Visibility.Visible;
        }
        DisplayNameInput.Text = displayName;
        Loaded += LoadCredentialAsync;
        Closing += (_, e) => e.Cancel = _saving;
        Closed += (_, _) => { PasswordInput.Clear(); VisiblePasswordInput.Clear(); UsernameInput.Clear(); };
    }

    private async void LoadCredentialAsync(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_accountId == Guid.Empty) { SaveButton.IsEnabled = true; return; }
            var credential = await _editor.ReadCredentialAsync(_accountId);
            if (!IsVisible) return;
            UsernameInput.Text = credential?.Username ?? string.Empty;
            PasswordInput.Password = credential?.Password ?? string.Empty;
            if (credential is not null)
                PasswordHint.Text = "已有密码已填入；不修改或清空密码框都沿用原密码。要删除保存信息，请勾选下方清除。";
            SaveButton.IsEnabled = true;
        }
        catch (Exception) { Feedback.Text = "读取账号信息失败，请关闭后重试。"; }
    }

    /// <summary>仅在切换时同步输入控件，隐藏后移除可见副本，避免切换丢失编辑内容。</summary>
    private void ShowPassword_Changed(object sender, RoutedEventArgs e)
    {
        var show = ShowPasswordInput.IsChecked == true;
        if (show) VisiblePasswordInput.Text = PasswordInput.Password;
        else { PasswordInput.Password = VisiblePasswordInput.Text; VisiblePasswordInput.Clear(); }
        PasswordInput.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        VisiblePasswordInput.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>快捷打开独立进程；关闭新增弹窗不会关闭工具或中断导入。</summary>
    private void OpenBatchTool_Click(object sender, RoutedEventArgs e)
    {
        try { AccountToolLauncher.Open(); AccountToolOpened = true; }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        { Feedback.Text = "无法打开账号管理工具，请尝试双击包内的 BqtjAccountTool.exe。"; }
    }

    private void ClearInput_Changed(object sender, RoutedEventArgs e)
    {
        UsernameInput.IsEnabled = PasswordInput.IsEnabled = VisiblePasswordInput.IsEnabled = ShowPasswordInput.IsEnabled = ClearInput.IsChecked != true;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        _saving = true;
        SaveButton.IsEnabled = CancelButton.IsEnabled = false;
        try
        {
            var password = ShowPasswordInput.IsChecked == true ? VisiblePasswordInput.Text : PasswordInput.Password;
            if (_accountId == Guid.Empty)
                SavedAccountId = await _editor.CreateAsync(DisplayNameInput.Text, UsernameInput.Text, password, PresidentInput.IsChecked == true);
            else
                await _editor.SaveAsync(_accountId, DisplayNameInput.Text, UsernameInput.Text, password, ClearInput.IsChecked == true, PresidentInput.IsChecked == true);
            _saving = false;
            DialogResult = true;
        }
        catch (AccountAlreadyExistsException exception) { Feedback.Text = exception.Message; }
        catch (ArgumentException)
        {
            Feedback.Text = _accountId == Guid.Empty
                ? "名称需为1–40字；4399账号和密码需同时填写，或同时留空。"
                : "请检查名称（1–40字）及4399账号；只有已有保存密码时，密码留空才能沿用原值。";
        }
        catch (Exception) { Feedback.Text = "保存失败，请重试。账号可能已被删除，或本地数据库不可用。"; }
        finally { _saving = false; SaveButton.IsEnabled = CancelButton.IsEnabled = true; }
    }
}
