# 小K · Local Desktop Assistant

小K is a Windows desktop pet and assistant being built for a single local PC. Its first release is planned to handle six flows: open applications, find files, run coding tasks in an isolated workspace, analyze visible private-message notifications, draft replies, and send only after the user reviews the recipient, text, and attachments.

The assistant uses local inference only. It does not route prompts to Codex or silently fall back to a cloud model. WeChat and QQ themselves still need their normal network connection for messaging.

## Current implementation status

This repository is in the first engineering slice, not a release:

- .NET 10.0.401 is pinned in global.json.
- A WPF floating host, tray menu, Ctrl+Shift+K shortcut, cancel and stop-microphone controls compile.
- Fixed tools currently cover allowlisted application launch and bounded file-name search.
- The inference client accepts loopback HTTP only.
- Message-notice policy and a draft MSIX manifest exist, but Windows notification listening is not yet installed or connected.
- Audio capture, local model process management, isolated coding agent, browser controls, and WeChat/QQ sending are not yet implemented.
- Task state temporarily uses a small JSON file outside the repository. SQLite migration is required before release.
- No model weights, runtime packages, private message content, user data, or local credentials belong in this repository.

P0 gates still need local evidence: at least 30 visible private-chat notifications per app, model/voice latency and VRAM measurements, and the Chinese coding-task evaluation. The app must not be treated as ready for daily use until all six scenarios pass normal, failure, and cancellation paths.

## Build

Prerequisite: Windows and .NET SDK 10.0.401. The SDK can be installed from Microsoft's official .NET downloads. No third-party NuGet package is currently used or restored.

    dotnet --version
    dotnet build XiaoK.sln --configuration Release
    dotnet run --project src/XiaoK.Host/XiaoK.Host.csproj

The local source configuration is intentionally empty while third-party dependencies await review. The repository pins the SDK with global.json.

## Local data and settings

- Settings: %LOCALAPPDATA%\XiaoK\settings.json
- Task state: D:\XiaoK\Data\tasks.json
- Planned models/cache/evaluation data: D:\XiaoK\Models, D:\XiaoK\Cache, D:\XiaoK\Evaluations

Copy and adapt src/XiaoK.Host/settings.example.json to the settings path. The default model endpoint is http://127.0.0.1:8080/; only loopback endpoints are accepted. Model downloads and third-party runtime installation are not part of this repository.

## Development

See PLAN.md, TODO.md, architecture.md, and development.md. Each completed milestone should be recorded as a focused commit on main. Keep all model files and personal data outside Git.

This repository currently has no license file. Public visibility does not grant permission to reuse the code; add a license only after the owner chooses one.
