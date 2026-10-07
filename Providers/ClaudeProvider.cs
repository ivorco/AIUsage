using System.Net;
using System.Text.Json.Nodes;
using AIUsage.Core;
using AIUsage.UI;

namespace AIUsage.Providers;

/// <summary>
/// Claude Pro/Max plan limits (5-hour, weekly, usage credits).
/// Primary source: claude.ai's usage API with a session from "Sign in…" in settings (see <see cref="ClaudeWeb"/>).
/// Fallback: the endpoint Claude Code's /usage uses, with Claude Code's login from ~/.claude/.credentials.json.
/// </summary>
public sealed class ClaudeProvider : IUsageProvider
{
    const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    const string SignInMessage = "Sign in to claude.ai from Settings (gear icon) to show Claude usage.";
    static readonly string[] TokenEndpoints = ["https://console.anthropic.com/v1/oauth/token", "https://platform.claude.com/v1/oauth/token"];
    static readonly SemaphoreSlim FileGate = new(1, 1);

    // Anthropic answers refreshes from anything but Claude Code, and over-eager polling, with 429; repeated
    // retries make it worse, so back off instead of retrying on every refresh cycle.
    static readonly TimeSpan RefreshCooldown = TimeSpan.FromMinutes(30);
    static readonly TimeSpan UsageCooldown = TimeSpan.FromMinutes(10);
    static (string RefreshToken, DateTimeOffset Until)? refreshBlocked;
    static DateTimeOffset usageBlockedUntil;

    public string Id => "claude";
    public string DisplayName => "Claude";
    public Color Accent => Color.FromArgb(0xD9, 0x77, 0x57);

    public IReadOnlyList<SettingField> Settings { get; } =
    [
        new("sessionKey", "claude.ai session",
            "Sign in to claude.ai (or paste its sessionKey cookie). Lasts about 30 days. Without it, the Claude Code login is tried.",
            IsSecret: true, Acquire: ClaudeLoginForm.Acquire),
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

        var sessionKey = CredentialStore.Read(Id, "sessionKey")?.Trim();
        return string.IsNullOrEmpty(sessionKey)
            ? await FetchWithClaudeCodeLoginAsync(ct)
            : await FetchWithWebSessionAsync(sessionKey, ct);
    }

    async Task<ProviderSnapshot> FetchWithWebSessionAsync(string sessionKey, CancellationToken ct)
    {
        var (plan, usage) = await ClaudeWeb.RunAsync(sessionKey, async get =>
        {
            var organizations = await get(ClaudeWeb.Origin + "/api/organizations");
            EnsureWebSuccess(organizations);
            var organization = (organizations.Json as JsonArray)?.OfType<JsonObject>()
                .OrderByDescending(o => Capabilities(o).Any(c => c.StartsWith("claude_", StringComparison.Ordinal)))
                .FirstOrDefault()
                ?? throw new ProviderException("claude.ai returned no organizations.");
            var uuid = organization.At("uuid").Str() ?? throw new ProviderException("claude.ai organization has no id.");

            var result = await get($"{ClaudeWeb.Origin}/api/organizations/{uuid}/usage");
            EnsureWebSuccess(result);
            return (PlanName(organization), result.Json!);
        }, ct);

        return new ProviderSnapshot(Id, DisplayName, plan, ParseMeters(usage), null, DateTimeOffset.Now);
    }

    static void EnsureWebSuccess(ClaudeWeb.Response response)
    {
        switch (response.Status)
        {
            case >= 200 and < 300 when response.Json is not null:
                return;
            case 401 or 403 when response.Json is not null:
                throw new ProviderException("claude.ai session expired. Sign in again from Settings (gear icon).");
            case 403:
                throw new ProviderException("claude.ai's browser check blocked the request. Retrying later.");
            case 429:
                usageBlockedUntil = DateTimeOffset.UtcNow + UsageCooldown;
                throw new ProviderException($"Claude usage is rate limited; retrying after {usageBlockedUntil.ToLocalTime():HH:mm}.");
            default:
                throw new ProviderException($"claude.ai usage failed: HTTP {response.Status}");
        }
    }

    static IEnumerable<string> Capabilities(JsonNode organization) =>
        (organization.At("capabilities") as JsonArray)?.Select(c => c.Str()).OfType<string>() ?? [];

    static string? PlanName(JsonNode organization)
    {
        var capabilities = Capabilities(organization).ToHashSet();
        return capabilities.Contains("claude_max") ? "Max"
            : capabilities.Contains("claude_pro") ? "Pro"
            : null;
    }

    async Task<ProviderSnapshot> FetchWithClaudeCodeLoginAsync(CancellationToken ct)
    {
        var (token, plan) = await GetLocalTokenAsync(rejectedToken: null, ct);
        var result = await GetUsageAsync(token, ct);
        if (result.Status == HttpStatusCode.Unauthorized)
        {
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

    /// <summary>Parses the usage JSON, which has the same shape on claude.ai and the Claude Code endpoint.</summary>
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
            bool isMain = name is "five_hour" or "seven_day";
            // Model- or feature-specific windows that haven't started are just noise.
            if (!isMain && resetsAt is null)
                continue;

            var meter = new UsageMeter
            {
                Label = WindowLabel(name),
                Used = (window.At("utilization").Num() ?? 0) / 100,
                PeriodStart = resetsAt - length,
                PeriodEnd = resetsAt,
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
                Label = "Usage credits",
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
        "five_hour" => "5-hour limit",
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
                throw new ProviderException(SignInMessage);

            var root = JsonFiles.ReadObject(path);
            var oauth = root.At("claudeAiOauth") as JsonObject ?? throw new ProviderException(SignInMessage);
            var access = oauth.At("accessToken").Str();
            var refresh = oauth.At("refreshToken").Str();
            var plan = oauth.At("subscriptionType").Str();
            var expiresAt = oauth.At("expiresAt").Date();

            if (access is not null && access != rejectedToken && (expiresAt is null || expiresAt > DateTimeOffset.UtcNow.AddMinutes(1)))
                return (access, plan);
            if (refresh is null)
                throw new ProviderException(SignInMessage);
            // A new Claude Code login replaces the refresh token, which lifts the block immediately.
            if (refreshBlocked is { } blocked && blocked.RefreshToken == refresh && DateTimeOffset.UtcNow < blocked.Until)
                throw new ProviderException(SignInMessage);

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
                throw new ProviderException(SignInMessage);
            }
        }
        throw new ProviderException("Could not refresh the Claude Code login: " + last!.Describe());
    }
}
