using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;

namespace PROTV.Plugin;

// Which PROTV catalogue a stream belongs to. Used by the plugin stream endpoint and the resolver.
public enum ProTvVodKind
{
    Movie,
    Episode
}

// A poster card in the PROTV tab: a movie, a series or an episode.
public sealed class ProTvTabVodItem
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    // movie | series | episode - tells the tab whether a tap plays or opens the season list.
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "movie";

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("posterUrl")]
    public string? PosterUrl { get; set; }

    [JsonPropertyName("plot")]
    public string? Plot { get; set; }

    [JsonPropertyName("rating")]
    public double Rating { get; set; }

    [JsonPropertyName("durationMinutes")]
    public int? DurationMinutes { get; set; }

    [JsonPropertyName("genres")]
    public IReadOnlyList<string> Genres { get; set; } = Array.Empty<string>();

    [JsonPropertyName("mediaCost")]
    public string? MediaCost { get; set; }

    [JsonPropertyName("seasonsCount")]
    public int SeasonsCount { get; set; }

    [JsonPropertyName("episodesCount")]
    public int EpisodesCount { get; set; }

    [JsonPropertyName("seasonNumber")]
    public int? SeasonNumber { get; set; }

    [JsonPropertyName("episodeNumber")]
    public int? EpisodeNumber { get; set; }

    // The Jellyfin item this PROTV title became. The tab plays it exactly like a live channel, so
    // VOD opens in the same player as everything else in Jellyfin.
    [JsonPropertyName("itemId")]
    public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("inLibrary")]
    public bool InLibrary { get; set; }

    // The plugin's own stream endpoint for this item (302 to the PROTV HLS link).
    [JsonPropertyName("streamUrl")]
    public string StreamUrl { get; set; } = string.Empty;
}

public sealed class ProTvTabVodCategory
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public sealed class ProTvTabVodPage
{
    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("pageCount")]
    public int PageCount { get; set; }

    [JsonPropertyName("items")]
    public IReadOnlyList<ProTvTabVodItem> Items { get; set; } = Array.Empty<ProTvTabVodItem>();

    [JsonPropertyName("categories")]
    public IReadOnlyList<ProTvTabVodCategory> Categories { get; set; } = Array.Empty<ProTvTabVodCategory>();

    [JsonPropertyName("librarySynced")]
    public bool LibrarySynced { get; set; }

    [JsonPropertyName("libraryCount")]
    public int LibraryCount { get; set; }
}

public sealed class ProTvTabVodSeason
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("posterUrl")]
    public string? PosterUrl { get; set; }

    [JsonPropertyName("episodes")]
    public IReadOnlyList<ProTvTabVodItem> Episodes { get; set; } = Array.Empty<ProTvTabVodItem>();
}

public sealed class ProTvTabVodSeriesDetail
{
    [JsonPropertyName("series")]
    public ProTvTabVodItem Series { get; set; } = new();

    [JsonPropertyName("seasons")]
    public IReadOnlyList<ProTvTabVodSeason> Seasons { get; set; } = Array.Empty<ProTvTabVodSeason>();
}

// What the dashboard and the tab report about the VOD side of the plugin.
public sealed class ProTvVodStatus
{
    [JsonPropertyName("moviesEnabled")]
    public bool MoviesEnabled { get; set; }

    [JsonPropertyName("seriesEnabled")]
    public bool SeriesEnabled { get; set; }

    [JsonPropertyName("libraryName")]
    public string LibraryName { get; set; } = ProTvVodPaths.LibraryName;

    [JsonPropertyName("libraryPath")]
    public string LibraryPath { get; set; } = string.Empty;

    [JsonPropertyName("libraryRegistered")]
    public bool LibraryRegistered { get; set; }

    // The series live in a second library of their own, in the layout Jellyfin expects for shows
    // (a "mixed" library made Jellyfin read the film folder as a single series).
    [JsonPropertyName("seriesLibraryName")]
    public string SeriesLibraryName { get; set; } = ProTvVodPaths.SeriesLibraryName;

