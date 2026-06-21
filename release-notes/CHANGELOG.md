# Stackroot release history

## Stackroot 0.2.5

Download and run `Stackroot-Setup-0.2.5.exe` on **Windows 10/11 (64-bit)**. Update over **0.2.4** â€” sites, databases, and settings are kept.

### Reliability

- **Steadier RAM over long sessions** â€” background port lookups and header metrics no longer let stale cache entries accumulate; memory stays closer to a flat line during all-day use.
- **Lighter port checks** â€” fewer redundant process lookups when a recent port result is still valid.
- **Performance page cleanup** â€” leaving Performance releases its last full snapshot instead of holding it in the background.

### Dashboard

- **Uptime tooltips** â€” fixed a handler leak that could keep tooltip timers alive after rows were recycled.

### Install

- **Old app versions removed on upgrade** â€” the launcher is refreshed when its protocol changes, then drops previous `app\{version}` folders on the next start so upgrades do not pile up on disk.

Download `Stackroot-Setup-0.2.5.exe` below and run the installer. **Close any running Stackroot instance** (tray â†’ Quit) before upgrading.

### Upgrading from 0.2.4

Your sites, databases, and settings are preserved. No extra steps â€” quit, install, and reopen Stackroot once.

---

## Stackroot 0.2.4

Download and run `Stackroot-Setup-0.2.4.exe` on **Windows 10/11 (64-bit)**. Update over **0.2.3** â€” sites, databases, and settings are kept.

### Dashboard

- **Shared live status** â€” services, PHP listeners, custom processes, Mailpit, and Test DNS use one background status feed; Dashboard, Services, Processes, and PHP stay aligned instead of each page polling on its own.
- **No 10-second refresh loop** â€” Dashboard rows update from live events. When you return from the tray, the latest status shows immediately.
- **Health badges** â€” three indicators in the title bar: **Web stack**, **Processes**, and **Scheduler**; **Starting web stackâ€¦** while services are still coming up.
- **RAM Â· CPU** in the title bar (turns off while the window is hidden to the tray).
- **Uptime tooltips** â€” hover a running service, PHP listener, or process; the tooltip keeps updating while it stays open.
- **Unexpected stop alert** â€” red border on a service row when it stops on its own (not after you clicked Stop).
- **Keep-alive notifications** â€” toast when a supervised service stops unexpectedly, and again every 10 failed auto-restart attempts.
- **Start All** â€” waits for services to finish starting and reports how many succeeded, failed, or are still starting.
- **Startup message** â€” one toast when the web stack is ready; mentions the cron scheduler when it started successfully.

### Test DNS (.test wildcard)

- **Its own row** on Dashboard and Services (Web) â€” Start, Stop, Restart, and settings (previously only a checkbox under Sites settings).
- **Own settings dialog** â€” enable/disable and auto-start live under **Test DNS**, not Sites settings.
- **Restart without a second admin prompt** â€” Dashboard Restart reloads the listener only when the Windows DNS rule is already in place.
- **Failed save rolls back** â€” if enabling Test DNS or saving settings fails, the dialog stays open and your previous choices are restored.

### Performance

- **PHP listeners table** added alongside services and processes, with memory totals and optional CPU %.
- Rebuilt to use the shared metrics service; heavy sampling runs only while the Performance page is open.
- Lists update row by row instead of clearing and rebuilding every refresh.

### Processes & PHP

- **Processes** and **PHP** pages read the same live status as the Dashboard; detailed polling pauses when you leave those pages.
- **PHP and Node pages** â€” view models warm up in the background after startup so the first visit is quicker.

### Services (reliability)

- **Fewer false restarts** â€” a slow or inconclusive port check no longer restarts a service whose process is still running.
- **Faster port checks** on Windows when resolving which process owns a port.
- **Auto-restart** â€” up to two supervised restarts can run in parallel after an unexpected stop (manual restarts are not limited).
- **Cleaner quit** â€” background supervision and status polling stop as soon as you quit Stackroot.
- **More stable launches** â€” managed services no longer capture stdout/stderr, which avoids rare hangs on long-running daemons.

### Scheduled tasks

- **Per-site cron** â€” optional site on each task (leave empty for app-wide). Existing tasks stay app-wide after upgrade.
- **UI** â€” site column, filter, and picker in the add/edit dialog.
- **Safer task list** â€” a read error no longer wipes your tasks; the same task cannot run twice at once.
- **Long commands** â€” due tasks run in the background so the scheduler stays responsive.

### Settings & startup

- **Damaged `settings.json`** â€” Stackroot starts with defaults, warns on **General settings**, and blocks saving until you restore from a nearby `.bak` backup.
- **Settings save fix** â€” stops rewriting settings with an outdated internal version on every save.
- **Faster window open** â€” the main window appears sooner; Mailpit, PHP configs, and web-stack finalize continue in the background.
- **Tray** â€” background status checks slow to about once a minute while hidden; they speed up when you show the window again.

### Logs

