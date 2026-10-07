using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AIUsage.Core;
using Microsoft.Data.Sqlite;

namespace AIUsage.Providers;

/// <summary>
/// Cursor individual plan: included usage, on-demand spend and the weekly Grok Bot allowance, from the
/// cursor.com dashboard endpoints. Auth comes from the Cursor app's login unless a session token is set.
/// </summary>
public sealed class CursorProvider : IUsageProvider
{
    const string CookieName = "WorkosCursorSessionToken";

    public string Id => "cursor";
    public string DisplayName => "Cursor";
    public Color Accent => Color.FromArgb(0x4A, 0x8B, 0xF5);

    public IReadOnlyList<SettingField> Settings { get; } =
    [
        new("sessionToken", "Session token", "Optional. The WorkosCursorSessionToken cookie from cursor.com. Overrides the Cursor app login.", IsSecret: true),
    ];

    public async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        var manual = CredentialStore.Read(Id, "sessionToken")?.Trim();
        var cookie = string.IsNullOrEmpty(manual) ? CookieFromJwt(ReadLocalToken()) : NormalizeCookie(manual);

        (string, string)[] headers = [("Cookie", $"{CookieName}={cookie}"), ("Origin", "https://cursor.com"), ("Referer", "https://cursor.com/dashboard")];
        var summaryTask = Http.SendAsync(HttpMethod.Get, "https://cursor.com/api/usage-summary", headers, null, ct);
        var grokTask = Http.SendAsync(HttpMethod.Post, "https://cursor.com/api/dashboard/get-sand-usage-status", headers, new JsonObject(), ct);

        var summary = await summaryTask;
        if (summary.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ProviderException(string.IsNullOrEmpty(manual)
                ? "Cursor login expired. Open Cursor to sign in again."
                : "The Cursor session token saved in Settings was rejected.");
        summary.EnsureSuccess("Cursor usage");

        var json = summary.Json!;
        var cycleStart = json.At("billingCycleStart").Date();
        var cycleEnd = json.At("billingCycleEnd").Date();
        var meters = new List<UsageMeter>();

        var plan = json.At("individualUsage").At("plan");
        if (plan is JsonObject && plan.At("enabled").Bool() != false)
        {
            void AddPercent(string label, string field)
            {
                if (plan.At(field).Num() is double percent)
                    meters.Add(new UsageMeter { Label = label, Used = percent / 100, PeriodStart = cycleStart, PeriodEnd = cycleEnd });
            }
            AddPercent("Included usage", "totalPercentUsed");
            AddPercent("Auto + Composer", "autoPercentUsed");
            AddPercent("API models", "apiPercentUsed");
        }

        var onDemand = json.At("individualUsage").At("onDemand");
        if (onDemand.At("enabled").Bool() == true)
        {
            var usedCents = onDemand.At("used").Num() ?? 0;
            var limitCents = onDemand.At("limit").Num();
            meters.Add(new UsageMeter
            {
                Label = "On-demand",
                Used = limitCents > 0 ? usedCents / limitCents : null,
                Detail = limitCents > 0 ? $"{Format.Money(usedCents / 100)} of {Format.Money(limitCents.Value / 100)}" : $"{Format.Money(usedCents / 100)} spent",
                PeriodStart = cycleStart,
                PeriodEnd = cycleEnd,
            });
        }

        string? note = json.At("isUnlimited").Bool() == true ? "Unlimited plan" : null;
        try
        {
            var grok = await grokTask;
            if (grok.Ok && grok.Json is { } status)
            {
                if (status.At("hasNonZeroIncludedLimit").Bool() != false && status.At("usagePercent").Num() is double percent)
                {
                    meters.Add(new UsageMeter
                    {
                        Label = "Grok Bot · weekly",
                        Used = percent / 100,
                        PeriodStart = status.At("currentPeriodStart").Date(),
                        PeriodEnd = status.At("nextResetTimestampUtc").Date(),
                    });
                }
            }
            else
            {
                note = "Grok Bot usage unavailable: " + grok.Describe();
            }
        }
        catch (HttpRequestException e)
        {
            note = "Grok Bot usage unavailable: " + e.Message;
        }

        return new ProviderSnapshot(Id, DisplayName, PlanName(json.At("membershipType").Str()), meters, note, DateTimeOffset.Now);
    }

    static string? PlanName(string? membership) => membership switch
    {
        null => null,
        "pro_plus" => "Pro+",
        _ => Format.TitleCase(membership),
    };

    static string ReadLocalToken()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Cursor", "User", "globalStorage", "state.vscdb");
        if (!File.Exists(path))
            throw new ProviderException("No Cursor login found. Sign in to Cursor, or set a session token in Settings.");

        var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM ItemTable WHERE key = 'cursorAuth/accessToken'";
        var token = command.ExecuteScalar() switch
        {
            string s => s,
            byte[] b => Encoding.UTF8.GetString(b),
            _ => null,
        };

        if (string.IsNullOrEmpty(token))
            throw new ProviderException("Cursor is not signed in. Sign in to Cursor, or set a session token in Settings.");
        if (Jwt.Expiry(token) < DateTimeOffset.UtcNow)
            throw new ProviderException("Cursor login expired. Open Cursor to refresh it.");
        return token;
    }

    /// <summary>The dashboard cookie is "{userId}::{jwt}" (URL-encoded), where userId is the JWT subject's last segment.</summary>
    static string CookieFromJwt(string jwt)
    {
        var subject = Jwt.Payload(jwt).At("sub").Str() ?? throw new ProviderException("Cursor token has no user id.");
        return $"{subject[(subject.LastIndexOf('|') + 1)..]}%3A%3A{jwt}";
    }

    static string NormalizeCookie(string value)
    {
        if (value.StartsWith(CookieName + "=", StringComparison.Ordinal))
            value = value[(CookieName.Length + 1)..];
        if (value.Contains("%3A%3A", StringComparison.OrdinalIgnoreCase))
            return value;
        return value.Contains("::", StringComparison.Ordinal) ? value.Replace("::", "%3A%3A") : CookieFromJwt(value);
    }
}
