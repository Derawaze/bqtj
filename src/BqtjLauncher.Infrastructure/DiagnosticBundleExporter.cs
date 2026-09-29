using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace BqtjLauncher.Infrastructure;

/// <summary>按白名单导出本地诊断摘要；不递归读取数据目录，不复制原始日志、凭据或请求内容。</summary>
public static partial class DiagnosticBundleExporter
{
    private const int MaximumLogBytes = 2 * 1024 * 1024;

    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BqtjLauncher", "logs");

    /// <summary>最多处理最近7个日志的末尾2MiB。先生成临时ZIP，成功后替换用户选定的目标。</summary>
    public static int Export(string logDirectory, string destination, string version)
    {
        if (!string.Equals(Path.GetExtension(destination), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("诊断包必须保存为ZIP文件。", nameof(destination));
        RejectLinkedPath(logDirectory);
        RejectLinkedPath(Path.GetFullPath(destination));
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var exported = 0;
        var skipped = 0;
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                var logs = Directory.Exists(logDirectory)
                    ? new DirectoryInfo(logDirectory).EnumerateFiles("launcher-*.log", SearchOption.TopDirectoryOnly)
                        .OrderByDescending(file => file.LastWriteTimeUtc).Take(7).ToArray()
                    : [];
                foreach (var file in logs)
                {
                    if ((file.Attributes & FileAttributes.ReparsePoint) != 0) { skipped++; continue; }
                    try
                    {
                        using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        var length = (int)Math.Min(input.Length, MaximumLogBytes);
                        var clipped = input.Length > length;
                        input.Seek(-length, SeekOrigin.End);
                        var bytes = new byte[length];
                        var read = 0;
                        while (read < length)
                        {
                            var count = input.Read(bytes, read, length - read);
                            if (count == 0) break;
                            read += count;
                        }
                        var text = Encoding.UTF8.GetString(bytes, 0, read);
                        // 从字节尾部截取时舍弃第一条不完整记录，避免截断UTF-8或日志字段。
                        if (clipped) text = text.IndexOf('\n') is var boundary && boundary >= 0 ? text[(boundary + 1)..] : string.Empty;
                        WriteEntry(archive, $"logs/summary-{exported + 1}.txt", Summarize(text));
                        exported++;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        skipped++;
                    }
                }
                WriteEntry(archive, "environment.txt",
                    $"Launcher={version}\nOS={Environment.OSVersion.VersionString}\nRuntime={RuntimeInformation.FrameworkDescription}\nArchitecture={RuntimeInformation.ProcessArchitecture}\nUTC={DateTimeOffset.UtcNow:O}\nLogFiles={exported}\nSkippedFiles={skipped}\n");
                WriteEntry(archive, "README.txt",
                    "请补充：问题发生时间、操作步骤、预期与实际结果、截图。\n本包仅含环境信息及白名单日志摘要，不含原始日志、账号数据库、Cookie、机器名或用户目录。\n日志摘要保留时间、级别、已知事件、错误类型及有限数值；其他文本省略。最多7个日志，每个取末尾2MiB，不保证包含全部现场。\n没有日志或有文件不可读时仍会生成环境信息。请检查后自行提交，本工具不会自动上传。\n");
            }
            File.Move(temporary, destination, overwrite: true);
            return exported;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>仅提取固定事件及数值，任意异常消息、URL和账号文本都不原样进入诊断包。</summary>
    internal static string Summarize(string text)
    {
        var result = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            var header = Header().Match(line);
            var exception = ExceptionType().Match(line);
            if (!header.Success && !exception.Success) continue;
            if (exception.Success) { result.AppendLine(exception.Value); continue; }
            result.Append(header.Value);
            var labels = new[] { "启动器初始化失败", "启动器操作失败", "未能读取本地账号信息", "原生宿主已启动", "原生宿主就绪", "初始化超时", "容器就绪", "F3 热键", "收到 F3", "F3 互换", "容器窗口收到 F3" };
            var label = labels.FirstOrDefault(value => line.Contains(value, StringComparison.Ordinal));
            result.Append(label ?? "事件正文已省略");
            if (label is not null)
            {
                foreach (Match value in SafeValue().Matches(line)) result.Append(' ').Append(value.Value);
            }
            result.AppendLine();
        }
        return result.Length == 0 ? "没有可导出的白名单日志记录。\n" : result.ToString();
    }

    private static void WriteEntry(ZipArchive archive, string name, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static void RejectLinkedPath(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("诊断路径不能包含目录链接。");
        }
    }

    [GeneratedRegex(@"^\[?\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?: [+-]\d{2}:\d{2})? \[?(?:INF|WRN|ERR|FTL|DBG)\]?\s*")]
    private static partial Regex Header();
    [GeneratedRegex(@"^(?:System|Microsoft|BqtjLauncher)(?:\.[A-Za-z][A-Za-z0-9]*)*Exception\b")]
    private static partial Regex ExceptionType();
    [GeneratedRegex(@"(?:pid=\d{1,10}\b|退出码=-?\d{1,10}\b|用时 \d{1,12}ms\b|阶段=(?:start|create-browser|ready)(?:>(?:start|create-browser|ready))*\b|HRESULT=0x[0-9a-fA-F]{8}\b)")]
    private static partial Regex SafeValue();
}
