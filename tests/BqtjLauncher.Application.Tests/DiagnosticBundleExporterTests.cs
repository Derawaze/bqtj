using System.IO;
using System.IO.Compression;
using BqtjLauncher.Infrastructure;

namespace BqtjLauncher.Application.Tests;

/// <summary>只使用临时虚构日志，验证导出边界和隐私过滤，不接触用户数据。</summary>
public sealed class DiagnosticBundleExporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Bqtj-export-tests-" + Guid.NewGuid().ToString("N"));

    public DiagnosticBundleExporterTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ExportsSummariesWithoutRawSecretsOrDatabase()
    {
        var logs = Path.Combine(_root, "logs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "launcher-20260929.log"),
            "[2026-09-29 10:00:00.000 +08:00 INF] 原生宿主已启动 pid=123 fakePassword=SECRET\n" +
            "[2026-09-29 10:00:01.000 +08:00 ERR] 启动器操作失败 user=PRIVATE https://example.test/?token=SECRET\n" +
            "System.InvalidOperationException: SECRET Cookie=PRIVATE\n   at secret-path PRIVATE\n");
        File.WriteAllText(Path.Combine(logs, "launcher.db"), "PRIVATE");
        Directory.CreateDirectory(Path.Combine(logs, "nested"));
        File.WriteAllText(Path.Combine(logs, "nested", "launcher-secret.log"), "PRIVATE");
        var target = Path.Combine(_root, "diagnostics.zip");
        Assert.Equal(1, DiagnosticBundleExporter.Export(logs, target, "0.0.0-dev.20000101000000"));
        using var zip = ZipFile.OpenRead(target);
        Assert.Equal(3, zip.Entries.Count);
        using var reader = new StreamReader(zip.GetEntry("logs/summary-1.txt")!.Open());
        var text = reader.ReadToEnd();
        Assert.Contains("pid=123", text);
        Assert.Contains("System.InvalidOperationException", text);
        Assert.DoesNotContain("SECRET", text);
        Assert.DoesNotContain("PRIVATE", text);
        Assert.DoesNotContain("https://", text);
        Assert.DoesNotContain(zip.Entries, entry => entry.Name.EndsWith(".db", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingLogsStillExportsEnvironment()
    {
        var target = Path.Combine(_root, "empty.zip");
        Assert.Equal(0, DiagnosticBundleExporter.Export(Path.Combine(_root, "missing"), target, "test"));
        using var zip = ZipFile.OpenRead(target);
        Assert.Equal(2, zip.Entries.Count);
        Assert.NotNull(zip.GetEntry("environment.txt"));
    }

    [Fact]
    public void ReadsLogWhileWriterIsOpenAndLimitsFileCount()
    {
        for (var i = 0; i < 9; i++) File.WriteAllText(Path.Combine(_root, $"launcher-{i}.log"), "test");
        using var writer = new FileStream(Path.Combine(_root, "launcher-8.log"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        Assert.Equal(7, DiagnosticBundleExporter.Export(_root, Path.Combine(_root, "shared.zip"), "test"));
    }

    [Fact]
    public void RejectsNonZipWithoutOverwritingFile()
    {
        var target = Path.Combine(_root, "launcher.db");
        File.WriteAllText(target, "fictional database");
        Assert.Throws<ArgumentException>(() => DiagnosticBundleExporter.Export(_root, target, "test"));
        Assert.Equal("fictional database", File.ReadAllText(target));
    }

    [Fact]
    public void KeepsRecentTailOfOversizedLogAndReplacesChosenArchive()
    {
        var target = Path.Combine(_root, "tail.zip");
        File.WriteAllText(target, "old archive placeholder");
        File.WriteAllText(Path.Combine(_root, "launcher-large.log"),
            "[2026-09-29 09:00:00.000 +08:00 INF] 原生宿主已启动 pid=111\n" +
            new string('x', 3 * 1024 * 1024) +
            "\n[2026-09-29 10:00:00.000 +08:00 INF] 原生宿主已启动 pid=222\n");
        Assert.Equal(1, DiagnosticBundleExporter.Export(_root, target, "test"));
        using var zip = ZipFile.OpenRead(target);
        using var reader = new StreamReader(zip.GetEntry("logs/summary-1.txt")!.Open());
        var text = reader.ReadToEnd();
        Assert.Contains("pid=222", text);
        Assert.DoesNotContain("pid=111", text);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
