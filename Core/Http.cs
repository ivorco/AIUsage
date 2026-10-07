using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIUsage.Core;

public sealed record HttpResult(HttpStatusCode Status, JsonNode? Json, string Body)
{
    public bool Ok => (int)Status is >= 200 and < 300;

    public void EnsureSuccess(string what)
    {
        if (!Ok || Json is null)
            throw new ProviderException($"{what} failed: {Describe()}");
    }

    public string Describe()
    {
        var message = Json.At("error").At("message").Str()
            ?? Json.At("error").Str()
            ?? Json.At("detail").At("message").Str()
            ?? Json.At("detail").Str()
            ?? Json.At("message").Str();
        return message is null ? $"HTTP {(int)Status}" : $"HTTP {(int)Status} – {message}";
    }
}

public static class Http
{
    static readonly HttpClient Client = CreateClient();

    static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            UseCookies = false, // cookies are passed explicitly per request
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AIUsage/1.0");
        return client;
    }

    public static async Task<HttpResult> SendAsync(HttpMethod method, string url, IEnumerable<(string Name, string Value)>? headers, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        foreach (var (name, value) in headers ?? [])
            request.Headers.TryAddWithoutValidation(name, value);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await Client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? json = null;
        try
        {
            json = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
        }
        return new HttpResult(response.StatusCode, json, text);
    }
}

public static class JsonExtensions
{
    public static JsonNode? At(this JsonNode? node, string key) =>
        node is JsonObject obj && obj.TryGetPropertyValue(key, out var value) ? value : null;

    public static string? Str(this JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? s) ? s : null;

    public static bool? Bool(this JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out bool b) ? b : null;

    public static double? Num(this JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue(out double d))
            return d;
        return value.TryGetValue(out string? s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : null;
    }

    /// <summary>Parses ISO-8601 strings and epoch seconds or milliseconds (numbers or numeric strings).</summary>
    public static DateTimeOffset? Date(this JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue(out string? s))
        {
            if (long.TryParse(s, out var epoch))
                return FromEpoch(epoch);
            return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
        }
        return value.Num() is double number ? FromEpoch((long)number) : null;
    }

    static DateTimeOffset FromEpoch(long n) =>
        n > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(n) : DateTimeOffset.FromUnixTimeSeconds(n);
}

public static class JsonFiles
{
    static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static JsonObject ReadObject(string path) =>
        JsonNode.Parse(File.ReadAllText(path)) as JsonObject
        ?? throw new ProviderException($"{Path.GetFileName(path)} is not a JSON object.");

    /// <summary>Writes via a temp file so the owning tool never sees a half-written file.</summary>
    public static void WriteAtomic(string path, JsonNode root)
    {
        var temp = path + ".aiusage.tmp";
        File.WriteAllText(temp, root.ToJsonString(WriteOptions));
        File.Move(temp, path, overwrite: true);
    }
}

public static class Jwt
{
    public static JsonNode? Payload(string? token)
    {
        var parts = token?.Split('.');
        if (parts is not { Length: >= 2 })
            return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            return JsonNode.Parse(Convert.FromBase64String(payload));
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }

    public static DateTimeOffset? Expiry(string? token) => Payload(token).At("exp").Date();
}

public static class Format
{
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Money(double amount) => "$" + amount.ToString("#,0.00", Invariant);

    public static bool TryParseMoney(string? text, out double amount) =>
        double.TryParse(text?.Trim().TrimStart('$'), NumberStyles.Float | NumberStyles.AllowThousands, Invariant, out amount) && amount > 0;

    public static string Percent(double fraction)
    {
        var percent = fraction * 100;
        return percent is > 0 and < 1 ? "<1%" : Math.Round(percent).ToString("0", Invariant) + "%";
    }

    /// <summary>Pace as a multiple of "on pace": 1.0× is in sync, 2.0× is twice as fast.</summary>
    public static string Pace(double ratio) => ratio switch
    {
        <= 0 => "0×",
        < 0.1 => "<0.1×",
        >= 10 => "10×+",
        _ => ratio.ToString("0.0", Invariant) + "×",
    };

    public static string TitleCase(string text) => Invariant.TextInfo.ToTitleCase(text.Replace('_', ' ').ToLowerInvariant());

    public static string WindowName(TimeSpan? length) => length switch
    {
        null => "limit",
        { TotalDays: 7 } => "weekly",
        { TotalDays: >= 1 } l when l.TotalDays % 1 == 0 => $"{l.TotalDays:0}-day",
        { } l => $"{l.TotalHours:0.#}-hour",
    };

    public static string ResetText(UsageMeter meter, DateTimeOffset now)
    {
        if (meter.PeriodEnd is not { } end)
            return "Not started";
        var local = end.ToLocalTime();
        var length = meter.PeriodStart is { } start ? end - start : (TimeSpan?)null;
        if (length is { TotalDays: >= 27 })
            return "Renews " + local.ToString("MMM d", Invariant);
        if (length is { TotalHours: < 24 })
            return "Resets " + local.ToString(local.Date == now.ToLocalTime().Date ? "HH:mm" : "ddd HH:mm", Invariant);
        return "Resets " + local.ToString("ddd MMM d", Invariant);
    }
}
