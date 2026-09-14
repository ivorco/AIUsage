namespace AIUsage.Core;

/// <summary>
/// A usage source shown as one tray bar. To add an AI service, implement this and
/// register it in <see cref="ProviderRegistry"/>; its settings appear in the settings dialog automatically.
/// </summary>
public interface IUsageProvider
{
    string Id { get; }
    string DisplayName { get; }
    IReadOnlyList<SettingField> Settings { get; }
    Task<ProviderSnapshot> FetchAsync(CancellationToken ct);
}

/// <param name="IsSecret">Secret values are write-only in the UI.</param>
/// <param name="Validate">Returns an error message for invalid input, or null.</param>
public sealed record SettingField(string Key, string Label, string Help, bool IsSecret, Func<string, string?>? Validate = null);

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
