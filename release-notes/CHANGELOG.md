## Stackroot 0.3.3

Download and run `Stackroot-Setup-0.3.3.exe` on **Windows 10/11 (64-bit)**. Update over **0.3.2** — sites, databases, and settings are kept.

### Site commands

- **Running commands survive navigation** — if you leave a site's Manage page while a custom command or quick action is still running, returning to the page now rediscovers it instead of leaving the process untrackable in the background.
- **Stop and log controls come back** — reconciled commands restore their running row, status banner, **Stop**, and **View log** controls so you can cancel or follow output after navigating away.
- **Completion status updates correctly** — when a rediscovered command finishes, the Manage page shows whether it succeeded, failed, or was cancelled without duplicating status for commands you started in the same visit.

### App shutdown

- **Cleaner exit on Windows logoff or restart** — when Windows is shutting down, signing out, or restarting, Stackroot now runs a fast bounded shutdown instead of waiting on close-behavior prompts or long-running background tasks. Managed services get a chance to stop cleanly before the OS force-kills the app.

### Upgrading from 0.3.2

Your sites, databases, and settings are preserved. No manual steps required.

---

## Stackroot 0.3.2

Download and run `Stackroot-Setup-0.3.2.exe` on **Windows 10/11 (64-bit)**. Update over **0.3.1** — sites, databases, and settings are kept.

### Sites data protection

- **Warning when `sites.json` cannot be read** — the Sites page shows a clear banner instead of silently showing an empty list.
- **Saving is blocked until you repair** — if the site registry file is unreadable, Stackroot refuses to write changes so an accidental save cannot overwrite your real data with an empty registry.
- **Restore from backup** — when a valid `sites.json` backup exists, a one-click **Restore from backup** button on the Sites page brings your site list back without hunting for `.bak` files on disk.

### Data reliability

- **Startup no longer blocked by a corrupted JSON file** — if migration cannot read a data file, Stackroot logs the failure, keeps a forensic copy, and continues starting; the normal load path handles repair or fallback.
- **Safer JSON writes** — every settings and registry write is read back and validated before it is allowed to replace the real file on disk.
- **Smarter backup restore** — automatic restore ignores `.invalid-*.bak` snapshots (copies of content that already failed to parse), so a corrupted file cannot keep restoring a newer broken copy and bury the last good backup.

### Test DNS

- **Restart actually rebinds the listener** — clicking **Restart** on Dashboard or Services now forces the DNS helper to stop and reopen its `127.0.0.1:53` socket. Previously, a wedged listener that still reported "running" could ignore a restart because unchanged settings were republished with no effect.
- **Restart signal does not linger** — after the helper applies a restart, the one-shot token is cleared from disk so a later helper service reboot does not trigger an extra socket restart.

### Upgrading from 0.3.1

Your sites, databases, and settings are preserved. No manual steps required.

---

## Stackroot 0.3.1

Download and run `Stackroot-Setup-0.3.1.exe` on **Windows 10/11 (64-bit)**. Update over **0.3.0** — sites, databases, and settings are kept.

### Site backup

