using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;

namespace PROTV.Plugin;

// One channel as the Jellyfin home page tab shows it: the same number, name and logo the
// PROTV tuner hands to Jellyfin, including the dashboard renames / hidden flags / ordering.
public sealed class ProTvTabChannel
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("number")]
    public string Number { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("logoUrl")]
    public string? LogoUrl { get; set; }

    // The Jellyfin Live TV channel item this PROTV channel became. The tab plays it, so the
    // channel opens in the same player Live TV uses (and in every Jellyfin app).
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = string.Empty;

    // False while Jellyfin has not stored the channel item yet (no guide refresh since the
    // channel appeared), which is when playback cannot work either.
    [JsonPropertyName("inLiveTv")]
    public bool InLiveTv { get; set; }

    // The last channel check's answer: "online", "offline" or "unknown" (never checked, or checked
    // before this Jellyfin started). The tab greys out a channel PROTV is not streaming right now.
    [JsonPropertyName("health")]
    public string Health { get; set; } = "unknown";

    // Why it is offline. Never a stream address or a token.
    [JsonPropertyName("healthMessage")]
    public string? HealthMessage { get; set; }

    // Favourite flag from the dashboard. Lives in ProTvChannelOverride.IsFavorite.
    [JsonPropertyName("favorite")]
    public bool Favorite { get; set; }

    // Id of the dashboard group this channel belongs to; empty = "Other channels".
    [JsonPropertyName("groupId")]
    public string GroupId { get; set; } = string.Empty;

    // True when the parental PIN has not been entered for this user and the dashboard marked the
    // channel as locked. The tab asks for the PIN before playing it.
    [JsonPropertyName("locked")]
    public bool Locked { get; set; }
}

public sealed class ProTvTabGroup
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public sealed class ProTvTabChannels
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "PROTV";

    // The language the dashboard is set to: "auto", "en" or "ar". The tab draws itself right to
    // left when it resolves to Arabic, and swaps its own strings for Arabic ones.
    [JsonPropertyName("language")]
    public string Language { get; set; } = "auto";

    [JsonPropertyName("total")]
    public int Total { get; set; }

    // Dashboard groups in display order. Group names are user data and are never translated.
    [JsonPropertyName("groups")]
    public IReadOnlyList<ProTvTabGroup> Groups { get; set; } = Array.Empty<ProTvTabGroup>();

    [JsonPropertyName("channels")]
    public IReadOnlyList<ProTvTabChannel> Channels { get; set; } = Array.Empty<ProTvTabChannel>();
}

public sealed class ProTvTabRequirement
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("installed")]
    public bool Installed { get; set; }

    [JsonPropertyName("loaded")]
    public bool Loaded { get; set; }
}

public sealed class ProTvTabStatus
{
    [JsonPropertyName("requirements")]
    public IReadOnlyList<ProTvTabRequirement> Requirements { get; set; } = Array.Empty<ProTvTabRequirement>();

    [JsonPropertyName("requireCompanionPlugins")]
    public bool RequireCompanionPlugins { get; set; } = true;

    [JsonPropertyName("frontpageTabEnabled")]
    public bool FrontpageTabEnabled { get; set; } = true;

    [JsonPropertyName("frontpageTabTitle")]
    public string FrontpageTabTitle { get; set; } = "PROTV";

    [JsonPropertyName("tabRegistered")]
    public bool TabRegistered { get; set; }

    [JsonPropertyName("channelCount")]
    public int ChannelCount { get; set; }

    // False when the server could not create/read <data>/protv/secret.key and the password or
    // VOD signing secret would have to be written in clear text. The dashboard shows a warning.
    [JsonPropertyName("secretsEncrypted")]
    public bool SecretsEncrypted { get; set; } = true;
}

// One programme, as the "On now" row shows it.
public sealed class ProTvTabProgramme
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("startsUtc")]
    public DateTime? StartsUtc { get; set; }

    [JsonPropertyName("endsUtc")]
    public DateTime? EndsUtc { get; set; }
}

// What is on one channel right now, and what follows it.
public sealed class ProTvTabNow
{
    [JsonPropertyName("channelId")]
    public int ChannelId { get; set; }

