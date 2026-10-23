using BqtjLauncher.Application;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>950×600下背包弹窗的固定布局；只检查灰色面板和红色按钮，不读取拆解文案。</summary>
internal static class InventoryDialogVision
{
    internal enum Kind { None, Confirmation, Result }

    /// <summary>区分双按钮确认框与单按钮结果框；None表示切换帧或非背包弹窗。</summary>
    internal static Kind Read(ScriptPixels image)
    {
        // 三处面板底色共同限定弹窗；背包格子或战场中的红色不能单独触发确认。
        foreach (var x in new[] { 270, 475, 680 })
        {
            var at = (150 * image.Width + x) * 3;
            var b = image.Bgr[at]; var g = image.Bgr[at + 1]; var r = image.Bgr[at + 2];
            if (r is < 16 or > 85 || Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) > 14) return Kind.None;
        }
        // 取按钮文字下方的色带，避免“确定/取消”的白字以及鼠标悬停亮度影响分类。
        var left = RedButton(390); var middle = RedButton(475); var right = RedButton(550);
        if (left && right && !middle) return Kind.Confirmation;
        if (middle && !left && !right) return Kind.Result;
        return Kind.None;

        bool RedButton(int center)
        {
            var red = 0;
            for (var y = 325; y <= 327; y++) for (var x = center - 5; x <= center + 5; x++)
            {
                var at = (y * image.Width + x) * 3;
                var b = image.Bgr[at]; var g = image.Bgr[at + 1]; var r = image.Bgr[at + 2];
                if (r > 45 && r - g > 30 && r - b > 30) red++;
            }
            return red >= 25;
        }
    }
}
