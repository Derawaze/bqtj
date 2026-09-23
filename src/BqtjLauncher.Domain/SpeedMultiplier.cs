using System.Globalization;

namespace BqtjLauncher.Domain;

public readonly record struct SpeedMultiplier
{
    public const decimal Minimum = 0.01m;
    public const decimal Maximum = 100m;

    private SpeedMultiplier(decimal value)
    {
        Value = value;
    }

    public decimal Value { get; }

    public bool IsOriginal => Value == 1m;

    public string DisplayText => IsOriginal
        ? "原速"
        : $"{Value.ToString("0.##", CultureInfo.InvariantCulture)}倍";

    public static SpeedMultiplier Original { get; } = new(1m);

    public static SpeedMultiplier Create(decimal value)
    {
        if (value is < Minimum or > Maximum)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"速度倍率必须在 {Minimum} 到 {Maximum} 之间。");
        }

        return new SpeedMultiplier(value);
    }

    public static bool TryParse(string? text, out SpeedMultiplier multiplier)
    {
        multiplier = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim()
            .Replace("倍", string.Empty, StringComparison.Ordinal)
            .Replace("x", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
        if (!decimal.TryParse(
                normalized,
                NumberStyles.Number,
                CultureInfo.CurrentCulture,
                out var value)
            && !decimal.TryParse(
                normalized,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out value))
        {
            return false;
        }

        if (value is < Minimum or > Maximum)
        {
            return false;
        }

        multiplier = new SpeedMultiplier(value);
        return true;
    }
}
