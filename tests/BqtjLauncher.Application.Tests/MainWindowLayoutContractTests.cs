using System.IO;
using System.Xml.Linq;

namespace BqtjLauncher.Application.Tests;

/// <summary>
/// 锁定管理面板关键布局与绑定契约，防止控件落入不存在的 Grid 行后互相覆盖。
/// </summary>
public sealed class MainWindowLayoutContractTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void AccountEditorAndListUseDistinctDeclaredRows()
    {
        var document = XDocument.Load(FindMainWindowXaml());
        Assert.Empty(document.Descendants(Presentation + "TextBox"));
        Assert.DoesNotContain(document.Descendants(Presentation + "TextBlock"), e => HasAttribute(e, "Text", "账号名称"));
        var accountPanelGrid = document.Descendants(Presentation + "TextBlock")
            .Single(e => HasAttribute(e, "Text", "账号")).Parent!;
        var declaredRowCount = accountPanelGrid
            .Element(Presentation + "Grid.RowDefinitions")!
            .Elements(Presentation + "RowDefinition")
            .Count();
        var directChildRows = accountPanelGrid
            .Elements()
            .Where(element => element.Name != Presentation + "Grid.RowDefinitions")
            .Select(element => GetGridRow(element))
            .ToArray();

        Assert.All(directChildRows, row => Assert.InRange(row, 0, declaredRowCount - 1));
        Assert.Contains(0, directChildRows);
        Assert.Contains(1, directChildRows);
    }

    [Fact]
    public void AccountListBindsVisibleNameText()
    {
        var document = XDocument.Load(FindMainWindowXaml());
        var accountList = document
            .Descendants(Presentation + "ListBox")
            .Single(element => HasAttribute(element, "AutomationProperties.Name", "账号列表"));
        var displayName = accountList
            .Descendants(Presentation + "TextBlock")
            .Single(element => HasAttribute(element, "Text", "{Binding DisplayName}"));

        Assert.True(HasAttribute(displayName, "Foreground", "{StaticResource TextBrush}"));
    }

    private static bool HasAttribute(XElement element, string localName, string value) =>
        element.Attributes().Any(attribute =>
            attribute.Name.LocalName == localName
            && attribute.Value == value);

    private static int GetGridRow(XElement element)
    {
        var value = element.Attributes()
            .SingleOrDefault(attribute => attribute.Name.LocalName == "Grid.Row")
            ?.Value;
        return value is null ? 0 : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string FindMainWindowXaml()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(
                directory.FullName,
                "src",
                "BqtjLauncher.Desktop",
                "MainWindow.xaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("无法定位管理面板 MainWindow.xaml。", "MainWindow.xaml");
    }
}
