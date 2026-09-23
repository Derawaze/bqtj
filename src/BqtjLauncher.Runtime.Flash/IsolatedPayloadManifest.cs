using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>
/// 描述允许进入隔离 payload 的不可变文件集合，并生成内容寻址目录所需的摘要。
/// </summary>
internal sealed class IsolatedPayloadManifest
{
    internal const string EntryPointFileName = "BqtjLauncher.Desktop.exe";
    internal const string NativeHostFileName = "BqtjNativeFlashHost.exe";
    internal const string MarkerFileName = "payload-manifest.v1";

    private static readonly HashSet<string> AllowedExtensions = new(
        [".dll", ".exe", ".json", ".config"],
        StringComparer.OrdinalIgnoreCase);

    private IsolatedPayloadManifest(
        IReadOnlyList<IsolatedPayloadFile> files,
        string canonicalText,
        string manifestHash)
    {
        Files = files;
        CanonicalText = canonicalText;
        ManifestHash = manifestHash;
    }

    public IReadOnlyList<IsolatedPayloadFile> Files { get; }

    public string CanonicalText { get; }

    public string ManifestHash { get; }

    /// <summary>
    /// 仅接纳 self-contained 发布所需的可执行文件、程序集和运行时配置。
    /// 日志、数据库、PDB、压缩包及说明文件不会进入账号可执行环境。
    /// </summary>
    public static IsolatedPayloadManifest Create(string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        var sourceRoot = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException($"隔离 payload 源目录不存在：{sourceRoot}");
        }

        RejectReparsePoint(sourceRoot);
        var files = Directory
            .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .Select(path => CreateEntry(sourceRoot, path))
            .Where(entry => entry is not null)
            .Cast<IsolatedPayloadFile>()
            .OrderBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ToArray();

        EnsureRequiredFile(files, EntryPointFileName);
        EnsureRequiredFile(files, NativeHostFileName);

        var canonicalText = string.Concat(files.Select(entry =>
            $"{entry.Sha256} {entry.Length} {entry.RelativePath}\n"));
        var manifestHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonicalText)));
        return new IsolatedPayloadManifest(files, canonicalText, manifestHash);
    }

    private static IsolatedPayloadFile? CreateEntry(string sourceRoot, string path)
    {
        RejectReparsePoint(path);
        var relativePath = Path.GetRelativePath(sourceRoot, path)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (relativePath.StartsWith("../", StringComparison.Ordinal)
            || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("payload 文件逃逸出源目录。 ");
        }

        if (!AllowedExtensions.Contains(Path.GetExtension(relativePath)))
        {
            return null;
        }

        // 发布内容只允许根目录文件及语言资源目录，拒绝任意嵌套目录被意外带入。
        var segments = relativePath.Split('/');
        if (segments.Length > 2
            || (segments.Length == 2 && !IsLanguageResourceDirectory(segments[0])))
        {
            throw new InvalidDataException($"payload 中存在未列入白名单的目录：{relativePath}");
        }

        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        var hash = Convert.ToHexStringLower(SHA256.HashData(stream));
        return new IsolatedPayloadFile(relativePath, stream.Length, hash);
    }

    private static bool IsLanguageResourceDirectory(string name) =>
        name.Length is >= 2 and <= 16
        && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '-');

    private static void EnsureRequiredFile(
        IReadOnlyCollection<IsolatedPayloadFile> files,
        string fileName)
    {
        if (!files.Any(file => file.RelativePath.Equals(fileName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"payload 缺少必要文件：{fileName}");
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"payload 不允许符号链接或其他重解析点：{path}");
        }
    }
}

internal sealed record IsolatedPayloadFile(string RelativePath, long Length, string Sha256);
