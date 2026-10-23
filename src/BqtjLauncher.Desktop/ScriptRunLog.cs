using System.Text.Encodings.Web;
using System.Text.Json;
using BqtjLauncher.Runtime.Flash;
using Serilog;

namespace BqtjLauncher.Desktop;

/// <summary>独立JSON行日志供本机排查脚本；按天及2MiB滚动，最多7份，不记录截图或账号凭据。</summary>
internal sealed class ScriptRunLog : IDisposable
{
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly Serilog.Core.Logger _logger;
    private readonly string _version;

    public ScriptRunLog(string directory, string version)
    {
        _version = version;
        _logger = new LoggerConfiguration().WriteTo.File(System.IO.Path.Combine(directory, "script-.jsonl"),
            rollingInterval: RollingInterval.Day, fileSizeLimitBytes: 2 * 1024 * 1024,
            retainedFileCountLimit: 7, rollOnFileSizeLimit: true, shared: true,
            outputTemplate: "{Message:lj}{NewLine}", formatProvider: System.Globalization.CultureInfo.InvariantCulture).CreateLogger();
    }

    public void Write(ScriptTraceEvent entry) => _logger.Information("{Trace}", JsonSerializer.Serialize(new { version = _version, trace = entry }, Options));
    public void Dispose() => _logger.Dispose();
}
