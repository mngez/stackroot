# Contributing to Stackroot

Technical reference for building, running, and releasing Stackroot from source. End-user documentation is in [README.md](README.md).

---

## Requirements

- Windows 10/11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (development)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (installed automatically by the Stackroot setup when missing)

---

## Quick commands

### Run (debug)

```powershell
dotnet run --project src/Stackroot.App/Stackroot.App.csproj
```

Or use the repo helper:

```powershell
./sr dev
```

### Build

```powershell
dotnet build Stackroot.sln
```

### Local installer

```powershell
./scripts/pack-release.ps1
```

Writes `release/Stackroot-Setup-{Version}.exe` (version from `Stackroot.App.csproj`).

### Publish installer payload

```powershell
./scripts/publish-installer.ps1
```

---

## Install layout (released app)

```
%LOCALAPPDATA%\Programs\Stackroot\
  Stackroot.exe              # pinned launcher — same binary, installed once
  current.txt                # active version, e.g. 0.2.9
  app\0.2.9\                 # framework-dependent app payload (changes each release)
    Stackroot.exe            # WPF app host (inside version folder)
    Stackroot.dll
    …
```

The root `Stackroot.exe` is built via `scripts/build-pinned-launcher.ps1` and stored in `installer/pinned/` (the `.exe` is gitignored; `launcher.version` is committed). Release builds copy it into the installer stage. Upgrades **keep** the existing launcher when `launcher.version` matches; the installer replaces it when the protocol changes or legacy layout files are detected.

Rebuild the pinned launcher when `src/Stackroot.Launcher` changes, then run `pack-release.ps1`.

---

## Data directory

`%APPDATA%\Stackroot` — all runtime data, settings, logs, backups, and site configs.

When changing on-disk JSON shape: bump schema constant, add a migrator step that runs at app start **before** any `Load()`, and keep defensive read-path logic until migrations are proven sufficient. See `.cursor/rules/stackroot-impact-analysis.mdc` in the repo for hotspots (shutdown order, settings, installer, etc.).

---

## Project structure

```
src/
├─ Stackroot.App/                # WPF shell, navigation, pages, bootstrap
├─ Stackroot.Core.Abstractions/  # shared domain models and contracts
├─ Stackroot.Core.AdminTools/    # phpMyAdmin, phpRedisAdmin, Composer
├─ Stackroot.Core.Catalog/       # package catalog + install/download flows
├─ Stackroot.Core.Databases/     # MySQL/MariaDB/PostgreSQL/MongoDB + backup/restore
├─ Stackroot.Core.IO/            # JSON storage, path resolution, migrations
├─ Stackroot.Core.Nginx/         # nginx vhost generation
├─ Stackroot.Core.Node/          # Node.js + nvm integration
├─ Stackroot.Core.Observability/ # logs, diagnostics, activity reporting
├─ Stackroot.Core.Services/      # service orchestration, toast, thumbnails
├─ Stackroot.Core.Settings/      # app settings + defaults
├─ Stackroot.Core.Sites/         # sites CRUD, installers (WP, Laravel)
├─ Stackroot.Core.Supervisor/    # background process supervision
└─ Stackroot.Core.Windows/       # Windows helpers (hosts, processes)
```

---

## Releases

Publishing is tag-driven via `./sr push {version}` (or push the tag manually). GitHub Actions runs `.github/workflows/release.yml`.

Before pushing:

1. Bump `<Version>` in `src/Stackroot.App/Stackroot.App.csproj` (must match the version you push).
2. Add `release-notes/{version}.md`.
3. Commit on `main`.

```powershell
./sr push 0.3.0      # csproj must already match
./sr push 0.3.0 +    # update csproj, commit, then push
```

Both stop immediately if `release-notes/{version}.md` is missing. Re-run to replace an existing release with a fresh CI build.

Release notes live in `release-notes/`; user-facing history is merged into `release-notes/CHANGELOG.md`.

---

## Pull requests and commits

- Focus changes on the requested scope; trace impact across startup, shutdown, settings, and UI when touching core paths.
- Commit messages and release notes should describe **user-visible outcomes**, not internal task labels. See `.cursor/rules/user-facing-changelog.mdc`.

---

## License

[MIT License](LICENSE)
