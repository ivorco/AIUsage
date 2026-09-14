using System.Net;
using System.Text.Json.Nodes;
using AIUsage.Core;

namespace AIUsage.Providers;

/// <summary>
/// Claude Pro/Max subscription limits, from the same endpoint Claude Code's /usage uses.
/// Auth comes from Claude Code's login (~/.claude/.credentials.json) unless a token is set in settings.
/// </summary>
public sealed class ClaudeProvider : IUsageProvider
{
    const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    const string RenewLoginMessage = "Claude Code login needs renewing: run `claude` in a terminal and sign in with /login.";
    static readonly string[] TokenEndpoints = ["https://console.anthropic.com/v1/oauth/token", "https://platform.claude.com/v1/oauth/token"];
    static readonly SemaphoreSlim FileGate = new(1, 1);

    // Anthropic answers stale refresh tokens and over-eager polling with 429, and repeated retries make it
    // worse, so back off instead of retrying on every refresh cycle.
    static readonly TimeSpan RefreshCooldown = TimeSpan.FromMinutes(30);
    static readonly TimeSpan UsageCooldown = TimeSpan.FromMinutes(10);
    static (string RefreshToken, DateTimeOffset Until)? refreshBlocked;
    static DateTimeOffset usageBlockedUntil;

    public string Id => "claude";
    public string DisplayName => "Claude";

    public IReadOnlyList<SettingField> Settings { get; } =
    [
        new("accessToken", "OAuth access token", "Optional. Overrides the Claude Code login read from ~/.claude/.credentials.json.", IsSecret: true),
    ];

