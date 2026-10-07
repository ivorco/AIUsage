# AIUsage

A Windows tray app (.NET 10, WinForms) that shows how fast you are using your AI subscriptions
**relative to how far you are into each billing/limit period**.

## Tray icon

One bar per provider on a transparent background — Claude (orange), Cursor (blue), ChatGPT (white),
left to right. The same colors are used in the popup.

Each bar shows that provider's **most urgent** meter — normally the one with the least allowance left
(a 5-hour limit at 95% beats a weekly limit at 16%). The fill is the share used, or the pace when that is
higher (half full = on pace, full = twice the pace), so a limit being burned far too fast also shows up.
Gray means no data (not signed in, or an error). Hover for which meter each bar shows; in the popup that
meter's label is highlighted.

Right after a reset, pace is measured against at least 10% of the period, so a little early usage doesn't
look like an alarm. On a light taskbar the white ChatGPT bar is drawn dark gray.

## Popup

Left-click the tray icon. Each meter has:

- a solid orange bar — how much of the allowance you've used,
- a paler orange bar with a marker — how much you *should* have used by now (elapsed share of the period),
- white overlay text — `75% used / 50% expected`,
- the reset/renewal date on the right.

The refresh and gear buttons are in the top-right corner. Data refreshes every 5 minutes.

## What is tracked

| Provider | Meters | Source |
|---|---|---|
| Claude (Pro/Max) | 5-hour limit, weekly, per-model weekly, usage credits | claude.ai `/api/organizations/{org}/usage` (the Settings → Usage page), with Claude Code's `/api/oauth/usage` as a fallback |
| Cursor (Pro/Pro+/Ultra) | Included usage (total / Auto+Composer / API), on-demand spend, Grok Bot weekly | `cursor.com/api/usage-summary`, `cursor.com/api/dashboard/get-sand-usage-status` |
| ChatGPT (Plus/Pro) | Codex weekly and 5-hour windows; plan renewal date | `chatgpt.com/backend-api/wham/usage` |
| OpenAI API (optional) | Month-to-date spend vs. a monthly budget | `api.openai.com/v1/organization/costs` (Admin key) |

The subscription endpoints are the private ones the vendors' own apps use; they may change without notice.

## Authentication

- **Claude** — click **Sign in…** next to *claude.ai session* in Settings and sign in to claude.ai in the
  window that opens (or paste the `sessionKey` cookie from your browser). The session lasts about 30 days.
  claude.ai is behind a Cloudflare browser check, so each refresh loads it in an invisible WebView2 (Edge)
  browser with the session injected as a non-persistent cookie; all cookies are cleared afterwards
  (the browser cache lives in `%LOCALAPPDATA%\AIUsage\WebView2`). Without a session the app tries Claude
  Code's login (`~/.claude/.credentials.json`), but Anthropic often refuses to renew that token outside Claude Code.
- **Cursor** — the Cursor app's login (`%APPDATA%\Cursor\User\globalStorage\state.vscdb`, read-only).
- **ChatGPT** — Codex's `~/.codex/auth.json`. Expired tokens are refreshed and written back so Codex stays signed in.

### Settings (gear icon)

Sign in to claude.ai, override tokens, add an OpenAI Admin API key, and set the API monthly budget.
Everything entered there is stored in **Windows Credential Manager** (generic credentials under `AIUsage/…`,
encrypted by Windows for your user account) — nothing is written to the repo or to settings files. Secret
fields are write-only: the dialog shows whether a value is saved and lets you replace or remove it, but never displays it.

## Build and run

Requires the Microsoft Edge WebView2 Runtime (included with Windows 11).

```bash
dotnet build
```

```bash
dotnet run
```

Only one instance runs at a time. To start it with Windows, put a shortcut to
`bin\Release\net10.0-windows\AIUsage.exe` in `shell:startup` — and exit the tray app before rebuilding Release.

### Test modes

```bash
dotnet run -- --snapshot out
```

Fetches every provider once and writes `snapshot.json` plus PNG renders of the tray icon, popup and settings dialog to `out`.

```bash
dotnet run -- --selftest selftest.txt
```

Checks the Credential Manager round-trip (using a throwaway entry) and the pace math; exit code 0 means everything passed.

## Adding a provider

Implement `IUsageProvider` (`Core/IUsageProvider.cs`) — return `UsageMeter`s with a used fraction and a
period start/end — and add it to `ProviderRegistry`. The tray gets another bar, the popup another section,
and its `SettingField`s show up in the settings dialog (a field can offer a sign-in button via `Acquire`).
