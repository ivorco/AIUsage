using AIUsage.Core;

namespace AIUsage;

/// <summary>
/// <c>AIUsage.exe --selftest &lt;file&gt;</c>: checks the Credential Manager round-trip (with a throwaway
/// "AIUsage/selftest/key" entry that is removed afterwards) and the pace math. Exit code 0 means all passed.
/// </summary>
static class SelfTest
{
    public static int Run(string outputPath)
    {
        var log = new List<string>();
        int failures = 0;
        void Check(string name, bool ok)
        {
            log.Add($"{(ok ? "PASS" : "FAIL")} {name}");
            if (!ok)
                failures++;
        }

        const string provider = "selftest";
        try
        {
            foreach (var length in new[] { 0, 10, 2048, 2049, 7000 })
            {
                var value = string.Concat(Enumerable.Range(0, length).Select(i => (char)('a' + i % 26)));
                CredentialStore.Write(provider, "key", value);
                Check($"credential round-trip, {length} chars", CredentialStore.Exists(provider, "key") && CredentialStore.Read(provider, "key") == value);
            }
            CredentialStore.Write(provider, "key", "short ✓");
            Check("shorter value replaces a multi-part value", CredentialStore.Read(provider, "key") == "short ✓");
        }
        finally
        {
            CredentialStore.Delete(provider, "key");
        }
        Check("credential delete", !CredentialStore.Exists(provider, "key") && CredentialStore.Read(provider, "key") is null);

        var start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var mid = start.AddDays(15);
        UsageMeter Meter(double used) => new() { Label = "test", Used = used, PeriodStart = start, PeriodEnd = start.AddDays(30) };
        Check("expected = elapsed share of the period", Near(Meter(0).Expected(mid), 0.5));
        Check("50% used at mid-period fills the tray bar halfway", Near(Meter(0.5).PaceFill(mid), 0.5));
        Check("75% used at mid-period fills 75%", Near(Meter(0.75).PaceFill(mid), 0.75));
        Check("double pace fills the bar", Near(Meter(1.0).PaceFill(mid), 1.0));
        Check("light usage right after a reset is not an alarm", Near(Meter(0.01).PaceFill(start.AddHours(3)), 0.05));
        Check("heavy usage right after a reset still alerts", Near(Meter(0.3).PaceFill(start.AddDays(1)), 1.0));

        log.Add($"{failures} failure(s)");
        File.WriteAllLines(outputPath, log);
        return failures == 0 ? 0 : 1;
    }

    static bool Near(double? actual, double expected) => actual is double a && Math.Abs(a - expected) < 1e-9;
}
