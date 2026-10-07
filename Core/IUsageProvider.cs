namespace AIUsage.Core;

/// <summary>
/// A usage source shown as one tray bar. To add an AI service, implement this and
/// register it in <see cref="ProviderRegistry"/>; its settings appear in the settings dialog automatically.
/// </summary>
public interface IUsageProvider
{
    string Id { get; }
    string DisplayName { get; }

    /// <summary>Color of this provider's tray bar and popup bars.</summary>
    Color Accent { get; }
    IReadOnlyList<SettingField> Settings { get; }
    Task<ProviderSnapshot> FetchAsync(CancellationToken ct);
}

/// <param name="IsSecret">Secret values are write-only in the UI.</param>
/// <param name="Validate">Returns an error message for invalid input, or null.</param>
/// <param name="Acquire">Optional interactive way to obtain the value (e.g. a sign-in window); returns null if cancelled.</param>
public sealed record SettingField(
    string Key,
    string Label,
    string Help,
    bool IsSecret,
    Func<string, string?>? Validate = null,
    Func<IWin32Window, string?>? Acquire = null,
    string AcquireLabel = "Sign in…");

public static class ProviderRegistry
{
    public static IReadOnlyList<IUsageProvider> Create() =>
    [
        new Providers.ClaudeProvider(),
        new Providers.CursorProvider(),
        new Providers.ChatGptProvider(),
    ];
}

public sealed class ProviderException(string message) : Exception(message);
