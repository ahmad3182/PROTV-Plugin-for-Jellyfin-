using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;

namespace PROTV.Plugin;

// Where a PROTV channel lives inside Jellyfin's Live TV.
public readonly struct ProTvChannelItem
{
    public ProTvChannelItem(Guid id, bool inLiveTv)
    {
        Id = id;
        InLiveTv = inLiveTv;
    }

    // The id of the Jellyfin Live TV channel item (never empty: the id Jellyfin *will* use is
    // derived from the channel id, so it is known before the guide refresh stores the item).
    public Guid Id { get; }

    // True when Jellyfin already has that channel item, i.e. playing the channel can work now.
    public bool InLiveTv { get; }
}

// Resolves the Jellyfin Live TV channel item for a PROTV channel, so the PROTV home page tab can
// start playback of the real channel. Any Jellyfin app (web, Android TV, mobile) then plays that
// same item through the normal Live TV path: the tuner, the guide and the transcode/HLS pipeline.
internal static class ProTvLiveTvItems
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(15);
    private static readonly object Gate = new();
    private static ILibraryManager? _libraryManager;
    private static Dictionary<string, Guid>? _byExternalId;
    private static DateTime _cachedAtUtc = DateTime.MinValue;

    // Jellyfin builds the plugin's hosted service with its own container, which is the reliable
    // place to get hold of the library manager (the plugin cannot take it in its constructor).
    public static void Attach(ILibraryManager libraryManager)
    {
        lock (Gate)
        {
            _libraryManager = libraryManager;
            _byExternalId = null;
            _cachedAtUtc = DateTime.MinValue;
        }
    }

    public static ProTvChannelItem Resolve(int channelId)
    {
        var key = channelId.ToString(CultureInfo.InvariantCulture);
        var channels = Snapshot();
        if (channels is not null)
        {
            return channels.TryGetValue(key, out var existing)
                ? new ProTvChannelItem(existing, true)
                : new ProTvChannelItem(DeterministicId(key), false);
        }

        // No library manager (yet): hand out the id Jellyfin derives from the channel id. It is
        // what the guide refresh will use, so playback works as soon as the channel is listed.
        return new ProTvChannelItem(DeterministicId(key), true);
    }

    // The id Jellyfin gives a Live TV channel item:
    //   Jellyfin.LiveTv.LiveTvDtoService.GetInternalChannelId(serviceName, channelId)
    //     = LibraryManager.GetNewItemId((serviceName + channelId + "4").ToLowerInvariant(), typeof(LiveTvChannel))
    //     = Guid(MD5(UTF-16LE("MediaBrowser.Controller.LiveTv.LiveTvChannel" + key)))
    // where serviceName is "Emby" (the service that fronts every tuner host).
    private static Guid DeterministicId(string channelId)
    {
        var key = typeof(LiveTvChannel).FullName + "emby" + channelId + "4";
        return new Guid(MD5.HashData(Encoding.Unicode.GetBytes(key)));
    }

    // Every Live TV channel Jellyfin knows, keyed by the external (PROTV) channel id. Cached
    // briefly because the tab asks per render.
    private static Dictionary<string, Guid>? Snapshot()
    {
        ILibraryManager? manager;
        lock (Gate)
        {
            manager = _libraryManager;
            if (manager is null)
            {
                return null;
            }

            if (_byExternalId is not null && DateTime.UtcNow - _cachedAtUtc < CacheLifetime)
            {
                return _byExternalId;
            }
        }

        // The library query runs outside the lock, so other requests are not held up behind it.
        var channels = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var item in manager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.LiveTvChannel },
                Recursive = true,
                DtoOptions = new DtoOptions(false)
            }))
            {
                if (!string.IsNullOrWhiteSpace(item.ExternalId))
                {
                    channels[item.ExternalId] = item.Id;
                }
            }
        }
        catch
        {
            // Keep the previous view instead of failing the channel list.
            lock (Gate)
            {
                return _byExternalId;
            }
        }

        lock (Gate)
        {
            _byExternalId = channels;
            _cachedAtUtc = DateTime.UtcNow;
            return _byExternalId;
        }
    }
}
