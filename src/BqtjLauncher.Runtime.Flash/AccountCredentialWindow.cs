using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BqtjLauncher.Application;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Button = System.Windows.Controls.Button;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>只读查看当前容器账号信息，默认隐藏密码，显式切换后显示。</summary>
internal sealed class AccountCredentialWindow : Window
{
    public AccountCredentialWindow(AccountCredential? credential)
    {
        Title = "账号密码"; Width = 400; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(14, 19, 27)); Foreground = Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "4399账号" });
        var username = new TextBox { Text = credential?.Username ?? "未保存", IsReadOnly = true, Margin = new Thickness(0, 8, 0, 16) };
        panel.Children.Add(username);
        panel.Children.Add(new TextBlock { Text = "密码" });
        var password = new TextBox { IsReadOnly = true, Text = credential is null ? "未保存" : "••••••••", Margin = new Thickness(0, 8, 0, 12) };
        panel.Children.Add(password);
        var show = new CheckBox { Content = "显示密码", Foreground = Brushes.White, IsEnabled = credential is not null };
        show.Checked += (_, _) => password.Text = credential?.Password ?? string.Empty;
        show.Unchecked += (_, _) => password.Text = "••••••••";
        panel.Children.Add(show);
        var close = new Button { Content = "关闭", IsCancel = true, Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(close); Content = panel;
        Closed += (_, _) => { username.Clear(); password.Clear(); };
    }
}
