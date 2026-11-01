# PROTV on Jellyfin in Docker

> Unofficial, free, made with AI and in early development. Needs your own PROTV subscription
> (PROTV is available in Syria only). Back up `config/` before you start.

## 1. Start Jellyfin

```bash
cd docker
mkdir -p config cache
docker compose up -d
```

Open `http://<server>:8096` and finish the Jellyfin setup. Check the compose file with
`docker compose config` first if you changed it. If Jellyfin runs as a normal user instead of root,
uncomment the `user:` line and make `config/` and `cache/` owned by that user.

The image tag (`12.0`) is pinned. Jellyfin 12 changes the database on first start and cannot be
downgraded, so keep a copy of `config/` before changing the tag.

## 2. Install the plugins

PROTV needs three companion plugins by IAmParadox27 (Plugin Pages, File Transformation, Custom Tabs).
Without them PROTV switches itself off, unless `RequireCompanionPlugins` is turned off.

1. Jellyfin Dashboard > Plugins > Repositories: add the catalogue of each companion plugin (see their
   project pages) and the PROTV catalogue (the `manifest.json` of this project).
2. Dashboard > Plugins > Catalog: install **Plugin Pages**, **File Transformation**, **Custom Tabs**,
   then **PROTV**.
3. Restart the container: `docker compose restart jellyfin`.
4. Open the PROTV entry in the dashboard sidebar, enter your PROTV username and password, save, and run
   **Sync PROTV VOD library** once (Dashboard > Scheduled Tasks, or the button on the PROTV page).

Manual install instead: unzip the PROTV release into `config/plugins/PROTV_<version>/` and restart.

## 3. Multicast checklist (live TV)

PROTV live channels are UDP multicast groups such as `225.2.0.105:1234`.

- **Host networking is mandatory.** `network_mode: host` is already set. On a bridge network the
  container never sees the multicast packets.
- **Use a wired connection** for the server (and ideally the player). Wi-Fi often drops or throttles
  multicast, which shows up as stuttering or channels that never start.
- **Firewall:** allow inbound UDP on the stream port (1234) from your PROTV network, and IGMP. For example, with
  `ufw`: `sudo ufw allow in proto udp to 225.2.0.0/16 port 1234` and `sudo ufw allow in proto igmp`.
  With `firewalld`, add the same to the zone of the interface that receives the stream.
- **IGMP snooping:** if the switch or router does IGMP snooping it needs an IGMP querier, or the
  group membership times out after a few minutes (the stream stops about 3 to 5 minutes in).
- **Several network interfaces?** Set the PROTV setting *Multicast interface* to the IPv4 address of
  the interface that receives the stream.
- **Check membership** while a channel plays: `ip maddr show` should list the group on the interface,
  and `ss -u -a | grep 1234` should show the socket.
- **Rootless Docker, Docker Desktop (Windows / macOS)** and some NAS "container managers" may not give
  real host networking, in which case multicast will not reach Jellyfin. Use a Linux host, or run
  Jellyfin natively.

## 4. Updating

Update PROTV from Dashboard > Plugins, then restart. After an update that changes the VOD links or
metadata, run **Sync PROTV VOD library** once (the changelog says when).