- **Backup list refreshes correctly** — if you navigated away from a site's Manage page while a backup was running, the new backup now appears in the list as soon as it finishes, even if you're no longer on the page that started it.
- **Skip symbolic links** — on by default; turn it off to include symlinked folders (e.g. Laravel's `storage:link`) as real files in the backup. A warning explains that the link itself won't be restored as a link when this is off.
- **Custom ignore patterns** — exclude paths from the backup using `.gitignore`-style patterns, with one-click presets for `node_modules`, `.git`, and `vendor`.

### Site restore

- **Warns before deleting excluded paths** — if a backup was made with ignore patterns, restoring files now lists anything on disk that was excluded from that backup and would otherwise be permanently deleted; check any item to keep it in place.
- Fixed warning banners in the Restore and Import dialogs cutting off long text instead of wrapping.

### Upgrading from 0.3.0

Your sites, databases, and settings are preserved. No manual steps required.


---

## Stackroot 0.3.0

Download and run `Stackroot-Setup-0.3.0.exe` on **Windows 10/11 (64-bit)**. Update over **0.2.9** — sites, databases, and settings are kept.

### Site backup

Back up any site with a single action — from the secondary action button on the site row or the **Backup** button on the site Manage page.

- Choose what to include: **site files**, **databases**, **processes**, and **scheduled tasks**
- **Archive site** mode — back up then delete the site in one step (files, databases, processes, and tasks are all removed after a successful backup)
- Progress is tracked in the activity tray; the UI stays responsive while large sites compress
- Backup is saved as a `.zip` file to the configured backups folder (default: `{DataRoot}/backups/sites/`)
- A **"Backup in progress"** banner locks site actions while the backup runs to avoid inconsistent state

The **Manage** page lists all backups found for the current site, sorted by date, with file size and timestamp.

### Site restore

From within the site's **Manage** page you can roll a site back to any of its own backups. Two entry points, same flow:

- **From the backup list** — click Restore on any entry; the backup is already tied to this site, no conflict check needed
- **Restore from backup** — browse to any `.zip` file; if the backup belongs to a different site, a warning is shown before proceeding

Before the restore runs, Stackroot shows a **delta preview** — a per-item diff across site files, databases, processes, and scheduled tasks:

- **Restore** — item is in the backup but not currently present; will be written back
- **Replace** — item exists and will be overwritten with the backup version
- **Delete** — item exists now but is not in the backup; will be removed

Every item can be toggled individually. Uncheck a **Delete** item to keep it; uncheck a **Restore** or **Replace** item to skip it.

After confirming, the site is overwritten in place — files replaced, databases restored, processes and tasks reset to the backup state. nginx is reloaded automatically.

### Site import

Import a backup as a **new site** — from the sites toolbar, pick a `.zip` file to import.

The dialog shows what the backup contains and checks for conflicts before allowing the import. All conflicts must be resolved manually before the import can proceed:

- **Domain conflict** — the site's domain already exists; the conflicting site must be removed or renamed first
- **Alias conflicts** — one or more domain aliases from the backup already exist on another site; they must be removed from that site first
- **Database conflicts** — one or more database names are already in use; they must be renamed or dropped first

Once all conflicts are cleared, the import creates the new site and restores its files, databases, processes, and scheduled tasks.

### Enhanced site deletion

The **Remove site** dialog now offers fine-grained control over what gets deleted alongside the site entry:

- **Also delete site files** — removes the site folder on disk
- **Also delete databases** — drops linked databases from the server
- **Also delete processes** — removes linked supervisor processes
- **Also delete scheduled tasks** — removes linked cron tasks

All options default to off; previously only "delete files" was available.

### Custom backups directory

Set a custom folder for all backups under **General Settings → Backups folder**.

- Applies to both site backups and database backups
- Leave empty to use the default path (`{DataRoot}/backups`)
- **Migration**: on first launch, existing database backup files in `{DataRoot}/backups/` are automatically moved into `{DataRoot}/backups/databases/` to match the new layout

### Data reliability

- **File read retry** — settings and registry reads now retry up to 4 times (25 ms apart) when a concurrent write briefly holds an exclusive lock, preventing spurious read errors under parallel I/O

### Upgrading from 0.2.9

Your sites, databases, and settings are preserved. On first launch, database backup files are migrated to the new subfolder layout automatically. No manual steps required.

---

## Stackroot 0.2.9

Download and run `Stackroot-Setup-0.2.9.exe` on **Windows 10/11 (64-bit)**. Update over **0.2.8** — sites, databases, and settings are kept.

### Multi-language UI

Stackroot now ships in **14 languages**. Switch language from **General Settings → Language** — the entire interface updates instantly without restarting.

Available languages: English, Arabic , Deutsch, Español, Français, Italiano, 日本語, 한국어, Nederlands, Polski, Português, Русский, Türkçe, 简体中文.



### Nginx stability fix

Nginx no longer crashes with "Nginx did not start listening" on restart or recovery. 

### Antivirus compatibility

- **Bin shims are no longer recreated unnecessarily** — previously, every app launch deleted and rewrote all command shims in the `runtime\bin` folder (`php80.cmd`, `git.cmd`, etc.), which caused antivirus software to treat them as new files and clear any exceptions you had added. Shims are now updated only when their content actually changes (e.g. after installing or updating a package). Antivirus exceptions survive normal restarts and stay in place until the underlying tool changes.



## Stackroot 0.2.8

Download and run `Stackroot-Setup-0.2.8.exe` on **Windows 10/11 (64-bit)**. Update over **0.2.7** — sites, databases, and settings are kept.

### PHP worker pool

- **Multiple workers per PHP version** — each PHP version now runs a pool of workers (default: 2) instead of a single process. nginx load-balances requests across the pool, so a worker recycling after its request limit causes no visible downtime.
- **Instant failover on recycle** — when a worker exits (normal php-cgi recycling after 10 000 requests), nginx immediately routes to its siblings and the exited slot is respawned. No dropped requests, no restart notification.
- **Configurable pool size** — in **PHP → Runtime settings**, set the number of workers per version (1–8). Increase for high request-concurrency workloads; leave at 2 for typical dev use.
- **Surviving a crash** — if a worker exits unexpectedly, the pool self-heals: the supervisor detects the exit and starts a replacement.

### HTTPS certificates

- **No repeated administrator prompts when adding sites** — previously, adding a site triggered two UAC prompts (clean old CA, install new CA) when **machine-wide SSL trust** was enabled. Stackroot now re-signs the server certificate using the existing trusted CA instead of generating a new one, so no trust changes are needed and no prompts appear.
- Administrator approval is still required the first time you trust the CA, and once a year when the CA renews — both of which are expected.

### Stability and reliability

- **"1 operation running" no longer gets stuck** — the activity row for a service start is now reliably closed when the service stops or fails to start, instead of remaining open indefinitely.
- **No spurious "Starting…" notifications** — a short port-probe timeout no longer incorrectly marks a healthy running service as starting again.
- **PHP memory reporting includes all workers** — the Performance page now reports combined memory for the whole worker pool instead of only the first process.

### Upgrading from 0.2.7

Your sites, databases, and settings are preserved. On first launch after upgrading, each PHP version starts with a pool of 2 workers. If you changed the **PHP port** in Runtime settings, the pool occupies a contiguous block of ports starting from that base (e.g. base 9000 with 2 workers uses 9000 and 9001 per version). Adjust **Runtime settings → Workers per version** if needed.




## Stackroot 0.2.7

Download and run `Stackroot-Setup-0.2.7.exe` on **Windows 10/11 (64-bit)**. Update over **0.2.6** — sites, databases, and settings are kept.

### Test DNS

- **Resolve to IP** — choose which address local site names resolve to (default `127.0.0.1`, e.g. your LAN IP). NRPT still sends queries to `127.0.0.1:53` on this PC; the hosts file uses the same IP.
- **Configurable suffixes** — under **Suffixes and recovery**, choose which suffixes route to Stackroot DNS (default `.test`).
- **Safe vs public suffixes** — reserved suffixes like `.test` resolve any matching name to your resolve IP. Real public suffixes only resolve **your Stackroot site names** locally; other names keep using normal internet DNS.
- **Stackroot DNS Helper service** — Test DNS runs in a Windows background service (`StackrootDnsHelper`). NRPT and the 127.0.0.1:53 listener stay active when you quit the app; turn Test DNS off in settings to clean routing through the helper.
- **Starts with Windows** — when Test DNS is enabled, the helper starts immediately at boot (not on Windows’ delayed-start schedule), so dev DNS is ready as soon as the PC comes up instead of leaving a gap with no listener after reboot.
- **Administrator approval** — Windows may ask once when you first enable Test DNS, to register or repair the helper service. Day-to-day site changes and quitting the app do not repeat that prompt.
- **DNS stays in sync with sites** — saving or editing a site updates the helper config; the service reloads it automatically.
- **Log DNS queries (optional)** — enable **Log DNS queries to file** to append queries to `Logs\test-dns-queries.jsonl` (off by default).
- **Recovery command** — if routing breaks, **Suffixes and recovery** includes a one-click copy of a PowerShell cleanup command (stop/remove the helper service and Stackroot NRPT rules). Run it in an elevated PowerShell window.

### Startup and dashboard

- **Smoother first launch after upgrade** — less UI churn and scroll jank while services start; Test DNS and service rows no longer flicker through false “stopped” states during startup.
- **Lighter background polling** — dashboard status refreshes read cached Test DNS state instead of running slow Windows probes every few seconds.
- **Scheduled tasks wait for startup** — cron tasks do not run until enabled services have finished starting.
- **Faster page navigation** — Node and PHP pages warm up in the background after startup finishes, so the first visit feels instant without blocking the dashboard.

### Services page

- **Loads when you open it** — the Services page no longer runs a heavy refresh at app launch; it warms up after startup or on first visit.

### Edit site & HTTPS

- **Dev proxy directives** — under **Edit site → Dev proxies**, set `proxy_pass` and add optional nginx directives per proxy; your changes are saved with the site and survive restarts.
- **Safer site saves** — Stackroot runs `nginx -t` before writing a site; a bad proxy or vhost config is rejected and the previous nginx file is restored.
- **SSL trust scope** — in **Settings**, choose whether **Trust SSL** installs the local CA for your Windows user only (default, no administrator prompt) or for all users on the PC (requires administrator approval once).

### App shell

- **Version beside the app name** — the sidebar shows the installed version (e.g. `0.2.7`) in small muted text next to **Stackroot**.

### Stability and reliability

- **SSL certificates work correctly when multiple sites start together** — when several HTTPS sites initialize at the same time, certificates are now generated one at a time rather than all at once, preventing corrupted cert files or failed trust on first launch.
- **Dev DNS errors are no longer silent** — if the DNS helper fails to apply a new configuration, Stackroot now reports the error instead of continuing as if nothing went wrong.
- **Cleaner app shutdown** — closing Stackroot while services are still starting or restarting no longer risks hanging the shutdown screen.
- **Service keep-alive counts restarts accurately** — the automatic restart tracker no longer records a failed attempt when the service was not actually in a position to restart, avoiding premature cooldown periods or missed restart alerts.
- **Service restarts during app shutdown complete gracefully** — if a keep-alive restart is mid-flight when you close Stackroot, it now exits cleanly instead of surfacing an internal error.
- **Process cleanup on exit** — internal background processes now release their OS handles reliably when the app closes, reducing leftover resource usage.

### Development (`./sr dev`)

- **More reliable dev launch** — avoids a broken Windows apphost blocking `dotnet` runs; warns if Stackroot is already running.

Download `Stackroot-Setup-0.2.7.exe` below and run the installer. **Close any running Stackroot instance** (tray → Quit) before upgrading.

### Upgrading from 0.2.6

Your sites, databases, and settings are preserved. The installer updates the DNS helper files under `%LOCALAPPDATA%\Programs\Stackroot\dns-helper` and clears stale helper status during setup. If you already enabled Test DNS on an older build, saving settings once may ask for administrator approval to repair the Windows service registration.




## Stackroot 0.2.6

Download and run `Stackroot-Setup-0.2.6.exe` on **Windows 10/11 (64-bit)**. Update over **0.2.5** — sites, databases, and settings are kept.

### Site dashboard

- **Edit, pin, and enable/disable** — manage the site from the dashboard header without going back to the sites list.
- **Scheduled tasks** — view, add, run, and edit cron tasks for this site at the bottom of the dashboard; link to the full Scheduled Tasks page when needed. The add/edit dialog shows **Capture output to log file** without clipping.

### Quick actions & logs

- **Cancel running commands** — stop a long quick action from the log viewer or the **Cancel** button on the status banner; cancellation is faster and the button stays visible with **Cancelling…** until the process stops.
- **Run again** — when a command finishes, rerun it from the same log window and stream the new output without closing the dialog.
- **Status banner** — dismiss (×) is hidden while a command runs; it returns when the command finishes or is cancelled.
- **No automatic timeout** — quick actions run until they finish or you cancel them.
- **Terminal-style logs** — colored output and line layout match a real console (Pest, PHPUnit, npm, Composer).
- **Log window size** — the in-app log viewer reopens at the size you last used.
- **Smarter log updates** — live refresh stops when a command finishes; **Refresh** appears only when live updates are off.
- **Cleaner log output** — command logs no longer show a `[stderr]` prefix; `#` comment lines use muted coloring.

### Custom commands

- **On every site template** — custom commands live inside each template’s dashboard card (WordPress, Laravel, Empty), alongside **Open site** and related actions.
- **Manage commands window** — add, edit, delete, and style site commands from one resizable dialog (gear icon on the card).
- **Import and export** — copy command buttons between sites via a portable file (labels, commands, colors, and icons).
- **Button styling** — optional text/background colors (with a color picker) and a custom icon (saved per site); custom colors stay on hover.
- **Safer delete** — a running command must be stopped before it can be removed.

### Edit site

- **No default Vite proxy** — new Laravel sites start with an empty proxy list; add dev proxies only when you need them.
- **Dev proxy rows** — collapsed proxy blocks have a taller header and a clearer expand control.

### Nginx

- **HTTP performance settings** — under **Services → Nginx → Settings**, tune gzip, workers, upload limits, logging, and PHP/proxy timeouts with sensible defaults.
- **Help on every option** — hover the **!** icon beside a setting for a short explanation.
- **Manage nginx.conf manually** — optional mode stops Stackroot from rewriting the main `nginx.conf`; open the file from the dialog and edit it yourself (site vhosts in `sites-enabled` are still updated).
- **Stronger defaults** — gzip, larger upload limits, and hash tuning in the main nginx config unless you change them.

### PHP

- **Performance settings per version** — from **PHP → Settings** for each installed version, tune limits, OPcache, path cache, and error display with fast dev defaults.
- **Help on every option** — hover the **!** icon for a plain-language explanation.
- **Manage php.ini manually** — optional full control over the generated ini; Stackroot stops patching it until you turn automatic mode back on.
- **Faster defaults** — unlimited memory/time for dev, 512M uploads (matches nginx), and OPcache enabled for quicker page loads.
- **PHP logs in logs folder** — `php-8.x.log` for script errors and `php-cgi-php-8.x.stderr.log` for the FastCGI process.

Download `Stackroot-Setup-0.2.6.exe` below and run the installer. **Close any running Stackroot instance** (tray → Quit) before upgrading.

### Upgrading from 0.2.5

Your sites, databases, and settings are preserved. Existing Laravel sites may still have an old Vite proxy entry — remove it in **Edit** if you do not use it.



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