- **Log viewer** â€” keeps twice as much tail in memory (512 KB instead of 256 KB).

### Other

- **Site thumbnails** â€” one capture at a time; **Capture / Refresh** always replaces the preview immediately. After install, an existing preview younger than 24 hours is reused so Stackroot does not reopen the browser unnecessarily.

### Install

Download `Stackroot-Setup-0.2.4.exe` below and run the installer. **Close any running Stackroot instance** (tray â†’ Quit) before upgrading.

### Upgrading from 0.2.3

Your sites, databases, and settings are preserved. After upgrade:

- Open Stackroot once so scheduled-task and settings file upgrades run automatically.
- If you use **Test DNS**, open its settings once to confirm enable and auto-start â€” it now has its own dialog on Dashboard and Services.
- **Restart services** (or quit and reopen Stackroot) so live status and supervision use the new behavior.

---

## Stackroot 0.2.3

Download and run `Stackroot-Setup-0.2.3.exe` on **Windows 10/11 (64-bit)**. Update over **0.2.2** â€” sites, databases, and settings are kept.

### Safer deletes (overflow menu)

- **â‹® menu** â€” destructive actions (Delete / Remove) are hidden behind a three-dots button instead of sitting inline in lists.
- **Confirm before delete** â€” databases, backups, downloads, sites, processes, scheduled tasks, Node/nvm, and dev proxies all show the app confirm dialog first.
- **Sites** â€” the â‹® delete control is the last action on each row (after Disable / Enable).
- **Site custom commands** â€” no â‹® on each command button; **Ctrl+right-click** a command to remove it (confirm dialog first).

### Logs

