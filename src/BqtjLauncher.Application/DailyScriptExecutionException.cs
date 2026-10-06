namespace BqtjLauncher.Application;

/// <summary>固定错误分类跨进程传递，不携带异常原文、凭据、截图或OCR文字。</summary>
public enum DailyScriptFailureCode
{
    Initialization, UnsupportedSize, TargetChanged, TargetHidden, DesktopUnavailable,
    Screenshot, Ocr, MenuNotFound, MenuAmbiguous, SaveLayout, Input,
}

/// <summary>保留安全的阶段与原因，使启动错误不再全部折叠为“阶段未通过”。</summary>
public sealed class DailyScriptExecutionException : InvalidOperationException
{
    public string Stage { get; }
    public DailyScriptFailureCode Code { get; }
    public int Width { get; }
    public int Height { get; }

    public DailyScriptExecutionException(string? stage, DailyScriptFailureCode code, int width = 0, int height = 0)
    {
        Stage = IsKnownStage(stage) ? stage! : "初始化视觉组件";
        Code = Enum.IsDefined(code) ? code : DailyScriptFailureCode.Initialization;
        if (width is > 0 and <= 16384 && height is > 0 and <= 16384) { Width = width; Height = height; }
    }

    public static bool IsKnownStage(string? stage) => stage is "初始化视觉组件" or "绑定游戏窗口" or "加载文字识别"
        or "读取存档" or "等待存档选择" or "进入指定存档" or "打开助理" or "选择日常操作复制"
        or "确认运行" or "观察日常进度与最终保存";

    public string Describe() => Stage + "：" + (Code switch
    {
        DailyScriptFailureCode.UnsupportedSize => Width > 0 ? $"当前原生画面为{Width}×{Height}，尚不支持该尺寸。" : "当前画面尺寸不支持。",
        DailyScriptFailureCode.TargetChanged => "绑定的Flash窗口已变化或归属不符。",
        DailyScriptFailureCode.TargetHidden => "游戏窗口尚未显示、已隐藏或最小化。",
        DailyScriptFailureCode.DesktopUnavailable => "桌面不可访问或已锁屏。",
        DailyScriptFailureCode.Screenshot => "后台截图失败或尺寸已变化。",
        DailyScriptFailureCode.Ocr => "文字识别组件加载或识别失败。",
        DailyScriptFailureCode.MenuNotFound => "未找到当前步骤的菜单目标。",
        DailyScriptFailureCode.MenuAmbiguous => "菜单目标不唯一，已拒绝点击。",
        DailyScriptFailureCode.SaveLayout => "存档卡片布局校验未通过。",
        DailyScriptFailureCode.Input => "定向点击未完成，结果不确定，不会自动重试。",
        _ => "视觉组件初始化失败。",
    });
}
