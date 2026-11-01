using System.Text.Json.Serialization;
using System.Xml.Serialization;
using MediaBrowser.Model.Plugins;

namespace PROTV.Plugin;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    private string _password = string.Empty;
    private bool _passwordWasPlainText;
    private string _vodStreamSecret = string.Empty;
    private bool _vodStreamSecretWasPlainText;

    public string ApiBaseUrl { get; set; } = "http://10.1.0.8:82";

    public string Username { get; set; } = string.Empty;

    // Stored encrypted (enc:v1:...) whenever the secret key can be created. The getter always
    // returns the envelope so Jellyfin's XML/JSON serializers never write the password in the
    // clear; runtime code must read PasswordValue instead.
    public string Password
    {
        get => ProTvSecretStore.Protect(_password);
        set
        {
            var raw = value ?? string.Empty;
            _passwordWasPlainText = !string.IsNullOrEmpty(raw) && !ProTvSecretStore.IsProtected(raw);
            _password = ProTvSecretStore.Protect(raw);
        }
    }

    // The decrypted password for login. Never serialized.
    [XmlIgnore]
    [JsonIgnore]
    public string PasswordValue => ProTvSecretStore.Unprotect(_password);

    // A password saved by an older build is plain text; the plugin rewrites it encrypted on start.
    [XmlIgnore]
    [JsonIgnore]
    public bool PasswordNeedsMigration => _passwordWasPlainText || ProTvSecretStore.NeedsProtection(_password);

    public string AppId { get; set; } = "ca88f40a-3056-4c52-9614-8d75a3b0b178";

    public string DeviceModel { get; set; } = "Jellyfin Server";

    public string DeviceIpAddress { get; set; } = string.Empty;

    public string DeviceMacAddress { get; set; } = string.Empty;

    public string RouterMacAddress { get; set; } = string.Empty;

    public string AppVersion { get; set; } = "2.1.9";

    public string MulticastAddress { get; set; } = "225.2.0.105";

    public int MulticastPort { get; set; } = 1234;

    // Optional: local IPv4 address of the NIC that receives the PROTV multicast traffic.
    // Leave empty to auto-detect. Set it on hosts with several network interfaces.
    public string MulticastInterfaceAddress { get; set; } = string.Empty;

    // How long ffprobe analyses a stream before Jellyfin starts playback, in milliseconds.
    // Lower = faster start, but too low can miss audio/subtitle tracks. 0 = Jellyfin default.
    public int StreamAnalyzeDurationMs { get; set; } = 1500;

    // Short retries for reads from PROTV and a pause after repeated failures (the "service state" the
    // dashboard and the home tab show). Off = every request goes to PROTV as it is, as before.
    public bool LiveApiResilience { get; set; } = true;

    // ---- Dashboard page customisation ----

    // Heading shown at the top of the PROTV dashboard page.
    public string DashboardTitle { get; set; } = "PROTV";

    // Optional message shown under the heading (for example notes for other admins).
    public string WelcomeMessage { get; set; } = string.Empty;

    // Menu entry text and Material icon name for the dashboard menu / Plugin Pages sidebar.
    // Sidebar changes apply after Jellyfin restarts.
    public string SidebarLabel { get; set; } = "PROTV";

    public string SidebarIcon { get; set; } = "live_tv";

    // ---- Interface language ----

    // Language of the PROTV dashboard page and the Jellyfin home page tab.
    //   "auto" - follow each browser's own language (Arabic browsers get Arabic)
    //   "en"   - always English
    //   "ar"   - always Arabic, drawn right to left
    public string UiLanguage { get; set; } = "auto";

    // ---- Update notice ----

    // GitHub repository ("owner/repo") used by the dashboard's update check. Leave it empty to
    // hide the update notice; the check only reads the public latest-release endpoint.
    public string UpdateRepository { get; set; } = string.Empty;

    // ---- Jellyfin home page tab ----

    // Draws a PROTV tab on the Jellyfin home page (next to Favorites) listing every tuner
    // channel. The tab is drawn by the Custom Tabs plugin, so it needs Plugin Pages,
    // File Transformation and Custom Tabs to be installed and loaded.
    public bool FrontpageTabEnabled { get; set; } = true;

    // Text shown on the home page tab and its header.
    public string FrontpageTabTitle { get; set; } = "PROTV";

    // When true PROTV refuses to start (no tuner, no guide, no pages) unless Plugin Pages,
    // File Transformation and Custom Tabs are all installed, instead of running half-working.
    // Turn this off to keep PROTV running without the tab.
    public bool RequireCompanionPlugins { get; set; } = true;

    // ---- VOD (films, series and episodes) ----

    // Mirrors the PROTV film catalogue into the "PROTV VOD" library the plugin creates, so films
    // play through Jellyfin in every app (web, Android TV, mobile) like any other library.
    public bool VodMoviesEnabled { get; set; } = true;

    // Same for series, their seasons and their episodes.
    public bool VodSeriesEnabled { get; set; } = true;

    // Downloads each title's poster into the library. On the first sync this is the slowest part;
    // turn it off to build the library from the metadata alone.
    public bool VodDownloadPosters { get; set; } = true;

    // Downloads the subtitle track PROTV has for a film or an episode next to its stream, so
    // Jellyfin offers it while playing. PROTV ships one for most of the catalogue.
    public bool VodDownloadSubtitles { get; set; } = true;

    // Hard limit, in gigabytes, on everything the sync downloads (posters, subtitles, the library
    // icons). Once it is reached the sync still mirrors the catalogue but stops downloading.
    // 0 = no limit.
    public double VodAssetBudgetGb { get; set; } = 5;

    // The address the ".strm" files point at, i.e. this Jellyfin server. Leave it empty to let the
    // plugin ask Jellyfin for its own LAN address (correct on nearly every setup).
    public string VodStreamBaseUrl { get; set; } = string.Empty;

    // How often Jellyfin runs the VOD sync (hours). Only used the first time the task is created.
    public int VodSyncIntervalHours { get; set; } = 12;

    // Secret that signs the links in the VOD .strm files. Created automatically the first time it
    // is needed; clearing it makes the next sync write new links. Like the password it is stored
    // encrypted, so a config file or config-only backup cannot be used to mint stream links.
    public string VodStreamSecret
    {
        get => ProTvSecretStore.Protect(_vodStreamSecret);
        set
        {
            var raw = value ?? string.Empty;
            _vodStreamSecretWasPlainText = !string.IsNullOrEmpty(raw) && !ProTvSecretStore.IsProtected(raw);
            _vodStreamSecret = ProTvSecretStore.Protect(raw);
        }
    }

    // The decrypted VOD signing secret. Never serialized.
    [XmlIgnore]
    [JsonIgnore]
    public string VodStreamSecretValue => ProTvSecretStore.Unprotect(_vodStreamSecret);

    // A signing secret written by a build before encryption was added.
    [XmlIgnore]
    [JsonIgnore]
    public bool VodStreamSecretNeedsMigration
        => _vodStreamSecretWasPlainText || ProTvSecretStore.NeedsProtection(_vodStreamSecret);

    // Startup persists once if either secret still needs its encrypted envelope.
    [XmlIgnore]
    [JsonIgnore]
    public bool SecretsNeedMigration => PasswordNeedsMigration || VodStreamSecretNeedsMigration;

    // After a complete sync, remove the films and series PROTV no longer lists.
    public bool VodRemoveDeletedTitles { get; set; } = true;

    // Per-channel customisation (rename, hide, order, logo, favourite, group, parental lock).
    // Keyed by the PROTV channel id.
    public ProTvChannelOverride[] ChannelOverrides { get; set; } = Array.Empty<ProTvChannelOverride>();

    // Named channel groups shown as sections on the home page tab. Order = display order.
    // A channel joins a group through its override's GroupId.
    public ProTvChannelGroup[] ChannelGroups { get; set; } = Array.Empty<ProTvChannelGroup>();

    // ---- Parental controls ----

    // When true the tab asks for the PIN before playing a channel whose override has
    // ParentalLocked set. The PIN hash lives in <data>/protv/parental.json, never in this file.
    public bool ParentalEnabled { get; set; }

    // Snapshot of the channels PROTV returned last time, so the dashboard can list them.
    public ProTvKnownChannel[] KnownChannels { get; set; } = Array.Empty<ProTvKnownChannel>();
}

public sealed class ProTvChannelGroup
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

public sealed class ProTvChannelOverride
{
    public int ChannelId { get; set; }

    public string CustomName { get; set; } = string.Empty;

    public bool Hidden { get; set; }

    // Lower numbers come first; channels with the same priority are sorted by name.
    public int Priority { get; set; }

    public string LogoUrl { get; set; } = string.Empty;

    // Shown first in a "Favourites" section on the tab and flagged to Jellyfin's Live TV.
    public bool IsFavorite { get; set; }

    // Id of a ProTvChannelGroup. Empty = no group ("Other channels" on the tab).
    public string GroupId { get; set; } = string.Empty;

    // Hidden behind the parental PIN on the tab when ParentalEnabled is on.
    public bool ParentalLocked { get; set; }
}

public sealed class ProTvKnownChannel
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string LogoUrl { get; set; } = string.Empty;
}
