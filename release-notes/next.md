## Stackroot (unreleased)

Working draft for the next version. When you ship, copy this content to `release-notes/{Version}.md`.

**Current target:** next release after published **0.2.4**.

### How publishing works

1. Set `<Version>` in `src/Stackroot.App/Stackroot.App.csproj` (must match the tag).
2. Write release notes in `release-notes/{Version}.md` — **not** in `0.2.4.md` after that version is already on GitHub.
3. `.\scripts\pack-release.ps1` — local build; `pack-release.ps1 -Publish` uses `release-notes/{Version}.md`.
4. Tag `v{Version}` and push — CI validates the tag against the csproj version and publishes the Setup exe.

`next.md` is only a scratch pad. **`pack-release.ps1` does not read this file.**
