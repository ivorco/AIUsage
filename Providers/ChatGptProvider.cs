using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using AIUsage.Core;

namespace AIUsage.Providers;

/// <summary>
/// ChatGPT subscription (Codex rate-limit windows via the Codex login in ~/.codex/auth.json) plus,
/// optionally, OpenAI API spend from the organization Costs API paced against a monthly budget.
/// </summary>
public sealed class ChatGptProvider : IUsageProvider
{
    const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    const string AuthClaim = "https://api.openai.com/auth";
    static readonly SemaphoreSlim FileGate = new(1, 1);

    public string Id => "chatgpt";
    public string DisplayName => "ChatGPT";

    public IReadOnlyList<SettingField> Settings { get; } =
    [
        new("accessToken", "ChatGPT access token", "Optional. Overrides the Codex login read from ~/.codex/auth.json.", IsSecret: true),
        new("adminKey", "OpenAI Admin API key", "Optional. An sk-admin-… key; adds API spend from the organization Costs API.", IsSecret: true),
        new("monthlyBudget", "API monthly budget (USD)", "Planned API spend per calendar month (UTC), used to pace API spend.", IsSecret: false,
            Validate: v => Format.TryParseMoney(v, out _) ? null : "Enter a positive amount, e.g. 50"),
    ];

    static string AuthPath => Path.Combine(
        Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"),
        "auth.json");

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        var meters = new List<UsageMeter>();
        var notes = new List<string>();
        var adminKey = CredentialStore.Read(Id, "adminKey")?.Trim();
        var apiSpendTask = string.IsNullOrEmpty(adminKey) ? null : FetchApiSpendAsync(adminKey, ct);

        string? plan = null;
        try
        {
            plan = await FetchCodexAsync(meters, ct);
        }
        catch (Exception e) when (apiSpendTask is not null && e is ProviderException or HttpRequestException)
        {
            notes.Add(e.Message);
        }

        if (apiSpendTask is not null)
        {
            try
            {
                meters.Add(await apiSpendTask);
            }
            catch (Exception e) when (e is ProviderException or HttpRequestException)
            {
                notes.Add(e.Message);
            }
        }

