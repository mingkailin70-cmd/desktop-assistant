# Contributing

小K is a Windows-first local assistant. Please read AGENTS.md, PLAN.md and TODO.md before changing behavior.

## Milestones and commits

Keep each completed phase reviewable and record it as a focused commit. Suggested prefixes:

- feat(p1): ... for the resident desktop shell
- feat(p2): ... for local voice
- feat(p3): ... for tools and safety
- feat(p4): ... for first-release scenarios and packaging
- docs: ..., build: ..., fix: ... for supporting changes

Do not force-push main or rewrite published phase history. Do not mark a phase complete without its stated local evidence and acceptance record.

## Privacy and dependencies

Never commit model weights, caches, databases, notification bodies, screenshots, recordings, credentials, tokens, personal evaluation samples, or local settings. Keep user data outside the checkout.

Third-party package additions, model/runtime downloads, and new network commands require explicit review before they are run. Pin versions and record license and artifact hashes for runtime/model additions.

## Local checks

The current dependency-free solution can be built with .NET SDK 10.0.401:

    dotnet build XiaoK.sln --configuration Release

Do not claim WeChat/QQ notification support, voice support, or message sending until tested with their actual Windows permission and application behavior. All sends require an exact preview and user approval.
