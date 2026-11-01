using MediaBrowser.Controller.Library;

namespace PROTV.Plugin;

// Where a PROTV VOD item lives inside Jellyfin, once the VOD sync has written it into the plugin's
// own library. The home page tab plays the real Jellyfin item, so it needs its id.
public readonly struct ProTvVodItem
{
    public ProTvVodItem(Guid id, bool inLibrary)
    {
        Id = id;
        InLibrary = inLibrary;
    }

    // The Jellyfin item id. Empty while the VOD library has not been synced.
    public Guid Id { get; }

    // True when Jellyfin actually has the item, i.e. playing it can work now.
    public bool InLibrary { get; }

    public bool CanPlay => InLibrary && !Id.Equals(Guid.Empty);
}

// Looks the Jellyfin item up by the path the sync wrote it to. Ask Jellyfin rather than recompute
// the id from the path: a mixed library can resolve the same ".strm" as a Movie, an Episode or a
// plain Video, and every one of those has a different id.
internal static class ProTvVodItems
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(15);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ProTvVodItem> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static ILibraryManager? _libraryManager;
    private static string? _dataPath;
    private static DateTime _cachedAtUtc = DateTime.MinValue;

    // Jellyfin builds the plugin's hosted service with its own container, which is the reliable
    // place to get hold of the library manager (the plugin cannot take it in its constructor).
    public static void Attach(ILibraryManager libraryManager, string dataPath)
    {
        lock (Gate)
        {
            _libraryManager = libraryManager;
            _dataPath = dataPath;
            Cache.Clear();
            _cachedAtUtc = DateTime.MinValue;
        }
    }

    public static string? DataPath
    {
        get
        {
            lock (Gate)
            {
                return _dataPath;
            }
        }
    }

    public static ProTvVodItem ResolveMovie(ProTvMovie movie)
    {
        var dataPath = DataPath;
        return dataPath is null ? default : Resolve(ProTvVodPaths.MovieStrmFile(dataPath, movie));
    }

    public static ProTvVodItem ResolveEpisode(ProTvSeries series, int seasonIndex, ProTvEpisode episode)
    {
        var dataPath = DataPath;
        return dataPath is null ? default : Resolve(ProTvVodPaths.EpisodeStrmFile(dataPath, series, seasonIndex, episode));
    }

    private static ProTvVodItem Resolve(string path)
    {
        lock (Gate)
        {
            var manager = _libraryManager;
            if (manager is null)
            {
                return default;
            }

            if (DateTime.UtcNow - _cachedAtUtc >= CacheLifetime)
            {
                Cache.Clear();
                _cachedAtUtc = DateTime.UtcNow;
            }

            if (Cache.TryGetValue(path, out var cached))
            {
                return cached;
            }

            ProTvVodItem item;
            try
            {
                var found = manager.FindByPath(path, false);
                item = found is null ? new ProTvVodItem(Guid.Empty, false) : new ProTvVodItem(found.Id, true);
            }
            catch
            {
                // Never fail the tab because the library lookup had a bad day.
                item = new ProTvVodItem(Guid.Empty, false);
            }

            Cache[path] = item;
            return item;
        }
    }
}
