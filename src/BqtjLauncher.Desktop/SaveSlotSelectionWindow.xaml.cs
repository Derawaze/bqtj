using System.Windows;
using System.Windows.Controls;
using BqtjLauncher.Application;

namespace BqtjLauncher.Desktop;

/// <summary>首次运行的八档选择；只选择编号和显示缓存名称，不启动游戏或自动读取名称。</summary>
public partial class SaveSlotSelectionWindow : Window
{
    public int? SelectedSave { get; private set; }

    public SaveSlotSelectionWindow(ScriptSaveSelection request, IReadOnlyList<SaveSlot> names)
    {
        InitializeComponent();
        AccountLabel.Text = request.Account.Name + " · 选择存档";
        PlanLabel.Text = request.PlanName;
        Slots.ItemsSource = Enumerable.Range(1, 8).Select(number =>
            new SaveChoice(number, "存档" + number, names.FirstOrDefault(slot => slot.Number == number)?.Name ?? "未读取名称")).ToArray();
    }

    /// <summary>确认实际点击的编号；缓存名称仅显示，不参与执行身份。</summary>
    private void Select_Click(object sender, RoutedEventArgs args)
    {
        SelectedSave = (int)((Button)sender).Tag;
        DialogResult = true;
    }

    private sealed record SaveChoice(int Number, string Title, string Name);
}
