using System.Buffers.Binary;
using System.IO;
using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

/// <summary>使用虚构目标验证限长本机通信与身份隔离，不创建游戏窗口或读取用户数据。</summary>
public sealed class AutomationSessionPipeTests
{
    [Fact]
    public async Task TargetRoundTripAndServerShutdown()
    {
        var id = Guid.NewGuid();
        var target = new AutomationWindowTarget(id, Guid.NewGuid(), Environment.ProcessId, 123, 456, 950, 600);
        var server = new AutomationSessionServer(id, Environment.ProcessId, (request, _) =>
            Task.FromResult(new AutomationSessionPipe.Response(request.SessionId, target)));
        try
        {
            var response = await AutomationSessionPipe.SendAsync(Environment.ProcessId, new(id, "target"), default);
            Assert.Equal(target, response.Target);
        }
        finally { server.Dispose(); server.Dispose(); }
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RejectsDifferentContainerProcess()
    {
        var id = Guid.NewGuid();
        using var server = new AutomationSessionServer(id, Environment.ProcessId, (request, _) =>
            Task.FromResult(new AutomationSessionPipe.Response(request.SessionId)));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AutomationSessionPipe.SendAsync(Environment.ProcessId + 1000, new(id, "target"), default));
    }

    [Fact]
    public async Task RejectsResponseFromDifferentSession()
    {
        var id = Guid.NewGuid();
        using var server = new AutomationSessionServer(id, Environment.ProcessId, (_, _) =>
            Task.FromResult(new AutomationSessionPipe.Response(Guid.NewGuid())));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AutomationSessionPipe.SendAsync(Environment.ProcessId, new(id, "target"), default));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(4097)]
    public async Task InvalidLengthIsRejectedBeforePayloadAllocation(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AutomationSessionPipe.ReadAsync<AutomationSessionPipe.Request>(stream, default));
    }

    [Fact]
    public async Task OversizedMessageCannotBeSent()
    {
        using var stream = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AutomationSessionPipe.WriteAsync(stream, new string('x', 5000), default));
        Assert.Equal(0, stream.Length);
    }
}
