# Stackroot

A modern local development environment built for developers who want complete control over their stack.

Stackroot manages services (nginx, MySQL, Redis, and more), PHP versions, Node.js, sites (WordPress/Laravel), databases, custom processes, and admin tools — all from a single desktop application on Windows. No Docker, no virtual machines, no unnecessary complexity.

![Stackroot dashboard](assets/screenshots/dashboard.png?v=2)

![Stackroot services](assets/screenshots/services.png?v=2)

**[Download the latest release](https://github.com/mngez/stackroot/releases)** · Windows 10/11 (64-bit) · [MIT License](LICENSE)

---

## Why Stackroot?

Stackroot is a local development environment built for Windows — native, fast, and predictable.

Most existing solutions are too opinionated, too heavy, or focused on a single ecosystem. Stackroot brings everything into one place: install services, run sites, switch PHP and Node versions per project, and see exactly what is running.

---

## Philosophy

Stackroot follows a few simple principles.

- **Native first** — runs on Windows as a desktop app, not inside a container or VM.
- **Lightweight** — only what you enable; no background bloat you did not ask for.
- **Modular** — pick your PHP versions, databases, and tools; configure per site.
- **Transparent** — configs, logs, and data paths are visible and reachable from the app.
- **Developer focused** — built for people who work locally every day.

You should understand what Stackroot is doing. Nothing should feel like magic.

---

## Features

### Dashboard
- See at a glance whether nginx, MySQL, MariaDB, Redis, Memcached, MongoDB, and PostgreSQL are running
- Start, stop, or restart any process — or all at once
- Pin the processes you use most so they stay easy to find
- See which PHP versions are active and ready to serve sites
- Jump straight to phpMyAdmin, phpRedisAdmin, and other admin tools

### Services
- Install, update, and configure nginx, PHP, MySQL, MariaDB, Redis, Memcached, PostgreSQL, and MongoDB
- Pick a version for each service and change ports when needed
- Regenerate nginx configs and admin tool URLs after you add or change sites

### Sites
- Create and manage local development sites from one list
- Use subdomain URLs (`myapp.test`) or path-based URLs (`localhost/myapp`)
- Add extra domain names per site, including wildcards
- Install WordPress automatically (downloads wp-cli, creates config, runs the installer)
- Install Laravel with Composer — choose a starter kit (Breeze, Jetstream)
- Set a PHP version and Node.js version per site
- Run custom shell commands per site and read their output
- Preview sites with automatic screenshots on the dashboard
- Force HTTPS per site when you want it
- Proxy a frontend dev server (Vite, webpack, etc.) through your site domain
- Attach processes, databases, and scheduled tasks to a site
- Manage everything from the site dashboard — edit settings, open in browser, terminal, backup, pin, enable or disable
- Back up a site to a zip — files, databases, processes, and scheduled tasks
- Restore a backup over an existing site, or import one as a new site on another machine

### Databases
- Create and delete databases for **MySQL, MariaDB, PostgreSQL, and MongoDB**
- Back up and restore any database
- Browse and delete backup files across all databases
- Restore into a different database name when you need to
- Copy ready-made `.env` connection lines for each database
- phpMyAdmin is configured automatically when you install it

### Processes
- Run your own commands — globally or tied to a specific site
- Start them automatically when Stackroot opens
- Pin important ones so they stay at the top
- See whether each process is running and open its log
- Start, stop, restart, add, or remove at any time

### PHP
- Install and run several PHP versions at the same time
- Edit `php.ini` settings per version
- Enable or disable extensions, and install PECL packages
- Adjust how many workers run per version for heavier workloads

### Node.js
- Install multiple Node versions and switch between them
- One-click install for common LTS releases, or enter any version you need

### Tools
- **PHP**: Composer, WP-CLI, Laravel Installer
- **JavaScript**: pnpm, Vite
- **Utilities**: Git, Python, SQLite CLI, Notepad++
- **Web panels**: phpMyAdmin, phpRedisAdmin
- Install, update, and pick a version for each tool
- See clearly what is not installed, downloading, installed, or currently in use
- Check for tool updates from the app

### Scheduled Tasks
- Schedule shell commands like cron — by minute, hour, day, month, or weekday
- Save task output to a log file
- Run a task immediately without waiting for the schedule
- Open task logs in VS Code, Cursor, Notepad++, or your own editor
- Edit, delete, enable, or disable tasks anytime
- See when a task last ran and whether it is running now

### Logs
- Browse log files grouped by category
- Open any log in your preferred editor

### Downloads
- See what installers Stackroot has downloaded and how much space they use
- Clear the cache to free disk space
- Open the downloads folder directly

### Performance
- Watch memory use across services and processes as you work
- Includes all PHP workers for a version, not just one process

### General
- **Test DNS** — `.test` domains (and other suffixes you choose) resolve on your machine; keeps working in the background even when the Stackroot window is closed
- **Trust SSL** — browse local sites over HTTPS; trust the certificate for your user only, or for everyone on the PC
- **14 languages** — change the interface language in Settings without restarting
- Close the window to the system tray and keep services running
- Start Stackroot automatically when Windows boots
- Optionally use Stackroot's PHP, Composer, and Node from any terminal
- Get a notification when startup finishes and everything is ready
- Dark theme throughout
- Activity log for the current session — what started, stopped, and changed
- Set your WWW folder, backup location, preferred editor, and how long to keep logs

---

## Getting started

**Requirements:** Windows 10/11 (64-bit). The installer includes the .NET Desktop Runtime if needed.

1. Download the latest `Stackroot-Setup-{version}.exe` from [Releases](https://github.com/mngez/stackroot/releases) and run the installer.
2. Open Stackroot. Go to **Services**, install the packages you need (nginx, PHP, MySQL, etc.), then start them from the **Dashboard**.
3. Add your first site from **Web domains** — pick a template, choose a name and domain, and create.
4. Optional: enable **Test DNS** and **Trust SSL** in **Settings** for `.test` domains and HTTPS without browser warnings.

Upgrades over an existing install keep your sites, databases, and settings.

For version history, see [release-notes/CHANGELOG.md](release-notes/CHANGELOG.md).

---

*Developers and contributors: see [CONTRIBUTING.md](CONTRIBUTING.md).*

*Icon resources: [dashboardicons.com](https://dashboardicons.com/) · [flaticon.com](https://www.flaticon.com/)*
