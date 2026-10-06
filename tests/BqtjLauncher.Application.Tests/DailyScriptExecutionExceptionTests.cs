namespace BqtjLauncher.Application.Tests;

/// <summary>只暴露固定阶段、错误码及有界尺寸，未知文字不能进入界面。</summary>
public sealed class DailyScriptExecutionExceptionTests
{
    [Fact]
    public void ReportsUsefulDimensionsWithoutOriginalException()
    {
        var error = new DailyScriptExecutionException("绑定游戏窗口", DailyScriptFailureCode.UnsupportedSize, 1425, 900);
        Assert.Contains("1425×900", error.Describe());
        Assert.StartsWith("绑定游戏窗口", error.Describe());
    }

    [Fact]
    public void UnknownWorkerTextAndInvalidCodesAreNotDisplayed()
    {
        var error = new DailyScriptExecutionException("虚构敏感原文", (DailyScriptFailureCode)999, -1, int.MaxValue);
        Assert.Equal("初始化视觉组件", error.Stage);
        Assert.DoesNotContain("虚构敏感原文", error.Describe());
        Assert.Equal(DailyScriptFailureCode.Initialization, error.Code);
        Assert.Equal(0, error.Width);
        Assert.Equal(0, error.Height);
    }
}