    [JsonPropertyName("now")]
    public ProTvTabProgramme? Now { get; set; }

    [JsonPropertyName("next")]
    public ProTvTabProgramme? Next { get; set; }
}

public sealed class ProTvTabNowList
{
    [JsonPropertyName("generatedUtc")]
    public DateTime GeneratedUtc { get; set; }

    [JsonPropertyName("channels")]
    public IReadOnlyList<ProTvTabNow> Channels { get; set; } = Array.Empty<ProTvTabNow>();
}

internal static class ProTvTabData
{
    // The saved channel list (kept fresh by the tuner / guide refresh) is used by default so the
    // tab is instant and does not depend on the PROTV service being reachable. refresh=true asks
    // PROTV again, the same way a guide refresh would.
    public static async Task<ProTvTabChannels> GetChannelsAsync(
        bool refresh,
        bool unlocked,
        CancellationToken cancellationToken)
    {
        var configuration = Plugin.Instance?.Configuration;
        IReadOnlyList<ProTvChannel>? channels = null;
        if (refresh)
        {
            channels = await TryFetchLiveAsync(configuration, cancellationToken);
        }

        channels ??= FromSnapshot(configuration);
        return Build(channels, Title(configuration), Language(configuration), unlocked);
    }

    public static ProTvTabStatus GetStatus()
    {
        var configuration = Plugin.Instance?.Configuration;
        return new ProTvTabStatus
        {
            Requirements = ProTvRequirements.Check().Requirements
                .Select(item => new ProTvTabRequirement
                {
                    Name = item.DisplayName,
                    Installed = item.Installed,
                    Loaded = item.Loaded
                })
                .ToList(),
            RequireCompanionPlugins = configuration?.RequireCompanionPlugins ?? true,
            FrontpageTabEnabled = configuration?.FrontpageTabEnabled ?? true,
            FrontpageTabTitle = Title(configuration),
            TabRegistered = ProTvCustomTabs.IsRegistered(),
            ChannelCount = configuration?.KnownChannels?.Length ?? 0,
            SecretsEncrypted = configuration is null
                || SecretsAreEncrypted(configuration)
        };
    }

    private static bool SecretsAreEncrypted(PluginConfiguration configuration)
    {
        // An envelope without its key is not readable, and a legacy value is not encrypted.
        // CanDecrypt loads the key if necessary, so this is accurate on a fresh process too.
        return ProTvSecretStore.CanDecrypt(configuration.Password)
            && ProTvSecretStore.CanDecrypt(configuration.VodStreamSecret);
    }

    // What is on each channel right now and what follows it, from the same PROTV guide the Live TV
    // listings come from: one call for every channel. The tab asks for this after it has drawn the
    // grid, so the grid never waits for the guide. When PROTV does not answer, the list is empty and
    // the tab simply leaves the row out.
    public static async Task<ProTvTabNowList> GetNowAsync(bool unlocked, CancellationToken cancellationToken)
    {
        IReadOnlyList<ProTvEpgChannel> epg;
        try
        {
            epg = await ProTvSharedClient.Get().GetEpgAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            epg = Array.Empty<ProTvEpgChannel>();
        }

        var now = DateTime.UtcNow;
        var served = FromSnapshot(Plugin.Instance?.Configuration)
            .Where(channel => unlocked || !ProTvParental.IsChannelLocked(channel.Id))
            .Select(channel => channel.Id)
            .ToHashSet();
        var items = new List<ProTvTabNow>(epg.Count);

        foreach (var channel in epg)
        {
            if (served.Count > 0 && !served.Contains(channel.ChannelId))
            {
                continue;
            }

            var programmes = (channel.Programs ?? Array.Empty<ProTvEpgItem>())
                .OrderBy(programme => programme.StartTimeUtc)
                .ToList();

            var current = programmes.FirstOrDefault(programme =>
                programme.StartTimeUtc <= now && programme.EndTimeUtc > now);
            var next = programmes.FirstOrDefault(programme => programme.StartTimeUtc > now);
            if (current is null && next is null)
            {
                continue;
            }

            items.Add(new ProTvTabNow
            {
                ChannelId = channel.ChannelId,
                Now = Programme(current),
                Next = Programme(next)
            });
        }

        return new ProTvTabNowList { GeneratedUtc = now, Channels = items };
    }

