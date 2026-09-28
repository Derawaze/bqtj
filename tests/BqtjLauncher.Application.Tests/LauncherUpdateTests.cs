using System.Net;
using System.Net.Http;
using System.Text.Json;
using BqtjLauncher.Infrastructure;

namespace BqtjLauncher.Application.Tests;

/// <summary>通过离线发行响应验证版本比较和下载门槛，不依赖 GitHub 或用户数据。</summary>
public sealed class LauncherUpdateTests
{
    [Theory]
    [InlineData("0.1.9", "发现启动器新版本")]
    [InlineData("0.1.10", "已是最新版本")]
    [InlineData("0.2.0+commit", "已是最新版本")]
    [InlineData("0.0.0-dev.20260928000000+commit", "当前为开发版")]
    public void DescribesNumericVersionsAndDevelopmentBuilds(string local, string expected)
    {
        var release = new LauncherUpdate(new Version(0, 1, 10), new Uri("https://github.com/Derawaze/bqtj/releases/tag/v0.1.10"));
        Assert.Contains(expected, release.Describe(local));
    }

    [Fact]
    public async Task ConstructsReleasePageFromValidatedTag()
    {
        using var client = new HttpClient(new ResponseHandler(HttpStatusCode.OK,
            "{\"tag_name\":\"v0.1.10\",\"draft\":false,\"prerelease\":false,\"html_url\":\"https://example.com\"}"));
        var release = await new GitHubLauncherUpdateSource(client).GetLatestAsync(CancellationToken.None);
        Assert.NotNull(release);
        Assert.Equal("https://github.com/Derawaze/bqtj/releases/tag/v0.1.10", release.ReleaseUri.AbsoluteUri);
    }
    [Theory]
    [InlineData("{\"draft\":true,\"prerelease\":false}")]
    [InlineData("{\"draft\":false,\"prerelease\":true}")]
    public async Task ExcludesUnreleasedBuilds(string body)
    {
        using var client = new HttpClient(new ResponseHandler(HttpStatusCode.OK, body));
        Assert.Null(await new GitHubLauncherUpdateSource(client).GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MissingReleaseIsNotAnUpdate()
    {
        using var client = new HttpClient(new ResponseHandler(HttpStatusCode.NotFound, ""));
        Assert.Null(await new GitHubLauncherUpdateSource(client).GetLatestAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task FailedRequestIsNotReportedAsUpToDate(HttpStatusCode status)
    {
        using var client = new HttpClient(new ResponseHandler(status, ""));
        await Assert.ThrowsAsync<HttpRequestException>(() => new GitHubLauncherUpdateSource(client).GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RejectsInvalidVersion()
    {
        using var client = new HttpClient(new ResponseHandler(HttpStatusCode.OK,
            "{\"draft\":false,\"prerelease\":false,\"tag_name\":\"v0.2.0-beta\"}"));
        await Assert.ThrowsAsync<JsonException>(() => new GitHubLauncherUpdateSource(client).GetLatestAsync(CancellationToken.None));
    }

    private sealed class ResponseHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://api.github.com/repos/Derawaze/bqtj/releases/latest", request.RequestUri!.AbsoluteUri);
            Assert.NotEmpty(request.Headers.UserAgent);
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
