## Stackroot (unreleased)

Working draft for the next version. When you ship, copy this content to `release-notes/{Version}.md`.

**Current target:** **0.3.6** (notes in `release-notes/0.3.6.md`).

### Draft notes (0.3.6)

See `release-notes/0.3.6.md`.

### How publishing works

1. Set `<Version>` in `src/Stackroot.App/Stackroot.App.csproj` (must match the tag).
2. Write release notes in `release-notes/{Version}.md`.
3. Commit on `main`.
4. `./sr push {Version}` or `./sr push {Version} +` to bump csproj when needed — GitHub Actions builds + publishes.

Local installer only: `.\scripts\pack-release.ps1` or `./sr pack`.

`next.md` is only a scratch pad. **`pack-release.ps1` does not read this file.**
