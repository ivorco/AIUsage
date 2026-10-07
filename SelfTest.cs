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
        Check("a period that just started doesn't divide by zero", Near(Meter(0).PaceFill(start), 0) && Near(Meter(0.005).PaceRatio(start), 0.5));
        Check("heavy usage right after a reset still alerts", Near(Meter(0.3).PaceFill(start.AddDays(1)), 1.0));

        UsageMeter Window(string label, double used, TimeSpan length, double elapsed) =>
            new() { Label = label, Used = used, PeriodStart = mid - length * elapsed, PeriodEnd = mid + length * (1 - elapsed) };

        // The three cases the tray bar is built around.
        var week = TimeSpan.FromDays(7);
        Check("70% used at 70% elapsed is in sync", Near(Window("w", 0.7, week, 0.7).PaceRatio(mid), 1.0) && Near(Window("w", 0.7, week, 0.7).PaceFill(mid), 0.5));
        Check("40% used at 20% elapsed is twice the pace", Near(Window("w", 0.4, week, 0.2).PaceRatio(mid), 2.0) && Near(Window("w", 0.4, week, 0.2).PaceFill(mid), 1.0));
        Check("20% used at 40% elapsed is half the pace", Near(Window("w", 0.2, week, 0.4).PaceRatio(mid), 0.5) && Near(Window("w", 0.2, week, 0.4).PaceFill(mid), 0.25));

        var snapshot = new ProviderSnapshot("test", "Test", null,
        [
            Window("weekly", 0.16, week, 0.5),
            Window("5-hour", 0.6, TimeSpan.FromHours(5), 0.2),
        ], null, mid);
        Check("tray shows the limit furthest ahead of pace", snapshot.TrayMeter(mid)?.Label == "5-hour" && Near(snapshot.TrayFill(mid), 1.0));
        var inSync = snapshot with
        {
            Meters = [Window("weekly", 0.5, week, 0.5), Window("5-hour", 0.1, TimeSpan.FromHours(5), 0.5)],
        };
        Check("everything on pace fills the bar halfway", inSync.TrayMeter(mid)?.Label == "weekly" && Near(inSync.TrayFill(mid), 0.5));

        log.Add($"{failures} failure(s)");
        File.WriteAllLines(outputPath, log);
        return failures == 0 ? 0 : 1;
    }

    static bool Near(double? actual, double expected) => actual is double a && Math.Abs(a - expected) < 1e-9;
}
