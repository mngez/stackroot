# Stackroot

A local development environment manager for Windows — .NET 8 + WPF.

Stackroot manages services (nginx, MySQL, Redis, etc.), PHP versions, Node.js, sites (WordPress/Laravel), databases, custom processes, and admin tools — all from a single desktop application.

![Stackroot dashboard](assets/screenshots/dashboard.png?v=2)

![Stackroot services](assets/screenshots/services.png?v=2)

**[MIT License](LICENSE)** — free to use, modify, and distribute with attribution.

---

## Features

### Dashboard
- Real-time service status (nginx, mysql, mariadb, redis, memcached, mongodb, postgresql)
- Live process list with start/stop/restart per process
- Start all / stop all processes
- Featured (pinned) processes appear first
- PHP FastCGI listener status
- Quick links to admin tools

### Services
- Install, update, and configure packages (nginx, PHP, MySQL, MariaDB, Redis, Memcached, PostgreSQL, MongoDB)
- Per-service version selection
- Port configuration
- Service settings dialog
- "Rebuild nginx" for vhost + admin tool regeneration

### Sites
- Create and manage local development sites
- Subdomain or path-based access
- WordPress installer with wp-cli (auto-download, config, install)
- Laravel installer with Composer (starter kits: Breeze, Jetstream)
- Node.js version per site
- Custom commands per site (run any shell command, view logs)
- Site thumbnails via headless Chromium (Playwright)
- PHP version per site
- HTTPS enforcement per site
- Site-specific processes (auto-start, logs, edit)

### Databases
- Create, delete databases (**MySQL / MariaDB / PostgreSQL / MongoDB**)
- Per-database backup and restore for all engines
- Browse all backups across all databases
- Delete individual backup files
- Pick target database when restoring cross-database
- .env snippet copy per database
- phpMyAdmin auto-configuration

### Processes
- Global and per-site custom processes
- Auto-start on app launch
- Featured (star) processes — appear at top, yellow star
- Live status indicator (green dot)
- Process logs viewer
- Start / stop / restart / add / remove

### PHP
- Install multiple PHP versions
- Per-version INI settings editor
- Extension management — toggle extensions, install PECL
- Compact 3-column extension grid
- FastCGI listeners per version

### Node.js
- nvm-windows integration
- Install multiple Node versions
- Version switching
- Quick-install recommended versions
- Custom version input

### Tools
- **PHP**: Composer, WP-CLI, Laravel Installer
- **JavaScript**: pnpm, Vite
- **Utilities**: Git, Python, SQLite CLI, Notepad++
- **Web Panels**: phpMyAdmin, phpRedisAdmin
- Install / update / version management per tool
- Visual distinction: installed (normal), active (green background + green border), downloading (amber), not installed (darker)
- Update checker

### Scheduled Tasks
- Cron-style scheduled tasks (minute / hour / day / month / weekday)
- Run any shell command on a schedule
- Capture task output to log files
- Run Now for immediate execution
- Open logs with preferred editor (VS Code, Cursor, Notepad++, or custom)
- Edit / delete / enable / disable tasks
- Real-time status updates via session activity

### Logs
- Browse log files by category
- Open log files in external editor

### Downloads
- View and manage downloaded package cache
- Remove cached downloads
- Open cache folder

### Performance
- Live memory and process sampling
- Per-process memory usage

### General
- System tray minimize (Close Behavior: Background)
- Hide window on close — shutdown continues in background
- Windows toast notification when all services start + scheduler ready
- Dark theme throughout
- Activity log with session reporting

---

## Quick Start

### Requirements
- Windows 10/11 x64
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (development)
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (installed automatically by the Stackroot setup when missing)

### Install layout

```
%LOCALAPPDATA%\Programs\Stackroot\
  Stackroot.exe              # pinned launcher — same binary, installed once
  current.txt                # active version, e.g. 0.2.1
  app\0.2.1\                 # framework-dependent app payload (changes each release)
    Stackroot.exe            # WPF app host (inside version folder)
    Stackroot.dll
    …
```

The root `Stackroot.exe` is built via `scripts/build-pinned-launcher.ps1` and stored in `installer/pinned/` (the `.exe` is gitignored; `launcher.version` is committed). Release builds copy it into the installer stage. Upgrades **keep** the existing launcher when `launcher.version` matches; the installer replaces it when the protocol changes or legacy layout files are detected.

Rebuild the pinned launcher when `src/Stackroot.Launcher` changes, then run `pack-release.ps1`.

### Local installer

```powershell
./scripts/pack-release.ps1
```

Writes `release/Stackroot-Setup-{Version}.exe` (version from `Stackroot.App.csproj`).


### Run (debug)
```bash
dotnet run --project src/Stackroot.App/Stackroot.App.csproj
```

### Build
```bash
dotnet build Stackroot.sln
```

### Publish installer payload
```powershell
./scripts/publish-installer.ps1
```

---

## Project Structure

```
src/
├─ Stackroot.App/                # WPF shell, navigation, pages, bootstrap
├─ Stackroot.Core.Abstractions/  # shared domain models and contracts
├─ Stackroot.Core.AdminTools/    # phpMyAdmin, phpRedisAdmin, Composer
├─ Stackroot.Core.Catalog/       # package catalog + install/download flows
├─ Stackroot.Core.Databases/     # MySQL/MariaDB management + backup/restore
├─ Stackroot.Core.IO/            # JSON storage, path resolution
├─ Stackroot.Core.Nginx/         # nginx vhost generation
├─ Stackroot.Core.Node/          # Node.js + nvm integration
├─ Stackroot.Core.Observability/ # logs, diagnostics, activity reporting
├─ Stackroot.Core.Services/      # service orchestration, toast, thumbnails
├─ Stackroot.Core.Settings/      # app settings + defaults
├─ Stackroot.Core.Sites/         # sites CRUD, installers (WP, Laravel)
├─ Stackroot.Core.Supervisor/    # background process supervision
└─ Stackroot.Core.Windows/       # Windows helpers (hosts, processes)
```

## Data Directory

`%APPDATA%\Stackroot` — all runtime data, settings, logs, backups, and site configs.

---

*Icon resources: [dashboardicons.com](https://dashboardicons.com/) · [flaticon.com](https://www.flaticon.com/)*
