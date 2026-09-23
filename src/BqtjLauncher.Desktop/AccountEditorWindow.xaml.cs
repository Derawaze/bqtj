using System.Windows;
using BqtjLauncher.Application;

namespace BqtjLauncher.Desktop;

/// <summary>固定账号的凭据编辑窗口，默认掩码，用户可切换可见密码，取消不写入。</summary>
public partial class AccountEditorWindow : Window
{
    private readonly IAccountEditor _editor;
    private readonly Guid _accountId;
    private bool _saving;
    public Guid SavedAccountId { get; private set; }

    public AccountEditorWindow(IAccountEditor editor, Guid accountId, string displayName)
    {
        InitializeComponent();
        _editor = editor;
        _accountId = accountId;
        SavedAccountId = accountId;
        if (accountId == Guid.Empty) { Title = "新增账号"; ClearInput.Visibility = Visibility.Collapsed; }
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
                SavedAccountId = await _editor.CreateAsync(DisplayNameInput.Text, UsernameInput.Text, password);
            else
                await _editor.SaveAsync(_accountId, DisplayNameInput.Text, UsernameInput.Text, password, ClearInput.IsChecked == true);
            _saving = false;
            DialogResult = true;
        }
        catch (ArgumentException) { Feedback.Text = "请检查账号名称（1–40字）、4399账号和密码；已保存的密码可留空保留。"; }
        catch (Exception) { Feedback.Text = "保存失败，请重试。账号可能已被删除，或本地数据库不可用。"; }
        finally { _saving = false; SaveButton.IsEnabled = CancelButton.IsEnabled = true; }
    }
}
