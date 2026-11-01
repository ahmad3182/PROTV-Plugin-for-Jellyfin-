# Changelog

## Unreleased

### Added

- **Reliability:** reads from PROTV are retried briefly (at most three tries, with backoff), and five failed
  tries in a row pause requests for a minute so an outage is not hammered; one request then tests PROTV.
  The state (`ok`, `degraded`, `unavailable`) is shown on the dashboard (**Reliability**, with a *Check PROTV status*
  button), in the diagnostic report and at `GET /PROTV/ServiceStatus`. Setting `LiveApiResilience` (default on).
- Every request to PROTV now gives up after 20 seconds.
- A **Docker** bundle (`docker/`): compose file with host networking and a pinned 12.0 tag, plus notes.
- Unit tests for the breaker and the retry rules, and a browser test for the new dashboard section.

### Fixed

- **A rejected request was retried.** A login answered with 401/403, or a request answered with 404, was repeated
  three times (risking an account lockout); a missing Watch endpoint now falls back at once. Only timeouts,
  connection problems and HTTP 5xx / 408 are retried.

## 0.8.0

### Added

- **Dashboard and tab search:** the **All** view searches live channels, films and series in one box,
  with debouncing and stale-response protection.
- **Live TV favourites and groups:** mark channels as favourites and arrange them into named groups
  on the dashboard; the tab shows Favourites first, then the groups in order, then the rest.
- **Parental controls:** lock chosen channels behind a PIN. The PIN is stored as a salted
  PBKDF2-SHA256 hash in `<data>/protv/parental.json`; unlock tokens are HMAC-signed, bound to one
  Jellyfin user and expire after twelve hours. Jellyfin's per-user Live TV permissions remain the
  hard access control.
- **On now / next:** an EPG strip and programme progress on the tab.
- **Channel health check:** probes each stream every six hours and on demand, greys out channels that
  did not answer, and keeps the result in memory only.
- **Settings backup and restore:** JSON export/import of the visible settings, channel overrides and
  groups. Passwords, the VOD signing secret and the parental PIN are excluded.
- **Update notice:** optional GitHub `owner/repo`; the dashboard shows the latest public release and a
  link, with a six-hour cache and a **Check now** button.
- **Arabic interface:** English/Arabic switching with right-to-left layout on the dashboard,
  configuration page and home page tab. Channel, group and programme values are not translated.
- **Real xunit test project** in `tests/PROTV.Plugin.Tests` for sorting/overrides, password encryption,
  parental PIN and token signing, update version parsing, VOD sweep safeguards and genre preservation,
  and live-stream contracts. GitHub Actions builds and runs it.

### Security

- **The PROTV password and the VOD signing secret are encrypted** with AES-GCM before they are written
  to `PROTV.Plugin.xml`. The random key lives in `<data>/protv/secret.key`, outside the plugin settings
  folder. Values written by older builds are migrated on the first start. The key must be moved with
  the server; losing it means re-entering the password. If the key cannot be created, the dashboard
  warns and saving settings fails rather than writing the values in clear text.
- The dashboard never displays the encrypted password; a blank password box keeps the saved value.
  Test connection reuses the saved password only when the username is unchanged.
- Diagnostics redact the password, username, tokens and `VodStreamSecret`, shorten IP addresses, and
  never include signed stream URLs.
- The parental PIN endpoint validates a 4–12 character PIN and never returns the hash or token secret.
  Five wrong attempts lock that Jellyfin user out for ten minutes. If the PIN file is missing or
  damaged, locked channels stay locked instead of becoming public.

### Improved

- **VOD sync status** on the dashboard: last run, live film / series / episode counts, disk used,
  download budget, last error and a **Sync now** button.
- **Genres in NFO files:** the sync now reads the detail endpoint's categories and writes `<genre>`
  entries. Existing genre-less NFOs are repaired one file at a time on the next sync; a forced
  rewrite preserves genres when a detail call fails.
- **VOD sweep safeguards:** folders are removed only after a complete listing that is at least half of
  the previous complete run. Unknown or zero previous counts skip the sweep, and disabled kinds keep
  their stored counts. State writes are atomic and corrupt state is set aside.
- **Retries and clearer errors:** transient PROTV failures are retried, an expired token is refreshed
  once, and the tab keeps the last channel list with a short unavailable banner instead of going blank.
- **Recording:** the live stream now implements Jellyfin's direct-stream contract and opens lazily, so
  Jellyfin's DVR path works with the tuner. Recording and multicast caveats are documented.
- **Dashboard VOD and health polling:** running syncs and health checks update the page automatically.
- **VOD write safety:** .strm, .nfo, poster and subtitle files are written through unique temporary
  files and moved into place, so a library scan never sees a half-written file. Existing .strm
  contents are compared, so a failed migration or a rotated signing secret is repaired on the next
  sync instead of leaving dead links.
- **Release workflow:** builds the DLL, packs the zip, publishes the release and updates
  `manifest.json` from a tag.

### Fixed

- The EPG provider now reads the live `epgItems` response, uses `showTime` and minute durations, and
  converts programme times with the service's own local/UTC offset. **On now / next** and the guide
  line up even when Jellyfin runs in a different time zone.
- A failed dashboard save no longer writes a blank password over the saved one.
- A new password is encrypted immediately, and the configuration page no longer re-sends the old
  password after a save.
- Channel overrides for channels that PROTV temporarily stops listing are kept.
- VOD titles that fail to write are not mistaken for removed titles.

## 0.6.2

### Security

- VOD stream links are now signed. The stream endpoint refuses links without a valid signature, so a
  link cannot be guessed for another title. **Run "Sync PROTV VOD library" once after updating.**

### Added

- **Test connection** button on the dashboard: tries a login with the values on screen.
- **Diagnostics** section: creates a report (versions, companion plugins, settings without secrets,
  VOD state, network, recent warnings) that is safe to paste into a bug report.
- Setting `VodRemoveDeletedTitles`: titles PROTV no longer lists are removed after a complete sync.
- Repository files: `README.md`, `.gitignore`, issue templates, `SECURITY.md`.

### Improved

- One shared PROTV API client (tuner, guide, home tab, VOD) instead of a new client and login per call.
- The library scan after a VOD sync only runs when something changed.
- Series are prepared four at a time under one shared request limit; missing posters are not retried
  for a week; the state file is written safely; stream lookups are shared and cached; series seasons
  load in parallel; browse pages are cached briefly; the companion-plugin check is cached.
