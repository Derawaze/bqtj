using System.IO;

namespace BqtjLauncher.Runtime.Flash;

/// <summary>950×600游戏画面的固定难度按钮布局；点击本体中央，避开右上角“扫荡”和悬停文字。</summary>
internal static class SkyTopMenuLayout
{
    /// <summary>正常模式的两列五行按钮；名称保留游戏原文，未登记的难度不猜位置。</summary>
    internal static (int X, int Y) Difficulty(string name) => name switch
    {
        "简单" => (370, 182),
        "普通" => (584, 182),
        "困难" => (370, 237),
        "超难" => (584, 237),
        "冲刺" => (370, 292),
        "炼狱" => (584, 292),
        "地狱" => (370, 347),
        "地狱二" => (584, 347),
        "地狱三" => (370, 402),
        "地狱四" => (584, 402),
        _ => throw new InvalidDataException("尚未登记该难度的固定按钮位置：" + name)
    };
}
