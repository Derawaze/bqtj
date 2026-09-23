using System.IO;
using Microsoft.Win32;

namespace BqtjLauncher.Runtime.Flash;

public sealed record FlashRuntimeInfo(bool IsAvailable, string? OcxPath, string Message);

public static class FlashRuntimeDetector
{
    public const string FlashClassId = "{D27CDB6E-AE6D-11CF-96B8-444553540000}";

    public static FlashRuntimeInfo Detect32Bit()
    {
        try
        {
            using var classesRoot = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry32);
            using var key = classesRoot.OpenSubKey($"CLSID\\{FlashClassId}\\InprocServer32");
            var path = key?.GetValue(null) as string;
            if (string.IsNullOrWhiteSpace(path))
            {
                return new FlashRuntimeInfo(false, null, "未注册 32 位 Flash ActiveX。");
            }

            path = Environment.ExpandEnvironmentVariables(path.Trim('"'));
            return File.Exists(path)
                ? new FlashRuntimeInfo(true, path, $"已找到本机 Flash：{Path.GetFileName(path)}")
                : new FlashRuntimeInfo(false, path, $"Flash 已注册，但 OCX 文件不存在：{path}");
        }
        catch (Exception exception)
        {
            return new FlashRuntimeInfo(false, null, $"读取 Flash 注册信息失败：{exception.Message}");
        }
    }
}
