namespace BqtjLauncher.Application.Tests;

/// <summary>隔离真实 WPF 窗口回归，避免与故意停止 Dispatcher 消息处理的退出测试并发。</summary>
[CollectionDefinition(nameof(WpfUiTestGroup), DisableParallelization = true)]
public sealed class WpfUiTestGroup
{
}