- **In-app log viewer** â€” custom site commands, quick actions, and scheduled task logs open in the built-in log window by default.
- **Ctrl+click** â€” opens the same log file in your preferred editor (General settings).
- **Custom commands** â€” output is written under `%AppData%\Stackroot\logs\sites\{siteId}\` (same area as quick-action logs), not `%TEMP%`.

### Web stack stability (fewer 502s)

- **Service priority** â€” nginx, php-cgi, MySQL, Redis, and other managed services start with **Above Normal** process priority so they are less likely to starve under desktop load.
- **nginx** â€” higher `worker_connections`, longer FastCGI/proxy timeouts (up to 600s), larger buffers; applied in main config, site vhosts, phpMyAdmin, and phpRedisAdmin.
- **PHP defaults** â€” new installs: no artificial `memory_limit` / `max_execution_time` cap (`-1` / `0`); longer socket/input timeouts. Re-save PHP version settings or restart PHP-CGI to refresh an existing `php.ini`.
- **MySQL / MariaDB** â€” higher `max_connections`, longer `wait_timeout`, larger `max_allowed_packet`.
- **PostgreSQL** â€” more connections and buffer headroom for local dev.
- **Memcached** â€” Stackroot no longer forces a 64 MB `-m` cap at launch.

### Tools & site install

- **Laravel Installer (Tools)** â€” Composer output streams live during install; fixes progress stuck at 25% and reduces pipe deadlocks on long installs.
- **Laravel site install** â€” Composer lines appear in the site dashboard and activity tray while the site installer runs.

### Install

Download `Stackroot-Setup-0.2.3.exe` below and run the installer. **Close any running Stackroot instance** (tray â†’ Quit) before upgrading.

### Upgrading from 0.2.2

Your sites, databases, and settings are preserved. After upgrade, **restart services** (or quit and reopen Stackroot) so nginx/PHP pick up the new timeout and priority behavior. To apply new PHP defaults on an existing machine, open **PHP â†’ version settings â†’ Save** for each version you use.

---

## Stackroot 0.2.2

Download and run `Stackroot-Setup-0.2.2.exe` on **Windows 10/11 (64-bit)**. Fresh install or update over **0.2.1** â€” sites, databases, and settings are kept when upgrading.

### Web domains

- **Domain aliases** â€” add extra names per site (e.g. `sameapp.test`, `*.app.test`) in Add/Edit site. nginx and HTTPS include all aliases; literal names are added to hosts automatically.
- **Wildcard DNS (optional)** â€” Sites settings â†’ *Resolve .test domains locally*. Routes only `.test` to Stackroot DNS on `127.0.0.1` (supports `*.app.test`). Disabled by default; other domains and internet traffic are unchanged.

### Site dashboard

- **SSL paths** â€” button at the top of the site dashboard opens local `dev.crt` / `dev.key` paths with per-field copy (for Node servers, Echo Server, `.env`).

### Processes (supervisor)

- **Restart delay** â€” set wait time (seconds) before auto-restart when adding or editing a process. Empty = default (2s, then backoff).
- **Process log** â€” one log per process for the app session; output is appended across restarts instead of rewritten.
- **Auto-restart fix** â€” supervised processes actually restart after exit (stale duplicate detection no longer blocks the new instance).
- **Log preamble** â€” `starting` / `cwd` / `command` header appears once at session start, or again only when process settings change (`config updated`).

### Scheduled tasks

- **Delete confirmation** â€” uses the appâ€™s styled confirm dialog instead of the system `MessageBox`.

### PHP extensions (PIE / PECL)

- **PHP 8.1** â€” PIE installs no longer fail when `zip` is built into PHP without a separate `php_zip.dll` (common on official NTS builds).
- **PIE runner** â€” `openssl` and `zip` are loaded before `pie.phar` runs, fixing false â€œopenssl extension requiredâ€ errors during PECL installs.
- **Imagick** â€” PIE package name corrected to `imagick/imagick` (PIE 1.4+ rejects the old `ext-imagick` alias).

### Install layout (thin launcher)

- **Pinned entry point** â€” root `Stackroot.exe` is a small launcher (~700 KB); the WPF app lives in `app\0.2.2\`.
- **Pinned launcher** â€” reads `current.txt` and starts `app\{version}\Stackroot.exe`.
- **Setup** â€” installs .NET 8 Desktop Runtime when missing (online first, bundled fallback). Installer fix: no longer skips the .NET install step on upgrade.
- **Upgrade from 0.2.0 / 0.2.1** â€” removes leftover self-contained files from the install root (`hostfxr.dll`, runtime DLLs, `resources/`, language folders). Without this cleanup, a hybrid install root shows a false â€œ.NET requiredâ€ dialog even when the runtime is installed.

### Install

Download `Stackroot-Setup-0.2.2.exe` below and run the installer. **Close any running Stackroot instance** (tray â†’ Quit) before upgrading.

### Upgrading from 0.2.1

Your sites, databases, and settings are preserved. The installer updates the app payload under `app\0.2.2\` and refreshes `current.txt`. The pinned launcher in the install root is replaced only when its protocol version changes.

If you upgraded from **0.2.0** earlier and Stackroot failed to start from the desktop shortcut with a â€œ.NET requiredâ€ message, reinstall with this build â€” it cleans legacy files from the install folder automatically.

---

## Stackroot 0.2.1

Windows installer (NSIS). Recommended upgrade from **0.2.0**.

### HTTPS / local SSL

- Local dev certificates are generated with .NET (no external OpenSSL required).
- nginx is prepared with HTTPS config and certificates before start/reload.
- Automatic repair when nginx reports missing certificate files.
- **Trust local CA** banner in the main window when the Stackroot CA is not yet trusted on Windows.
- Misplaced SSL files from older config paths are migrated automatically.
- **Auto-renewal**: server certificates renew ~30 days before expiry (same CA â€” no re-trust needed in most cases).

> After upgrading, nginx SSL may be enabled automatically via settings migration. You may see a one-time prompt to trust the local CA.

### Database backup & restore

- Restore into an existing database with optional **Replace database** (drop + recreate) in the target picker dialog.
- **UTF-8 fix**: Arabic and other Unicode text in SQL backups import correctly (stdin encoding + `utf8mb4`).
- Closing the restore target dialog or the app no longer starts a restore by accident (`IsConfirmed` only on **Restore**).
- Safer shutdown: background tasks are tracked; restore is blocked once shutdown begins.

### Activity notifications

- Tray badge shows **running operations** (amber) vs **unread** items (green).
- Badge supports counts up to **99+**.
- Pulse animation on the bell when new activity starts.
- Footer bar in the activity panel: â€œN operations runningâ€ with progress indicator.

### App update check

- Deferred check for new GitHub releases after startup.
- Update banner in the main window with download and install flow.

### Other fixes & improvements

- `ApplicationShutdownState` uses volatile flags for cross-thread shutdown visibility.
- Service supervision stops during shutdown.
- nginx SSL repair logic consolidated in `NginxControl` (less duplication).

### Install

Download `Stackroot-Setup-0.2.1.exe` below and run the installer. **Close any running Stackroot instance** (tray â†’ Quit) before upgrading.

### Upgrading from 0.2.0

0.2.0 works without HTTPS; 0.2.1 completes local HTTPS support and fixes restore/encoding issues. Your sites, databases, and settings are preserved. If HTTPS is new for you, trust the local CA when prompted, then reload sites if needed.

---

## Stackroot 0.2.0

Windows installer (NSIS).

### PHP profile import & export

- Export a single PHP version profile or all installed profiles as JSON (single file or bundle).
- Import profiles from the PHP page toolbar (**Import profiles** / **Export profiles**) or per-version **â‹¯** menu.
- Import installs missing PHP versions, required services (e.g. Redis), and PECL extensions automatically.
- Applies `php.ini` overrides and per-version settings; restarts the FastCGI listener when needed.
- Starts newly installed services after import and refreshes the Services page.
- Activity tray shows progress during export and import.

### Other improvements since 0.1.0

- JSON data migrations with schema versions for on-disk documents.
- Performance page split into app/services and custom process cards.
- Improved NSIS installer shutdown flow before file extraction.
- Apache removed from the service model and defaults.

### Install

Download `Stackroot-Setup-0.2.0.exe` below and run the installer. Close any running Stackroot instance before upgrading.

