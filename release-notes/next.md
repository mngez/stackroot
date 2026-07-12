## Stackroot (unreleased)

Working draft for the next version. When you ship, copy this content to `release-notes/{Version}.md`.

**Current target:** **0.3.4** (unreleased — draft notes in `release-notes/0.3.4.md`; do not push until you decide to ship).

### Draft notes (0.3.4)

### PHP

- **Stop stays stopped** — stopping a required PHP listener from the Dashboard no longer brings it back within seconds. Auto-recovery skips versions you stopped until you restart them (or nginx starts and brings the pool up again).

### Logs

- **Log viewer scrolls to the real end** — after refreshing a large log, the view jumps to the latest lines instead of landing mid-document.

### Site dashboard

- **Action buttons wrap cleanly** — Open site, Open admin, custom commands, and Manage commands share one wrapping row so they stay aligned when the window is narrow.

### Site install

- **Database-not-running warning shows reliably** — when installing WordPress or Laravel and the chosen database engine is installed but not running, the warning dialog now appears correctly instead of failing after the port check.

### How publishing works

1. Set `<Version>` in `src/Stackroot.App/Stackroot.App.csproj` (must match the tag).
2. Write release notes in `release-notes/{Version}.md`.
3. Commit on `main`.
4. `./sr push {Version}` or `./sr push {Version} +` to bump csproj when needed — GitHub Actions builds + publishes.

Local installer only: `.\scripts\pack-release.ps1` or `./sr pack`.

`next.md` is only a scratch pad. **`pack-release.ps1` does not read this file.**
