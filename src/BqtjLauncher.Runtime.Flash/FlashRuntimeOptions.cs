namespace BqtjLauncher.Runtime.Flash;

public sealed record FlashRuntimeOptions
{
    public required Uri GamePageUri { get; init; }

    public int WindowWidth { get; init; } = 1000;

    public int WindowHeight { get; init; } = 690;

    public void Validate()
    {
        if (!GamePageUri.IsAbsoluteUri || GamePageUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Flash 游戏页地址必须是 HTTP(S) 绝对地址。", nameof(GamePageUri));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(WindowWidth, 800);
        ArgumentOutOfRangeException.ThrowIfLessThan(WindowHeight, 600);

    }
}
