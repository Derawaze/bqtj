using System.Windows;
using BqtjLauncher.Application;

namespace BqtjLauncher.Desktop;

/// <summary>人工确认一条重复记录；默认使用新数据，关闭窗口不覆盖原记录。</summary>
public partial class AccountImportReviewWindow : Window
{
    private readonly AccountTransferService _service;
    private readonly AccountImportConflict _conflict;
    private bool _saving;
    public bool Deferred { get; private set; } = true;

    public AccountImportReviewWindow(AccountTransferService service, AccountImportConflict conflict)
    {
        InitializeComponent();
        _service = service;
        _conflict = conflict;
        Summary.Text = $"第 {conflict.Incoming.LineNumber} 行：4399账号已存在，其他非重复记录已继续导入。";
        ExistingSummary.Text = $"原名称：{conflict.Existing.DisplayName}\n原账号：{conflict.Existing.Username}\n原密码：已保存（不显示）";
        DisplayNameInput.Text = conflict.Incoming.DisplayName;
        UsernameInput.Text = conflict.Incoming.Username;
        PasswordInput.Password = conflict.Incoming.Password;
        Closing += (_, e) => e.Cancel = _saving;
        Closed += (_, _) => { PasswordInput.Clear(); VisiblePasswordInput.Clear(); };
    }

    private void ShowPassword_Changed(object sender, RoutedEventArgs e)
    {
        if (ShowPasswordInput.IsChecked == true) VisiblePasswordInput.Text = PasswordInput.Password;
        else { PasswordInput.Password = VisiblePasswordInput.Text; VisiblePasswordInput.Clear(); }
        PasswordInput.Visibility = ShowPasswordInput.IsChecked == true ? Visibility.Collapsed : Visibility.Visible;
        VisiblePasswordInput.Visibility = ShowPasswordInput.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>只有明确确认才提交替换；重复或无效输入留在窗口，用户可以修改或跳过。</summary>
    private async void Replace_Click(object sender, RoutedEventArgs e)
    {
        _saving = true;
        Actions.IsEnabled = false;
        try
        {
            var password = ShowPasswordInput.IsChecked == true ? VisiblePasswordInput.Text : PasswordInput.Password;
            var displayName = DisplayNameInput.Text;
            var username = UsernameInput.Text;
            await Task.Run(() => _service.ReplaceAsync(_conflict, displayName, username, password));
            _saving = false;
            Deferred = false;
            DialogResult = true;
        }
        catch (AccountAlreadyExistsException) { Feedback.Text = "这个账号还与另一条记录重复，请修改账号或跳过。"; }
        catch (ArgumentException) { Feedback.Text = "请完整填写名称、账号和密码，三项均不能包含空白。"; }
        catch (Exception) { Feedback.Text = "替换失败，原记录可能已删除或本地账号库不可用。"; }
        finally { _saving = false; Actions.IsEnabled = true; }
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        Deferred = false;
        DialogResult = false;
    }
}
