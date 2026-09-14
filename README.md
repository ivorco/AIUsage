# AIUsage

A Windows tray app (.NET 10, WinForms) that shows how fast you are using your AI subscriptions
**relative to how far you are into each billing/limit period**.

## Tray icon

Three orange bars on a transparent background — Claude, Cursor, ChatGPT (left to right).
Each bar shows *pace*, not raw usage:

- **Half full** — exactly on pace (e.g. 50% used halfway through the period).
- **Fuller** — ahead of pace; slow down on that one. Full = twice the pace or more.
- **Gray** — no data (not signed in, or an error). Hover for a per-provider pace summary.

A provider's bar follows its most-ahead weekly/monthly meter. Short windows (5-hour sessions) are
shown in the popup but don't drive the tray. Right after a reset, pace is measured against at least
10% of the period, so a little early usage doesn't look like an alarm.

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
| Claude (Pro/Max) | Weekly, per-model weekly, 5-hour session, extra usage | `api.anthropic.com/api/oauth/usage` (same as Claude Code `/usage`) |
| Cursor (Pro/Pro+/Ultra) | Included usage (total / Auto+Composer / API), on-demand spend, Grok Bot weekly | `cursor.com/api/usage-summary`, `cursor.com/api/dashboard/get-sand-usage-status` |
| ChatGPT (Plus/Pro) | Codex weekly and 5-hour windows; plan renewal date | `chatgpt.com/backend-api/wham/usage` |
| OpenAI API (optional) | Month-to-date spend vs. a monthly budget | `api.openai.com/v1/organization/costs` (Admin key) |

The subscription endpoints are the private ones the vendors' own tools use; they may change without notice.

## Authentication

By default the app reuses the logins already on this PC:

- **Claude** — Claude Code's `~/.claude/.credentials.json`. Expired tokens are refreshed and written back
  so Claude Code stays signed in. If refresh is refused (e.g. the login is months old), run `claude` and `/login`.
- **Cursor** — the Cursor app's login (`%APPDATA%\Cursor\User\globalStorage\state.vscdb`, read-only).
- **ChatGPT** — Codex's `~/.codex/auth.json`, refreshed and written back like Claude.

### Settings (gear icon)

Override tokens, add an OpenAI Admin API key, and set the API monthly budget. Everything entered
there is stored in **Windows Credential Manager** (generic credentials under `AIUsage/…`, encrypted by
Windows for your user account) — nothing is written to the repo or to settings files. Secret fields are
write-only: the dialog shows whether a value is saved and lets you replace or remove it, but never displays it.

## Build and run

```bash
dotnet build
```

```bash
dotnet run
```

Only one instance runs at a time. To start it with Windows, put a shortcut to
`bin\Release\net10.0-windows\AIUsage.exe` in `shell:startup`.

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
and its `SettingField`s show up in the settings dialog.
