using System.Windows;
using System.Windows.Controls;
using BqtjLauncher.Domain;
using WpfButton = System.Windows.Controls.Button;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfMessageBox = System.Windows.MessageBox;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace BqtjLauncher.Runtime.Flash;

internal sealed class CustomSpeedDialog : Window
{
    private readonly WpfTextBox _input;

    public CustomSpeedDialog(Window owner, SpeedMultiplier initialValue)
    {
        Owner = owner;
        Title = "自定义变速";
        Width = 340;
        Height = 190;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        _input = new WpfTextBox
        {
            Text = initialValue.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Height = 30,
            Margin = new Thickness(0, 8, 0, 12),
            VerticalContentAlignment = VerticalAlignment.Center,
        };

        var accept = new WpfButton
        {
            Content = "确定",
            Width = 88,
            Height = 30,
            IsDefault = true,
            Margin = new Thickness(4),
        };
        accept.Click += (_, _) => Accept();

        var cancel = new WpfButton
        {
            Content = "取消",
            Width = 88,
            Height = 30,
            IsCancel = true,
            Margin = new Thickness(4),
        };

        var buttons = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            HorizontalAlignment = WpfHorizontalAlignment.Right,
        };
        buttons.Children.Add(accept);
        buttons.Children.Add(cancel);

        var content = new StackPanel { Margin = new Thickness(18) };
        content.Children.Add(new TextBlock { Text = "请输入 0.01 到 100 之间的倍率：" });
        content.Children.Add(_input);
        content.Children.Add(buttons);
        Content = content;

        Loaded += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
    }

    public SpeedMultiplier SelectedMultiplier { get; private set; } = SpeedMultiplier.Original;

    private void Accept()
    {
        if (!SpeedMultiplier.TryParse(_input.Text, out var multiplier))
        {
            WpfMessageBox.Show(
                this,
                "倍率必须是 0.01 到 100 之间的数字。",
                "自定义变速",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            _input.Focus();
            _input.SelectAll();
            return;
        }

        SelectedMultiplier = multiplier;
        DialogResult = true;
    }
}
