# Clotch

Clotch is a Windows system tray app for monitoring **Claude Code / Claude subscription** usage.
Usage is shown as a small *notch* at the top of the screen.

- Appears in the system tray when launched.
- Detects **Claude Code** (npm / native) and **Claude Desktop** (MSIX or legacy Squirrel), then checks for an active subscription (Pro / Max / Team / Enterprise).
- If one is found, the notch appears at the top center of the screen and shows the % of the 5-hour session limit and the % of the weekly limit.
- The notch can be **dragged**, and its position is saved. **Click** it to see details. The notch can be **hidden** with the ✕ button or from the tray menu.

## Running

```sh
# from source (requires the .NET 10 SDK)
dotnet run --project src/Clotch.App -c Release

# build a self-contained single-file exe (no .NET needed on the target machine, ~170 MB)
dotnet publish src/Clotch.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/publish

# small alternative (~350 KB), requires the .NET 10 Desktop Runtime to be installed
dotnet publish src/Clotch.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o artifacts/publish-fd
```

`EnableCompressionInSingleFile` is intentionally not used. It shrinks the exe to ~75 MB, but RAM usage rises from ~49 MB to ~170 MB.

Only one instance runs at a time. If `Clotch.exe` is launched again, the running instance shows its notch.

## Usage

| Action | Result |
|---|---|
| Click the notch row | Expand / collapse details |
| Drag the notch | Move it. The position is clamped to the screen and saved |
| ✕ on the notch / "Sembunyikan" (Hide) | Hides the notch. This state is saved |
| Left-click the tray icon | Show / hide the notch. If the notch is unavailable, an explanation is shown |
| Right-click the tray icon | Menu: refresh, open the claude.ai usage page, reset position, refresh interval (2–30 minutes), hide while fullscreen, run at Windows startup, log folder, exit |

The details panel contains:

- Plan and data status (Live / cache).
- Every limit the server reports, with reset time and countdown.
- Weekly usage breakdown per product (Claude Code / Chats / Cowork / Other).
- Extra usage status.
- **Local tokens**: Claude Code tokens on this machine per model, for today and the active 5-hour window. Read from `~/.claude/projects/**/*.jsonl`.

The notch does not appear in the following cases. The reason is shown in the tray tooltip / balloon:

| Condition | Message |
|---|---|
| Neither Claude Code nor Claude Desktop is installed | not detected |
| Only Claude Desktop, not logged in to Claude Code | Claude Code login required |
| Claude Code installed, not logged in | run `claude`, then `/login` |
| Free / unknown plan | no active subscription |
| Token rejected by the server and no cached data yet | log in again |

## Data sources & privacy

- **Credentials**: `%USERPROFILE%\.claude\.credentials.json`, or `CLAUDE_CONFIG_DIR` if set. Clotch **only reads** this file. It never writes it and never refreshes the token. Token refresh stays Claude Code's job, so Clotch cannot log your Claude Code session out. If the token has expired, Clotch stops calling the API until Claude Code refreshes it.
- **Usage**: `GET https://api.anthropic.com/api/oauth/usage`, the endpoint used by the `/usage` command in Claude Code. This endpoint is **unofficial** and may change. The parser is therefore lenient: missing or unknown fields do not crash the app, and new keys are displayed under their raw names.
- **Rate limit**: by default Clotch calls the endpoint once every 5 minutes (minimum 2 minutes). On failure, the interval backs off 10 → 20 → 30 minutes (maximum). The server's `Retry-After` is honored. Manual refresh is limited to once per minute. The last successful data stays on screen, marked "cache".
- **Network**: Clotch only contacts `api.anthropic.com`.
- **Logs**: `%LOCALAPPDATA%\Clotch\logs`. Every log line goes through token redaction, so no `sk-ant-…` or `Authorization` header is ever recorded.
- **Settings**: `%APPDATA%\Clotch\settings.json`.
- **Autostart**: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `Clotch`.

## Structure

```
src/Clotch.Core   detection, credentials, usage client + parser, monitor/backoff, JSONL scanner, gating, settings
src/Clotch.App    WPF: tray (WinForms NotifyIcon), NotchWindow, view model
src/Clotch.Diag   clotch-diag: JSON dump of what Clotch sees (tokens redacted)
tests/Clotch.Tests  xUnit
tools/make_icon.py  icon generator (Python stdlib)
```

## Verification

```sh
dotnet build Clotch.slnx -c Release
dotnet test Clotch.slnx -c Release
dotnet run --project src/Clotch.Diag -c Release            # exit 0 = notch will show, 2 = it won't
dotnet run --project src/Clotch.Diag -c Release -- --no-network
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v Clotch
grep -rE "sk-ant-|Bearer " "$LOCALAPPDATA/Clotch/logs"      # must be empty
```

## Known limitations

- The usage endpoint is undocumented. If Anthropic changes it, the limits panel will be empty, but local tokens will still be shown.
- Some keys in the server response are code names (e.g. `iguana_necktie`). These are shown as-is, without guessing their meaning.
- The units of `monthly_limit` / `used_credits` in extra usage are undocumented, so only the server's percentage is shown.
- Usage cannot be read for users who only use Claude Desktop (without a Claude Code login). This is intentional: Clotch does not extract Claude Desktop's cookies.