    [JsonPropertyName("seriesLibraryPath")]
    public string SeriesLibraryPath { get; set; } = string.Empty;

    [JsonPropertyName("seriesLibraryRegistered")]
    public bool SeriesLibraryRegistered { get; set; }

    // The address the library's streams point back at (this server).
    [JsonPropertyName("streamBaseUrl")]
    public string? StreamBaseUrl { get; set; }

    [JsonPropertyName("movies")]
    public int Movies { get; set; }

    [JsonPropertyName("series")]
    public int Series { get; set; }

    [JsonPropertyName("episodes")]
    public int Episodes { get; set; }

    // How many subtitle files the sync has downloaded.
    [JsonPropertyName("subtitles")]
    public int Subtitles { get; set; }

    [JsonPropertyName("downloadSubtitles")]
    public bool DownloadSubtitles { get; set; }

    // Whether the sync downloads posters (and so whether the leftovers it reports make sense).
    [JsonPropertyName("downloadPosters")]
    public bool DownloadPosters { get; set; }

    // running | completed | cancelled | failed. Empty for a state written by an older build.
    [JsonPropertyName("lastRunStatus")]
    public string LastRunStatus { get; set; } = string.Empty;

    // Everything the sync has downloaded (posters, subtitles, the library icons) and the limit an
    // admin set for it.
    [JsonPropertyName("assetBytes")]
    public long AssetBytes { get; set; }

    [JsonPropertyName("assetBudgetBytes")]
    public long AssetBudgetBytes { get; set; }

    [JsonPropertyName("budgetReached")]
    public bool BudgetReached { get; set; }

    [JsonPropertyName("lastSyncUtc")]
    public string? LastSyncUtc { get; set; }

    [JsonPropertyName("running")]
    public bool Running { get; set; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }
}

// The VOD side of the PROTV home page tab. It browses the PROTV catalogue directly (the API is
// paged, so nothing large is ever held in memory) and resolves a stream only when one is asked for.
internal static class ProTvVodData
{
    // Resolved links are cached, so re-opening a film or an episode costs no API call at all.
    // PROTV's VOD paths are static files, so the entries can live for a long time.
    private static readonly ConcurrentDictionary<string, (string Url, DateTime ExpiresUtc)> StreamCache = new();
    private static readonly TimeSpan StreamCacheLifetime = TimeSpan.FromHours(12);

    private static readonly ConcurrentDictionary<string, (IReadOnlyList<ProTvTabVodCategory> Items, DateTime ExpiresUtc)> CategoryCache = new();
    private static readonly TimeSpan CategoryCacheLifetime = TimeSpan.FromMinutes(30);

    // The plugin's stream endpoint for an item, as the .strm files point at (and the tab offers as
    // a plain HLS link). The name is what a client sees when it direct-plays.
    public static string StreamPath(ProTvVodKind kind, int id)
    {
        var token = StreamToken(kind, id);
        var idText = id.ToString(CultureInfo.InvariantCulture);
        return token.Length == 0
            ? $"/PROTV/Vod/Stream/{KindName(kind)}/{idText}.m3u8"
            : $"/PROTV/Vod/Stream/{KindName(kind)}/{idText}/{token}.m3u8";
    }

    // The stream endpoint has to work without a Jellyfin login (Jellyfin's own ffmpeg and
    // direct-playing apps fetch it), and the ids are small sequential numbers. Every link carries a
    // signature only this server can produce, so a link cannot be guessed for another title.
    private static readonly object SecretGate = new();
    private static string? _secretSource;
    private static byte[]? _secretBytes;

    private static byte[]? StreamSecret()
    {
        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null)
        {
            return null;
        }