        return new ProviderSnapshot(Id, DisplayName, plan, meters, notes.Count > 0 ? string.Join(" · ", notes) : null, DateTimeOffset.Now);
    }

    sealed record CodexAuth(string AccessToken, string? AccountId, string? PlanType, DateTimeOffset? ActiveUntil)
    {
        public static CodexAuth From(string accessToken, string? idToken = null, string? accountId = null)
        {
            var accessClaims = Jwt.Payload(accessToken).At(AuthClaim);
            var idClaims = Jwt.Payload(idToken).At(AuthClaim);
            return new CodexAuth(
                accessToken,
                accountId ?? accessClaims.At("chatgpt_account_id").Str() ?? idClaims.At("chatgpt_account_id").Str(),
                idClaims.At("chatgpt_plan_type").Str() ?? accessClaims.At("chatgpt_plan_type").Str(),
                idClaims.At("chatgpt_subscription_active_until").Date() ?? accessClaims.At("chatgpt_subscription_active_until").Date());
        }
    }

    async Task<string?> FetchCodexAsync(List<UsageMeter> meters, CancellationToken ct)
    {
        var manual = CredentialStore.Read(Id, "accessToken")?.Trim();
        var auth = string.IsNullOrEmpty(manual) ? await GetLocalAuthAsync(rejectedToken: null, ct) : CodexAuth.From(manual);

        var result = await GetUsageAsync(auth, ct);
        if (result.Status == HttpStatusCode.Unauthorized)
        {
            if (!string.IsNullOrEmpty(manual))
                throw new ProviderException("The ChatGPT token saved in Settings was rejected.");
            auth = await GetLocalAuthAsync(rejectedToken: auth.AccessToken, ct);
            result = await GetUsageAsync(auth, ct);
        }
        result.EnsureSuccess("Codex usage");

        var json = result.Json!;
        AddWindows(meters, json.At("rate_limit"), "Codex", affectsTray: true);
        if (json.At("additional_rate_limits") is JsonArray additional)
        {
            foreach (var limit in additional)
                AddWindows(meters, limit.At("rate_limit"), limit.At("limit_name").Str() ?? "Other", affectsTray: false);
        }

        var parts = new List<string>();
        if ((json.At("plan_type").Str() ?? auth.PlanType) is { } planType)
            parts.Add(Format.TitleCase(planType));
        if (auth.ActiveUntil is { } until)
            parts.Add("renews " + until.ToLocalTime().ToString("MMM d", CultureInfo.InvariantCulture));
        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    static Task<HttpResult> GetUsageAsync(CodexAuth auth, CancellationToken ct)
    {
        var headers = new List<(string, string)> { ("Authorization", "Bearer " + auth.AccessToken) };
        if (auth.AccountId is not null)
            headers.Add(("ChatGPT-Account-Id", auth.AccountId));
        return Http.SendAsync(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage", headers, null, ct);
    }

    static void AddWindows(List<UsageMeter> meters, JsonNode? rateLimit, string prefix, bool affectsTray)
    {
        foreach (var key in new[] { "secondary_window", "primary_window" })
        {
            if (rateLimit.At(key) is not JsonObject window)
                continue;
            var length = window.At("limit_window_seconds").Num() is double seconds ? TimeSpan.FromSeconds(seconds) : (TimeSpan?)null;
            var resetAt = window.At("reset_at").Date()
                ?? (window.At("reset_after_seconds").Num() is double after ? DateTimeOffset.UtcNow.AddSeconds(after) : (DateTimeOffset?)null);
            meters.Add(new UsageMeter
            {
                Label = $"{prefix} · {Format.WindowName(length)}",
                Used = (window.At("used_percent").Num() ?? 0) / 100,
                PeriodStart = resetAt - length,
                PeriodEnd = resetAt,
                AffectsTray = affectsTray && length >= TimeSpan.FromDays(1),
            });
        }
    }

    /// <summary>
    /// Returns the Codex access token, refreshing it when expired (or rejected) and writing the rotated
    /// tokens back to Codex's auth.json so Codex stays signed in.
    /// </summary>
    static async Task<CodexAuth> GetLocalAuthAsync(string? rejectedToken, CancellationToken ct)
    {
        await FileGate.WaitAsync(ct);
        try
        {
            var path = AuthPath;
            if (!File.Exists(path))
                throw new ProviderException("No Codex login found. Sign in with `codex login`, or set a token in Settings.");

            var root = JsonFiles.ReadObject(path);
            var tokens = root.At("tokens") as JsonObject
                ?? throw new ProviderException("Codex is not signed in with a ChatGPT account.");
            var access = tokens.At("access_token").Str();
            var refresh = tokens.At("refresh_token").Str();
            var idToken = tokens.At("id_token").Str();
            var accountId = tokens.At("account_id").Str();

            if (access is not null && access != rejectedToken && (Jwt.Expiry(access) is not { } expiry || expiry > DateTimeOffset.UtcNow.AddMinutes(1)))
                return CodexAuth.From(access, idToken, accountId);
            if (refresh is null)
                throw new ProviderException("Codex login expired. Run `codex login` to sign in again.");

            var body = new JsonObject
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refresh,
                ["scope"] = "openid profile email",
            };
            var result = await Http.SendAsync(HttpMethod.Post, "https://auth.openai.com/oauth/token", null, body, ct);
            if (!result.Ok || result.Json is null)
            {
                throw new ProviderException(result.Body.Contains("invalid_grant") || result.Body.Contains("refresh_token_")
                    ? "Codex login expired. Run `codex login` to sign in again."
                    : "Could not refresh the Codex login: " + result.Describe());
            }

            access = result.Json.At("access_token").Str() ?? throw new ProviderException("Codex token refresh returned no access token.");
            tokens["access_token"] = access;
            if (result.Json.At("refresh_token").Str() is { } newRefresh)
                tokens["refresh_token"] = newRefresh;
            if (result.Json.At("id_token").Str() is { } newIdToken)
                tokens["id_token"] = idToken = newIdToken;
            root["last_refresh"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            JsonFiles.WriteAtomic(path, root);
            return CodexAuth.From(access, idToken, accountId);
        }
        finally
        {
            FileGate.Release();
        }
    }

    async Task<UsageMeter> FetchApiSpendAsync(string adminKey, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        double total = 0;
        string? page = null;

        for (int i = 0; i < 20; i++)
        {
            var url = $"https://api.openai.com/v1/organization/costs?start_time={monthStart.ToUnixTimeSeconds()}&bucket_width=1d&limit=31"
                + (page is null ? "" : "&page=" + Uri.EscapeDataString(page));
            var result = await Http.SendAsync(HttpMethod.Get, url, [("Authorization", "Bearer " + adminKey)], null, ct);
            if (result.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ProviderException("OpenAI Admin API key was rejected: " + result.Describe());
            result.EnsureSuccess("OpenAI costs");

            if (result.Json.At("data") is JsonArray buckets)
            {
                foreach (var bucket in buckets)
                {
                    if (bucket.At("results") is not JsonArray results)
                        continue;
                    foreach (var item in results)
                        total += item.At("amount").At("value").Num() ?? 0;
                }
            }

            page = result.Json.At("next_page").Str();
            if (result.Json.At("has_more").Bool() != true || page is null)
                break;
        }

        double? budget = Format.TryParseMoney(CredentialStore.Read(Id, "monthlyBudget"), out var amount) ? amount : null;
        return new UsageMeter
        {
            Label = "API spend",
            Used = total / budget,
            Detail = budget is null ? $"{Format.Money(total)} · set a budget" : $"{Format.Money(total)} of {Format.Money(budget.Value)}",
            PeriodStart = monthStart,
            PeriodEnd = monthStart.AddMonths(1),
        };
    }
}