    static string CredentialsPath => Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        ".credentials.json");

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow < usageBlockedUntil)
            throw new ProviderException($"Claude usage is rate limited; retrying after {usageBlockedUntil.ToLocalTime():HH:mm}.");

        var manual = CredentialStore.Read(Id, "accessToken")?.Trim();
        string token;
        string? plan = null;
        if (!string.IsNullOrEmpty(manual))
            token = manual;
        else
            (token, plan) = await GetLocalTokenAsync(rejectedToken: null, ct);

        var result = await GetUsageAsync(token, ct);
        if (result.Status == HttpStatusCode.Unauthorized)
        {
            if (!string.IsNullOrEmpty(manual))
                throw new ProviderException("The Claude token saved in Settings was rejected.");
            (token, plan) = await GetLocalTokenAsync(rejectedToken: token, ct);
            result = await GetUsageAsync(token, ct);
        }
        if (result.Status == HttpStatusCode.TooManyRequests)
        {
            usageBlockedUntil = DateTimeOffset.UtcNow + UsageCooldown;
            throw new ProviderException($"Claude usage is rate limited; retrying after {usageBlockedUntil.ToLocalTime():HH:mm}.");
        }
        result.EnsureSuccess("Claude usage");

        return new ProviderSnapshot(Id, DisplayName, plan is null ? null : Format.TitleCase(plan), ParseMeters(result.Json!), null, DateTimeOffset.Now);
    }

    static Task<HttpResult> GetUsageAsync(string token, CancellationToken ct) =>
        Http.SendAsync(HttpMethod.Get, UsageUrl, [("Authorization", "Bearer " + token), ("anthropic-beta", "oauth-2025-04-20")], null, ct);

    static List<UsageMeter> ParseMeters(JsonNode json)
    {
        var meters = new List<(int Order, UsageMeter Meter)>();
        if (json is not JsonObject obj)
            return [];

        foreach (var (name, node) in obj)
        {
            if (node is not JsonObject window || !window.ContainsKey("utilization"))
                continue;
            TimeSpan? length = name == "five_hour" ? TimeSpan.FromHours(5)
                : name.StartsWith("seven_day", StringComparison.Ordinal) ? TimeSpan.FromDays(7)
                : null;
            if (length is null)
                continue;

            var resetsAt = window.At("resets_at").Date();
            var meter = new UsageMeter
            {
                Label = WindowLabel(name),
                Used = (window.At("utilization").Num() ?? 0) / 100,
                PeriodStart = resetsAt - length,
                PeriodEnd = resetsAt,
                AffectsTray = name != "five_hour",
            };
            meters.Add((name == "seven_day" ? 0 : name == "five_hour" ? 2 : 1, meter));
        }

        if (json.At("extra_usage") is JsonObject extra && extra.At("is_enabled").Bool() == true
            && extra.At("monthly_limit").Num() is double limit && limit > 0)
        {
            var used = extra.At("used_credits").Num() ?? 0;
            var monthStart = new DateTimeOffset(DateTimeOffset.Now.Year, DateTimeOffset.Now.Month, 1, 0, 0, 0, DateTimeOffset.Now.Offset);
            meters.Add((3, new UsageMeter
            {
                Label = "Extra usage",
                Used = used / limit,
                Detail = $"{Format.Money(used / 100)} of {Format.Money(limit / 100)}",
                PeriodStart = monthStart,
                PeriodEnd = monthStart.AddMonths(1),
            }));
        }

        return meters.OrderBy(m => m.Order).Select(m => m.Meter).ToList();
    }

    static string WindowLabel(string name) => name switch
    {
        "seven_day" => "Weekly",
        "five_hour" => "5-hour session",
        _ => "Weekly · " + Format.TitleCase(name["seven_day_".Length..]),
    };

    /// <summary>
    /// Returns Claude Code's access token, refreshing it when expired (or rejected) and writing the rotated
    /// tokens back to Claude Code's credentials file so Claude Code stays signed in.
    /// </summary>
    static async Task<(string Token, string? Plan)> GetLocalTokenAsync(string? rejectedToken, CancellationToken ct)
    {
        await FileGate.WaitAsync(ct);
        try
        {
            var path = CredentialsPath;
            if (!File.Exists(path))
                throw new ProviderException("No Claude Code login found. Sign in with `claude`, or set a token in Settings.");

            var root = JsonFiles.ReadObject(path);
            var oauth = root.At("claudeAiOauth") as JsonObject
                ?? throw new ProviderException("Claude Code is not signed in with a Claude subscription.");
            var access = oauth.At("accessToken").Str();
            var refresh = oauth.At("refreshToken").Str();
            var plan = oauth.At("subscriptionType").Str();
            var expiresAt = oauth.At("expiresAt").Date();

            if (access is not null && access != rejectedToken && (expiresAt is null || expiresAt > DateTimeOffset.UtcNow.AddMinutes(1)))
                return (access, plan);
            if (refresh is null)
                throw new ProviderException(RenewLoginMessage);
            // A new login replaces the refresh token, which lifts the block immediately.
            if (refreshBlocked is { } blocked && blocked.RefreshToken == refresh && DateTimeOffset.UtcNow < blocked.Until)
                throw new ProviderException(RenewLoginMessage);

            var tokens = await RefreshAsync(refresh, ct);
            var newAccess = tokens.At("access_token").Str() ?? throw new ProviderException("Claude token refresh returned no access token.");
            oauth["accessToken"] = newAccess;
            if (tokens.At("refresh_token").Str() is { } newRefresh)
                oauth["refreshToken"] = newRefresh;
            if (tokens.At("expires_in").Num() is double seconds)
                oauth["expiresAt"] = DateTimeOffset.UtcNow.AddSeconds(seconds).ToUnixTimeMilliseconds();
            JsonFiles.WriteAtomic(path, root);
            refreshBlocked = null;
            return (newAccess, plan);
        }
        finally
        {
            FileGate.Release();
        }
    }

    static async Task<JsonNode> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var body = new JsonObject { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken, ["client_id"] = ClientId };
        HttpResult? last = null;
        foreach (var endpoint in TokenEndpoints)
        {
            last = await Http.SendAsync(HttpMethod.Post, endpoint, null, body, ct);
            if (last.Ok && last.Json is not null)
                return last.Json;
            if (last.Status == HttpStatusCode.TooManyRequests || last.Body.Contains("invalid_grant", StringComparison.Ordinal))
            {
                refreshBlocked = (refreshToken, DateTimeOffset.UtcNow + RefreshCooldown);
                throw new ProviderException(RenewLoginMessage);
            }
        }
        throw new ProviderException("Could not refresh the Claude Code login: " + last!.Describe());
    }
}
