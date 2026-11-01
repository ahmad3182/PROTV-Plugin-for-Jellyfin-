# Security

## Reporting a problem

If you find a security problem (for example a way to reach someone's streams, or a way to read the
saved PROTV login), please **do not open a public issue**. Use GitHub's private reporting instead:
**Security > Report a vulnerability** on this repository.

## Please never post

- your PROTV username or password,
- the files `PROTV.Plugin.xml` and `<data>/protv/secret.key` (the XML holds encrypted secrets, but the
  key can decrypt them),
- full Jellyfin logs without checking them first.

The **Diagnostics > Create diagnostic report** button on the PROTV dashboard page makes a report that
leaves passwords and secrets out and shortens IP addresses. Use that in bug reports.

## Notes

- The PROTV password and the VOD link-signing secret are encrypted with AES-GCM before they are
  written to `PROTV.Plugin.xml`. The random key is stored in `<data>/protv/secret.key`, outside the
  plugin settings folder. This protects against screenshots and config-only backups, not against
  someone who can read the whole Jellyfin data folder.
- If the key is lost, the saved password and signing secret are unreadable. Re-enter the password; a
  new signing secret is generated and the next VOD sync rewrites the links. If the key cannot be
  created, saving settings fails instead of writing the password or signing secret in clear text.
- Stream links in the VOD library are signed. If the signing secret leaked, clear `VodStreamSecret`
  in the settings, restart Jellyfin and run the VOD sync again to write new links.
- The parental PIN is stored as a salted PBKDF2-SHA256 hash in `<data>/protv/parental.json`, not in
  the plugin XML. Unlock tokens are HMAC-signed, bound to one Jellyfin user, sent in the
  `X-PROTV-Unlock` header (not a URL), and expire after twelve hours. The tab is a convenience lock;
  use Jellyfin's per-user Live TV permissions for hard access control.
- The plugin is an early-development community project made with the help of AI. Review it before
  you run it on a server you care about.
