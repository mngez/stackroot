## Stackroot (unreleased)

### Processes (supervisor)

- **Restart delay** — set wait time (seconds) before auto-restart when adding or editing a process. Empty = default (2s, then backoff).
- **Process log** — one log per process for the app session; output is appended across restarts instead of rewritten.
- **Auto-restart fix** — supervised processes actually restart after exit (stale duplicate detection no longer blocks the new instance).
- **Log preamble** — `starting` / `cwd` / `command` header appears once at session start, or again only when process settings change (`config updated`).

### Scheduled tasks

- **Delete confirmation** — uses the app’s styled confirm dialog instead of the system `MessageBox`.
