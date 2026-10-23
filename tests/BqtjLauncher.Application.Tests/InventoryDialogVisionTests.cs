using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

/// <summary>只用匿名弹窗裁剪回放固定按钮布局，结果标题/数字改变不影响流程。</summary>
public sealed class InventoryDialogVisionTests
{
    [Theory]
    [InlineData("confirm", "Confirmation", false)]
    [InlineData("result", "Result", false)]
    [InlineData("empty", "Result", false)]
    [InlineData("confirm", "Confirmation", true)]
    [InlineData("result", "Result", true)]
    public void RealDialogLayoutDoesNotDependOnItsText(string file, string expected, bool eraseText)
    {
        using var scene = new Bitmap(950, 600, PixelFormat.Format24bppRgb);
        using var dialog = new Bitmap(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sky-top-inventory-" + file + ".png"));
        using (var graphics = Graphics.FromImage(scene))
        {
            graphics.Clear(Color.Navy); graphics.DrawImageUnscaled(dialog, 255, 135);
            if (eraseText) graphics.FillRectangle(Brushes.Black, 300, 170, 350, 105);
        }
        Assert.Equal(expected, InventoryDialogVision.Read(Pixels(scene)).ToString());
    }

    [Fact]
    public void RedSceneObjectsCannotBecomeAnInventoryDialogWithoutItsPanel()
    {
        using var scene = new Bitmap(950, 600, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(scene))
        {
            graphics.Clear(Color.Navy); graphics.FillRectangle(Brushes.Red, 375, 303, 90, 30);
            graphics.FillRectangle(Brushes.Red, 485, 303, 90, 30);
        }
        Assert.Equal(InventoryDialogVision.Kind.None, InventoryDialogVision.Read(Pixels(scene)));
    }

    private static ScriptPixels Pixels(Bitmap bitmap)
    {
        var data = bitmap.LockBits(new(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var bytes = new byte[bitmap.Width * bitmap.Height * 3];
        try
        {
            for (var y = 0; y < bitmap.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * bitmap.Width * 3, bitmap.Width * 3);
        }
        finally { bitmap.UnlockBits(data); }
        return new(bitmap.Width, bitmap.Height, bytes);
    }
}
