using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using BqtjLauncher.Runtime.Flash;

namespace BqtjLauncher.Application.Tests;

/// <summary>用真实异步管道和虚构宿主验证回执生命周期，不启动游戏或访问用户数据。</summary>
public sealed class NativeHostCommandChannelTests
{
    [Theory]
    [InlineData("reload", "reload-ok")]
    [InlineData("show", "show-ok")]
    [InlineData("speed 2", "speed-ok")]
    [InlineData("display-ready", "display-pending")]
    public async Task MatchingReplyCompletesRequest(string command, string reply)
    {
        using var fixture = await PipeFixture.CreateAsync();
        var pending = fixture.Commands.RequestAsync(command);
        var request = await fixture.ReadRequestAsync();
        Assert.EndsWith(" " + command, request);
        await fixture.ReplyAsync(request, reply);
        Assert.Equal(reply, await pending);
    }

    [Fact]
    public async Task MissingReplyTimesOutAndNextReloadCanSucceed()
    {
        using var fixture = await PipeFixture.CreateAsync();
        var first = fixture.Commands.RequestAsync("reload");
        await fixture.ReadRequestAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => first);

        var second = fixture.Commands.RequestAsync("reload");
        var request = await fixture.ReadRequestAsync();
        await fixture.ReplyAsync(request, "reload-ok");
        Assert.Equal("reload-ok", await second);
    }

    [Fact]
    public async Task LateReplyCannotCompleteAnotherReloadWithTheSameReplyText()
    {
        using var fixture = await PipeFixture.CreateAsync();
        var first = fixture.Commands.RequestAsync("reload");
        var oldRequest = await fixture.ReadRequestAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => first);

        var second = fixture.Commands.RequestAsync("reload");
        var currentRequest = await fixture.ReadRequestAsync();
        await fixture.ReplyAsync(oldRequest, "reload-ok");
        // 观察标记确保旧回执已经被处理，避免用固定 sleep 掩盖时序缺陷。
        await fixture.HostOutput.WriteLineAsync("stage late-reply-drained");
        await fixture.WaitForObservedAsync("stage late-reply-drained");
        Assert.False(second.IsCompleted);
        await fixture.ReplyAsync(currentRequest, "reload-ok");
        Assert.Equal("reload-ok", await second);
    }

    [Fact]
    public async Task TimedOutPartialReplyIsDrainedWithoutCorruptingNextReply()
    {
        using var fixture = await PipeFixture.CreateAsync();
        var first = fixture.Commands.RequestAsync("reload");
        var oldRequest = await fixture.ReadRequestAsync();
        await fixture.HostOutput.WriteAsync($"reply {oldRequest.Split(' ')[1]} reload-");
        await fixture.HostOutput.FlushAsync();
        await Assert.ThrowsAsync<TimeoutException>(() => first);

        var second = fixture.Commands.RequestAsync("reload");
        var currentRequest = await fixture.ReadRequestAsync();
        await fixture.HostOutput.WriteLineAsync("ok");
        await fixture.ReplyAsync(currentRequest, "reload-ok");
        Assert.Equal("reload-ok", await second);
    }

    [Fact]
    public async Task CancellingRequestDoesNotLeaveAReadForTheNextRequest()
    {
        using var fixture = await PipeFixture.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var first = fixture.Commands.RequestAsync("show", cancellation.Token);
        var oldRequest = await fixture.ReadRequestAsync();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        var second = fixture.Commands.RequestAsync("reload");
        var currentRequest = await fixture.ReadRequestAsync();
        await fixture.ReplyAsync(oldRequest, "show-ok");
        await fixture.ReplyAsync(currentRequest, "reload-ok");
        Assert.Equal("reload-ok", await second);
    }

    [Fact]
    public async Task ClosingChannelCompletesPendingRequestWithoutWaitingForTimeout()
    {
        using var fixture = await PipeFixture.CreateAsync();
        var pending = fixture.Commands.RequestAsync("reload");
        await fixture.ReadRequestAsync();
        fixture.Commands.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(1)));
        await fixture.Commands.Completion.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task HostExitFailsPendingAndFutureRequests()
    {
        using var fixture = await PipeFixture.CreateAsync();
        var pending = fixture.Commands.RequestAsync("reload");
        await fixture.ReadRequestAsync();
        fixture.CloseHost();
        await Assert.ThrowsAsync<IOException>(() => pending);
        await Assert.ThrowsAsync<IOException>(() => fixture.Commands.RequestAsync("reload"));
    }

    /// <summary>客户端走生产通道，服务端只提供可控的虚构回执与关闭行为。</summary>
    private sealed class PipeFixture : IDisposable
    {
        private readonly NamedPipeServerStream _server;
        private readonly NamedPipeClientStream _client;
        private readonly StreamReader _hostInput;
        private readonly Channel<string> _observed = Channel.CreateUnbounded<string>();
        public StreamWriter HostOutput { get; }
        public NativeHostCommandChannel Commands { get; }

        private PipeFixture(NamedPipeServerStream server, NamedPipeClientStream client)
        {
            _server = server;
            _client = client;
            _hostInput = new StreamReader(server, Encoding.UTF8, false, 1024, true);
            HostOutput = new StreamWriter(server, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
            Commands = new NativeHostCommandChannel(
                new StreamWriter(client, new UTF8Encoding(false), 1024, true) { AutoFlush = true },
                new StreamReader(client, Encoding.UTF8, false, 1024, true),
                line => _observed.Writer.TryWrite(line), TimeSpan.FromMilliseconds(500));
        }

        public static async Task<PipeFixture> CreateAsync()
        {
            var name = "bqtj-test-" + Guid.NewGuid().ToString("N");
            var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var connect = client.ConnectAsync(timeout.Token);
            await server.WaitForConnectionAsync(timeout.Token);
            await connect;
            return new PipeFixture(server, client);
        }

        public async Task<string> ReadRequestAsync() =>
            await _hostInput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3))
                ?? throw new IOException("虚构宿主输入已关闭。");

        public Task ReplyAsync(string request, string reply) =>
            HostOutput.WriteLineAsync($"reply {request.Split(' ')[1]} {reply}");

        public async Task WaitForObservedAsync(string expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (await _observed.Reader.ReadAsync(timeout.Token) != expected) { }
        }

        public void CloseHost() => _server.Dispose();

        public void Dispose()
        {
            Commands.Dispose();
            _client.Dispose();
            _server.Dispose();
        }
    }
}