    private static ProTvTabProgramme? Programme(ProTvEpgItem? programme)
        => programme is null
            ? null
            : new ProTvTabProgramme
            {
                Title = programme.Title,
                StartsUtc = programme.StartTimeUtc,
                EndsUtc = programme.EndTimeUtc
            };

    private static string Title(PluginConfiguration? configuration)
        => string.IsNullOrWhiteSpace(configuration?.FrontpageTabTitle) ? "PROTV" : configuration!.FrontpageTabTitle.Trim();

    // "auto" / "en" / "ar": anything unrecognised means "follow the browser".
    private static string Language(PluginConfiguration? configuration)
    {
        var value = configuration?.UiLanguage?.Trim().ToLowerInvariant();
        return value is "en" or "ar" ? value : "auto";
    }

    // Rebuilds PROTV channels from the saved snapshot so the shared ordering / rename / hide /
    // logo logic (ProTvChannelOrdering) produces exactly what the tuner serves.
    private static List<ProTvChannel> FromSnapshot(PluginConfiguration? configuration)
        => ProTvChannelOrdering.FromSnapshot(configuration);

    private static ProTvTabChannels Build(
        IReadOnlyList<ProTvChannel> channels,
        string title,
        string language,
        bool unlocked)
    {
        var configuration = Plugin.Instance?.Configuration;
        var overrides = (configuration?.ChannelOverrides ?? Array.Empty<ProTvChannelOverride>())
            .GroupBy(item => item.ChannelId)
            .ToDictionary(group => group.Key, group => group.Last());
        var groups = (configuration?.ChannelGroups ?? Array.Empty<ProTvChannelGroup>())
            .Where(group => !string.IsNullOrWhiteSpace(group.Id) && !string.IsNullOrWhiteSpace(group.Name))
            .GroupBy(group => group.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .Select(group => new ProTvTabGroup { Id = group.Id.Trim(), Name = group.Name.Trim() })
            .ToList();

        var ordered = ProTvChannelOrdering.Sort(channels);
        var items = new List<ProTvTabChannel>(ordered.Count);
        for (var index = 0; index < ordered.Count; index++)
        {
            var channel = ordered[index];
            var item = ProTvLiveTvItems.Resolve(channel.Id);
            var health = ProTvChannelHealth.Get(channel.Id);
            overrides.TryGetValue(channel.Id, out var custom);
            items.Add(new ProTvTabChannel
            {
                Id = channel.Id,
                Number = (index + 1).ToString(),
                Name = ProTvChannelOrdering.DisplayName(channel),
                LogoUrl = ProTvChannelOrdering.LogoUrl(channel),
                ItemId = item.Id.ToString("N"),
                InLiveTv = item.InLiveTv,
                Health = ProTvChannelHealth.Describe(channel.Id),
                HealthMessage = health?.State == ProTvChannelState.Offline ? health.Message : null,
                Favorite = custom?.IsFavorite == true,
                GroupId = custom?.GroupId?.Trim() ?? string.Empty,
                Locked = !unlocked
                    && configuration?.ParentalEnabled == true
                    && custom?.ParentalLocked == true
            });
        }

        return new ProTvTabChannels
        {
            Title = title,
            Language = language,
            Total = items.Count,
            Groups = groups,
            Channels = items
        };
    }

    private static async Task<IReadOnlyList<ProTvChannel>?> TryFetchLiveAsync(
        PluginConfiguration? configuration,
        CancellationToken cancellationToken)
    {
        if (configuration is null
            || string.IsNullOrWhiteSpace(configuration.ApiBaseUrl)
            || string.IsNullOrWhiteSpace(configuration.Username)
            || string.IsNullOrEmpty(configuration.PasswordValue))
        {
            return null;
        }

        try
        {
            return await ProTvSharedClient.Get().GetChannelsAsync(cancellationToken);
        }
        catch (Exception)
        {
            // Fall back to the saved list instead of showing an error in the middle of the home page.
            return null;
        }
    }
}
