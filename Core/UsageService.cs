namespace AIUsage.Core;

/// <summary>Fetches every provider in parallel and keeps the latest snapshots.</summary>
public sealed class UsageService
{
    public UsageService(IReadOnlyList<IUsageProvider> providers)
    {
        Providers = providers;
        Latest = providers.Select(ProviderSnapshot.Loading).ToList();
    }

    public IReadOnlyList<IUsageProvider> Providers { get; }
    public IReadOnlyList<ProviderSnapshot> Latest { get; private set; }
    public DateTimeOffset? LastUpdated { get; private set; }

    public async Task RefreshAsync(CancellationToken ct)
    {
        var previous = Latest;
        var fresh = await Task.WhenAll(Providers.Select(p => Task.Run(() => FetchSafeAsync(p, ct), ct)));

        // On a transient failure keep showing the last good numbers, flagged with the error.
        for (int i = 0; i < fresh.Length; i++)
        {
            var old = previous[i];
            if (fresh[i].Error is { } error && fresh[i].Meters.Count == 0 && old.Meters.Count > 0)
                fresh[i] = old with { Error = $"{error} (showing data from {old.FetchedAt.ToLocalTime():HH:mm})" };
        }

        Latest = fresh;
        LastUpdated = DateTimeOffset.Now;
    }

    static async Task<ProviderSnapshot> FetchSafeAsync(IUsageProvider provider, CancellationToken ct)
    {
        try
        {
            return await provider.FetchAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            var message = e switch
            {
                ProviderException => e.Message,
                HttpRequestException or TaskCanceledException => "Network error: " + e.Message,
                _ => $"{e.GetType().Name}: {e.Message}",
            };
            return new ProviderSnapshot(provider.Id, provider.DisplayName, null, [], message, DateTimeOffset.Now);
        }
    }
}
