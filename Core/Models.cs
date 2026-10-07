namespace AIUsage.Core;

/// <summary>One usage allowance with a reset window, e.g. "Claude weekly limit".</summary>
public sealed record UsageMeter
{
    public required string Label { get; init; }

    /// <summary>Fraction of the allowance consumed (1 = fully used). Null when unknown.</summary>
    public double? Used { get; init; }

    public DateTimeOffset? PeriodStart { get; init; }
    public DateTimeOffset? PeriodEnd { get; init; }

    /// <summary>Optional amount text such as "$3.20 of $10.00".</summary>
    public string? Detail { get; init; }

    /// <summary>Fraction of the period that has elapsed — where usage "should" be by now.</summary>
    public double? Expected(DateTimeOffset now)
    {
        if (PeriodStart is not { } start || PeriodEnd is not { } end)
            return null;
        var total = (end - start).TotalSeconds;
        return total <= 0 ? null : Math.Clamp((now - start).TotalSeconds / total, 0, 1);
    }

    /// <summary>
    /// Guard for the first moments of a period, where elapsed is ~0 and the ratio would blow up.
    /// Kept small so the ratio still matches the two percentages shown next to it.
    /// </summary>
    const double MinExpectedForPace = 0.01;

    /// <summary>
    /// Used divided by elapsed — how in sync usage is with the period: 1 is exactly on pace,
    /// 2 is twice as fast (slow down), 0.5 is half as fast (room to spare).
    /// </summary>
    public double? PaceRatio(DateTimeOffset now) =>
        Used is double used && Expected(now) is double expected ? used / Math.Max(expected, MinExpectedForPace) : null;

    /// <summary>The pace ratio on a 0..1 bar: 0.5 means on pace, 1 means twice the pace or more.</summary>
    public double? PaceFill(DateTimeOffset now) =>
        PaceRatio(now) is double ratio ? Math.Clamp(ratio * 0.5, 0, 1)
        : Used is double used ? Math.Clamp(used, 0, 1)
        : null;
}

public sealed record ProviderSnapshot(
    string ProviderId,
    string DisplayName,
    string? PlanText,
    IReadOnlyList<UsageMeter> Meters,
    string? Error,
    DateTimeOffset FetchedAt)
{
    public Color Accent { get; init; } = Color.Gray;

    public static ProviderSnapshot Loading(IUsageProvider provider) =>
        new(provider.Id, provider.DisplayName, null, [], null, default) { Accent = provider.Accent };

    public bool IsLoading => FetchedAt == default;

    /// <summary>The limit furthest ahead of its pace. It alone drives the tray bar.</summary>
    public UsageMeter? TrayMeter(DateTimeOffset now) =>
        Meters.Where(m => m.PaceRatio(now) is not null).MaxBy(m => m.PaceRatio(now))
        ?? Meters.Where(m => m.Used is not null).MaxBy(m => m.Used);

    public double? TrayFill(DateTimeOffset now) => TrayMeter(now)?.PaceFill(now);
}
