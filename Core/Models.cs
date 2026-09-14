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

    /// <summary>Whether this meter drives the provider's tray bar. Short windows (5-hour) don't.</summary>
    public bool AffectsTray { get; init; } = true;

    /// <summary>Fraction of the period that has elapsed — where usage "should" be by now.</summary>
    public double? Expected(DateTimeOffset now)
    {
        if (PeriodStart is not { } start || PeriodEnd is not { } end)
            return null;
        var total = (end - start).TotalSeconds;
        return total <= 0 ? null : Math.Clamp((now - start).TotalSeconds / total, 0, 1);
    }

    /// <summary>
    /// Pace is measured against at least this share of the period, so a little usage right after a
    /// reset (1% used, 0.5% elapsed) doesn't read as double pace. Heavy early usage still alerts.
    /// </summary>
    const double MinExpectedForPace = 0.1;

    /// <summary>Used divided by expected: 1 is exactly on pace, 2 is twice as fast.</summary>
    public double? PaceRatio(DateTimeOffset now) =>
        Used is double used && Expected(now) is double expected ? used / Math.Max(expected, MinExpectedForPace) : null;

    /// <summary>Tray bar fill: 0.5 means on pace, 1 means twice the pace or more.</summary>
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
    public static ProviderSnapshot Loading(IUsageProvider provider) =>
        new(provider.Id, provider.DisplayName, null, [], null, default);

    public bool IsLoading => FetchedAt == default;

    /// <summary>The most alarming pace among the meters that drive the tray.</summary>
    public double? TrayFill(DateTimeOffset now) => Max(m => m.PaceFill(now));

    public double? PaceRatio(DateTimeOffset now) => Max(m => m.PaceRatio(now));

    double? Max(Func<UsageMeter, double?> selector)
    {
        double? max = null;
        foreach (var meter in Meters)
        {
            if (meter.AffectsTray && selector(meter) is double value)
                max = max is null ? value : Math.Max(max.Value, value);
        }
        return max;
    }
}