        lock (SecretGate)
        {
            var secret = configuration.VodStreamSecretValue;
            if (string.IsNullOrWhiteSpace(secret))
            {
                secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                configuration.VodStreamSecret = secret;
                try
                {
                    Plugin.Instance?.PersistConfiguration();
                }
                catch
                {
                    // Still usable for this run; the next run would just create another secret.
                }
            }

            if (!string.Equals(_secretSource, secret, StringComparison.Ordinal))
            {
                _secretSource = secret;
                _secretBytes = Encoding.UTF8.GetBytes(secret);
            }

            return _secretBytes;
        }
    }

    public static string StreamToken(ProTvVodKind kind, int id)
    {
        var secret = StreamSecret();
        if (secret is null)
        {
            return string.Empty;
        }

        var data = Encoding.UTF8.GetBytes($"{KindName(kind)}:{id.ToString(CultureInfo.InvariantCulture)}");
        return Convert.ToHexString(HMACSHA256.HashData(secret, data), 0, 16).ToLowerInvariant();
    }

    public static bool IsValidStreamToken(ProTvVodKind kind, int id, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var expected = StreamToken(kind, id);
        return expected.Length > 0
            && CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(token.Trim().ToLowerInvariant()));
    }

    public static string KindName(ProTvVodKind kind) => kind == ProTvVodKind.Movie ? "movie" : "episode";

    public static ProTvVodKind? ParseKind(string? kind)
        => kind?.Trim().ToLowerInvariant() switch
        {
            "movie" or "movies" => ProTvVodKind.Movie,
            "episode" or "episodes" => ProTvVodKind.Episode,
            _ => null
        };

    // Resolves the playable URL for a PROTV id, preferring the cached answer.
    private static readonly ConcurrentDictionary<string, Lazy<Task<string?>>> StreamInFlight = new();

    public static async Task<string?> ResolveStreamUrlAsync(ProTvVodKind kind, int id, CancellationToken cancellationToken)
    {
        var key = $"{KindName(kind)}:{id.ToString(CultureInfo.InvariantCulture)}";
        if (StreamCache.TryGetValue(key, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
        {
            return cached.Url;
        }

        // Jellyfin probes a stream and then plays it, often within milliseconds of each other:
        // both share one lookup instead of asking PROTV twice.
        var lookup = StreamInFlight.GetOrAdd(key, _ => new Lazy<Task<string?>>(() => ResolveStreamUrlCoreAsync(kind, id, key)));
        try
        {
            return await lookup.Value.WaitAsync(cancellationToken);
        }
        finally
        {
            if (lookup.Value.IsCompleted)
            {
                StreamInFlight.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(key, lookup));
            }
        }
    }

    private static async Task<string?> ResolveStreamUrlCoreAsync(ProTvVodKind kind, int id, string key)
    {
        var client = CreateClient(Plugin.Instance?.Configuration);
        if (client is null)
        {
            return null;
        }

        // Shared by every caller waiting on this item, so one caller leaving must not cancel it.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string? url;
        try
        {
            url = kind == ProTvVodKind.Movie
                ? ProTvApiClient.SelectStreamUrl((await client.GetMovieDetailAsync(id, timeout.Token))?.MovieLinks)
                : ProTvApiClient.SelectStreamUrl((await client.GetEpisodeDetailAsync(id, timeout.Token))?.EpisodeLinks);
        }
        catch (Exception)
        {
            // An unknown id, PROTV down, a broken answer: nothing here can be played, so let the
            // caller answer "not found" instead of a server error.
            return null;
        }

        // A miss is not cached: PROTV may simply be unreachable right now.
        if (!string.IsNullOrWhiteSpace(url))
        {
            if (StreamCache.Count > 1000)
            {
                PurgeExpired(StreamCache, entry => entry.ExpiresUtc);
            }

            StreamCache[key] = (url!, DateTime.UtcNow + StreamCacheLifetime);
        }

        return url;
    }

    // Entries are only ever replaced, never removed, so a long-running server would keep every
    // title it ever played. Dropping the expired ones once the cache is large keeps it bounded.
    private static void PurgeExpired<TValue>(ConcurrentDictionary<string, TValue> cache, Func<TValue, DateTime> expires)
    {
        var now = DateTime.UtcNow;
        foreach (var pair in cache)
        {
            if (expires(pair.Value) <= now)
            {
                cache.TryRemove(pair.Key, out _);
            }
        }
    }

    // Browse pages and series details are the same for everybody, and the tab asks again whenever
    // it is reopened or scrolled back, so they are kept for a short while.
    private static readonly TimeSpan BrowseCacheLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DetailCacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, (object Value, DateTime ExpiresUtc)> BrowseCache = new();

    private static async Task<T?> CachedAsync<T>(string key, TimeSpan lifetime, Func<Task<T?>> load)
        where T : class
    {
        if (BrowseCache.TryGetValue(key, out var hit) && hit.ExpiresUtc > DateTime.UtcNow && hit.Value is T cached)
        {
            return cached;
        }

        var result = await load();
        if (result is not null)
        {
            if (BrowseCache.Count > 300)
            {
                PurgeExpired(BrowseCache, entry => entry.ExpiresUtc);
            }

            BrowseCache[key] = (result, DateTime.UtcNow + lifetime);
        }

        return result;
    }

    public static async Task<ProTvTabVodPage> GetMoviesAsync(
        int page,
        int pageSize,
        string? search,
        int? categoryId,
        CancellationToken cancellationToken)
    {
        var key = string.Join('|', "movies", Math.Max(1, page), Math.Clamp(pageSize, 1, 100), search?.Trim().ToLowerInvariant() ?? string.Empty, categoryId ?? 0);
        return (await CachedAsync<ProTvTabVodPage>(
            key,
            BrowseCacheLifetime,
            () => GetMoviesUncachedAsync(page, pageSize, search, categoryId, cancellationToken)!))!;
    }

    public static async Task<ProTvTabVodPage> GetSeriesAsync(
        int page,
        int pageSize,
        string? search,
        int? categoryId,
        CancellationToken cancellationToken)
    {
        var key = string.Join('|', "series", Math.Max(1, page), Math.Clamp(pageSize, 1, 100), search?.Trim().ToLowerInvariant() ?? string.Empty, categoryId ?? 0);
        return (await CachedAsync<ProTvTabVodPage>(
            key,
            BrowseCacheLifetime,
            () => GetSeriesUncachedAsync(page, pageSize, search, categoryId, cancellationToken)!))!;
    }

    public static Task<ProTvTabVodSeriesDetail?> GetSeriesDetailAsync(int seriesId, CancellationToken cancellationToken)
        => CachedAsync<ProTvTabVodSeriesDetail>(
            "seriesdetail|" + seriesId.ToString(CultureInfo.InvariantCulture),
            DetailCacheLifetime,
            () => GetSeriesDetailUncachedAsync(seriesId, cancellationToken));

    private static async Task<ProTvTabVodPage> GetMoviesUncachedAsync(
        int page,
        int pageSize,
        string? search,
        int? categoryId,
        CancellationToken cancellationToken)
    {
        var client = CreateClient(Plugin.Instance?.Configuration)
            ?? throw new InvalidOperationException("PROTV is not configured.");

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = $"api/Movies?Page={page}&Count={pageSize}&Search.Status=Active&Sort=ActivateDate&Order=Desc";
        if (!string.IsNullOrWhiteSpace(search))
        {
            query += "&Search.Name=" + Uri.EscapeDataString(search.Trim());
        }

        if (categoryId is > 0)
        {
            query += "&Search.CategoryId=" + categoryId.Value.ToString(CultureInfo.InvariantCulture);
        }

        var result = await client.GetAuthorizedAsync<ProTvListResponse<ProTvMovie>>(query, cancellationToken);
        var data = result?.Data;
        var items = (data?.Result ?? Array.Empty<ProTvMovie>())
            .Select(movie => MapMovie(client, movie))
            .ToList();

        return new ProTvTabVodPage
        {
            Total = data?.Total ?? 0,
            Page = data?.CurrentPage ?? page,
            PageSize = data?.PageSize ?? pageSize,
            PageCount = data?.PageCount ?? 0,
            Items = items,
            Categories = await GetCategoriesAsync(client, "Movie", cancellationToken)
        };
    }

    private static async Task<ProTvTabVodPage> GetSeriesUncachedAsync(
        int page,
        int pageSize,
        string? search,
        int? categoryId,
        CancellationToken cancellationToken)
    {
        var client = CreateClient(Plugin.Instance?.Configuration)
            ?? throw new InvalidOperationException("PROTV is not configured.");

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = $"api/Series?Page={page}&Count={pageSize}&Search.Status=Active&Sort=ActivateDate&Order=Desc";
        if (!string.IsNullOrWhiteSpace(search))
        {
            query += "&Search.Name=" + Uri.EscapeDataString(search.Trim());
        }

        if (categoryId is > 0)
        {
            query += "&Search.CategoryId=" + categoryId.Value.ToString(CultureInfo.InvariantCulture);
        }

        var result = await client.GetAuthorizedAsync<ProTvListResponse<ProTvSeries>>(query, cancellationToken);
        var data = result?.Data;
        var items = (data?.Result ?? Array.Empty<ProTvSeries>())
            .Select(series => MapSeries(client, series))
            .ToList();

        return new ProTvTabVodPage
        {
            Total = data?.Total ?? 0,
            Page = data?.CurrentPage ?? page,
            PageSize = data?.PageSize ?? pageSize,
            PageCount = data?.PageCount ?? 0,
            Items = items,
            Categories = await GetCategoriesAsync(client, "Series", cancellationToken)
        };
    }

    // A series and all of its seasons with their episodes, ready for the tab to draw.
    private static async Task<ProTvTabVodSeriesDetail?> GetSeriesDetailUncachedAsync(int seriesId, CancellationToken cancellationToken)
    {
        var client = CreateClient(Plugin.Instance?.Configuration);
        if (client is null)
        {
            return null;
        }

        var series = await client.GetSeriesDetailAsync(seriesId, cancellationToken);
        if (series is null)
        {
            return null;
        }

        var seasons = await client.GetSeasonsForSeriesAsync(seriesId, cancellationToken);

        // One request per season, all at once instead of one after another (the order is kept).
        var result = (await Task.WhenAll(seasons.Select(async season =>
        {
            var seasonIndex = season.Index > 0 ? season.Index : 1;
            var episodes = await client.GetEpisodesForSeasonAsync(season.Id, cancellationToken);
            return new ProTvTabVodSeason
            {
                Id = season.Id,
                Index = seasonIndex,
                Title = string.IsNullOrWhiteSpace(season.Title) ? $"Season {seasonIndex}" : season.Title,
                PosterUrl = client.NormalizeContentImageUrl(
                    !string.IsNullOrWhiteSpace(season.Poster) ? season.Poster : series.Poster),
                Episodes = episodes
                    .OrderBy(episode => episode.Index)
                    .Select(episode => MapEpisode(client, series, seasonIndex, episode))
                    .ToList()
            };
        }))).ToList();

        return new ProTvTabVodSeriesDetail
        {
            Series = MapSeries(client, series),
            Seasons = result
        };
    }

    public static async Task<IReadOnlyList<ProTvTabVodCategory>> GetCategoriesAsync(
        bool series,
        CancellationToken cancellationToken)
    {
        var client = CreateClient(Plugin.Instance?.Configuration);
        return client is null
            ? Array.Empty<ProTvTabVodCategory>()
            : await GetCategoriesAsync(client, series ? "Series" : "Movie", cancellationToken);
    }

    public static ProTvVodStatus GetStatus(bool running = false, string? lastError = null)
    {
        var configuration = Plugin.Instance?.Configuration;
        var state = ProTvVodLibrary.State;
        var budget = ProTvVodLibrary.AssetBudgetBytes(configuration);
        return new ProTvVodStatus
        {
            MoviesEnabled = configuration?.VodMoviesEnabled ?? true,
            SeriesEnabled = configuration?.VodSeriesEnabled ?? true,
            LibraryName = ProTvVodPaths.LibraryName,
            LibraryPath = ProTvVodLibrary.LibraryRoot,
            LibraryRegistered = ProTvVodLibrary.IsRegistered,
            SeriesLibraryName = ProTvVodPaths.SeriesLibraryName,
            SeriesLibraryPath = ProTvVodLibrary.SeriesLibraryRoot,
            SeriesLibraryRegistered = ProTvVodLibrary.IsSeriesLibraryRegistered,
            StreamBaseUrl = ProTvVodLibrary.ActiveStreamBaseUrl,
            // While a run is in flight the dashboard follows its counters; otherwise it shows the
            // counts the last complete run stored, which the run in flight does not touch.
            Movies = running ? state.RunMovies : state.Movies,
            Series = running ? state.RunSeries : state.Series,
            Episodes = running ? state.RunEpisodes : state.Episodes,
            Subtitles = state.Subtitles,
            DownloadSubtitles = configuration?.VodDownloadSubtitles ?? true,
            DownloadPosters = configuration?.VodDownloadPosters ?? true,
            AssetBytes = state.AssetBytes,
            AssetBudgetBytes = budget,
            // Worked out from what is on the disk and the limit in force now, so raising the limit
            // in the settings clears it without waiting for another sync.
            BudgetReached = budget > 0 && state.AssetBytes >= budget,
            LastSyncUtc = state.LastSyncUtc?.ToString("o", CultureInfo.InvariantCulture),
            Running = running,
            LastError = lastError ?? state.LastError,
            LastRunStatus = state.LastRunStatus ?? string.Empty
        };
    }

    private static async Task<IReadOnlyList<ProTvTabVodCategory>> GetCategoriesAsync(
        ProTvApiClient client,
        string categoryType,
        CancellationToken cancellationToken)
    {
        if (CategoryCache.TryGetValue(categoryType, out var cached) && cached.ExpiresUtc > DateTime.UtcNow)
        {
            return cached.Items;
        }

        try
        {
            var categories = await client.GetCategoriesAsync(categoryType, cancellationToken);
            var items = categories
                .Select(category => new ProTvTabVodCategory
                {
                    Id = category.Id,
                    Name = !string.IsNullOrWhiteSpace(category.TitleEn) ? category.TitleEn! : category.TitleAr ?? string.Empty
                })
                .Where(category => category.Name.Length > 0)
                .ToList();
            if (items.Count > 0)
            {
                CategoryCache[categoryType] = (items, DateTime.UtcNow + CategoryCacheLifetime);
            }

            return items;
        }
        catch (Exception)
        {
            // Filters are a nicety; a hiccup there must not stop the tab from listing titles.
            return CategoryCache.TryGetValue(categoryType, out var stale) ? stale.Items : Array.Empty<ProTvTabVodCategory>();
        }
    }

    private static ProTvTabVodItem MapMovie(ProTvApiClient client, ProTvMovie movie)
    {
        var item = ProTvVodItems.ResolveMovie(movie);
        return new ProTvTabVodItem
        {
            Id = movie.Id,
            Kind = "movie",
            Title = string.IsNullOrWhiteSpace(movie.Title) ? $"PROTV {movie.Id}" : movie.Title!,
            Year = movie.ReleaseYear,
            PosterUrl = client.NormalizeContentImageUrl(movie.Poster),
            Plot = FirstNonEmpty(movie.PlotEn, movie.PlotAr),
            Rating = movie.ImdbScore > 0 ? movie.ImdbScore : movie.ProtvScore,
            DurationMinutes = ParseMinutes(movie.Duration, movie.DurationTime),
            Genres = MapGenres(movie.Categories),
            MediaCost = movie.MediaCost,
            ItemId = item.Id.ToString("N"),
            InLibrary = item.CanPlay,
            StreamUrl = StreamPath(ProTvVodKind.Movie, movie.Id)
        };
    }

    private static ProTvTabVodItem MapSeries(ProTvApiClient client, ProTvSeries series)
    {
        var dataPath = ProTvVodItems.DataPath;
        var inLibrary = false;
        if (dataPath is not null)
        {
            // A series has no file of its own; the tab only needs to know whether the sync has
            // written its folder, and taps open the season list either way.
            inLibrary = Directory.Exists(ProTvVodPaths.SeriesRootDirectory(dataPath, series));
        }

        return new ProTvTabVodItem
        {
            Id = series.Id,
            Kind = "series",
            Title = string.IsNullOrWhiteSpace(series.Title) ? $"PROTV {series.Id}" : series.Title!,
            PosterUrl = client.NormalizeContentImageUrl(series.Poster),
            Plot = FirstNonEmpty(series.DescriptionEn, series.DescriptionAr),
            Rating = series.ImdbScore,
            SeasonsCount = series.SeasonsCount,
            Genres = MapGenres(series.Categories),
            InLibrary = inLibrary
        };
    }

    private static ProTvTabVodItem MapEpisode(ProTvApiClient client, ProTvSeries series, int seasonIndex, ProTvEpisode episode)
    {
        var item = ProTvVodItems.ResolveEpisode(series, seasonIndex, episode);
        var episodeNumber = episode.Index > 0 ? episode.Index : 1;
        return new ProTvTabVodItem
        {
            Id = episode.Id,
            Kind = "episode",
            // PROTV often has no name for an episode and labels it with the series name and its
            // numbers, so the tab shows the same name the library does.
            Title = ProTvVodPaths.EpisodeTitle(series, seasonIndex, episode),
            Year = episode.ReleaseYear,
            PosterUrl = client.NormalizeContentImageUrl(episode.Poster),
            Plot = FirstNonEmpty(episode.DescrptionEn, episode.DescrptionAr),
            Rating = episode.ImdbScore,
            DurationMinutes = ParseMinutes(episode.Duration, episode.DurationTime),
            MediaCost = episode.MediaCost,
            SeasonNumber = seasonIndex,
            EpisodeNumber = episodeNumber,
            ItemId = item.Id.ToString("N"),
            InLibrary = item.CanPlay,
            StreamUrl = StreamPath(ProTvVodKind.Episode, episode.Id)
        };
    }

    private static IReadOnlyList<string> MapGenres(IReadOnlyList<ProTvVodCategory>? categories)
        => categories is null
            ? Array.Empty<string>()
            : categories
                .Select(category => category.TitleEn ?? category.TitleAr)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static string? FirstNonEmpty(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first) ? first : (string.IsNullOrWhiteSpace(second) ? null : second);

    // PROTV sends a movie length as minutes and an episode length as hh:mm:ss (and each of them in
    // a second field too), so both shapes are accepted.
    internal static int? ParseMinutes(string? duration, string? durationTime)
    {
        if (TryParseMinutes(duration, out var minutes))
        {
            return minutes;
        }

        return TryParseMinutes(durationTime, out minutes) ? minutes : null;
    }

    private static bool TryParseMinutes(string? value, out int minutes)
    {
        minutes = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.Contains(':'))
        {
            var parts = text.Split(':');
            if (parts.Length is 2 or 3
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mins))
            {
                minutes = (hours * 60) + mins;
                return minutes > 0;
            }

            return false;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var numeric))
        {
            minutes = (int)Math.Round(numeric);
            return minutes > 0;
        }

        return false;
    }

    internal static ProTvApiClient? CreateClient(PluginConfiguration? configuration)
    {
        if (configuration is null
            || string.IsNullOrWhiteSpace(configuration.ApiBaseUrl)
            || string.IsNullOrWhiteSpace(configuration.Username)
            || string.IsNullOrEmpty(configuration.PasswordValue))
        {
            return null;
        }

        return ProTvSharedClient.Get();
    }
}
