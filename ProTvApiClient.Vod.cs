namespace PROTV.Plugin;

// The VOD half of the PROTV API client: Movies / Series / Seasons / Episodes.
//
// It lives in its own file (the class is partial) because the live TV half is already long, but it
// shares the same HttpClient, bearer token, retry and caching. Every method here mirrors a call the
// PROTV desktop app makes, so the service sees the same shapes it already answers.
public sealed partial class ProTvApiClient
{
    // PROTV caps a page; 250 keeps a full catalogue sync to a few hundred requests instead of tens
    // of thousands.
    public const int VodPageSize = 250;

    private const string VodStatusFilter = "Search.Status=Active";

    public async Task<ProTvDataList<ProTvMovie>?> GetMoviesPageAsync(int page, int count, CancellationToken cancellationToken)
    {
        var response = await SendAsync<ProTvListResponse<ProTvMovie>>(
            HttpMethod.Get,
            $"api/Movies?Page={page}&Count={count}&{VodStatusFilter}&Sort=Id&Order=Asc",
            addDeviceHeaders: true,
            acceptLanguage: null,
            cancellationToken);
        return response.Data;
    }

    public async Task<ProTvMovie?> GetMovieDetailAsync(int movieId, CancellationToken cancellationToken)
    {
        var response = await SendAsync<ProTvResponse<ProTvMovie>>(
            HttpMethod.Get,
            $"api/Movies/{movieId}",
            addDeviceHeaders: true,
            acceptLanguage: "en-US",
            cancellationToken);
        return response.Data;
    }

    public async Task<ProTvDataList<ProTvSeries>?> GetSeriesPageAsync(int page, int count, CancellationToken cancellationToken)
    {
        var response = await SendAsync<ProTvListResponse<ProTvSeries>>(
            HttpMethod.Get,
            $"api/Series?Page={page}&Count={count}&{VodStatusFilter}&Sort=Id&Order=Asc",
            addDeviceHeaders: true,
            acceptLanguage: null,
            cancellationToken);
        return response.Data;
    }

    public async Task<ProTvSeries?> GetSeriesDetailAsync(int seriesId, CancellationToken cancellationToken)
    {
        var response = await SendAsync<ProTvResponse<ProTvSeries>>(
            HttpMethod.Get,
            $"api/Series/{seriesId}",
            addDeviceHeaders: true,
            acceptLanguage: "en-US",
            cancellationToken);
        return response.Data;
    }

    public async Task<IReadOnlyList<ProTvSeason>> GetSeasonsForSeriesAsync(int seriesId, CancellationToken cancellationToken)
    {
        var response = await SendAsync<ProTvListResponse<ProTvSeason>>(
            HttpMethod.Get,
            $"api/Seasons?Page=1&Count=-1&Sort=Index&Order=Asc&Search.SeriesId={seriesId}",
            addDeviceHeaders: true,
            acceptLanguage: null,
            cancellationToken);
        return response.Data?.Result ?? Array.Empty<ProTvSeason>();
    }

    public async Task<IReadOnlyList<ProTvEpisode>> GetEpisodesForSeasonAsync(int seasonId, CancellationToken cancellationToken)
    {
        var response = await SendAsync<ProTvListResponse<ProTvEpisode>>(
            HttpMethod.Get,
            $"api/Episodes?Page=1&Count=-1&Sort=Index&Order=Asc&Search.SeasonId={seasonId}",
            addDeviceHeaders: true,
            acceptLanguage: null,
            cancellationToken);
        return response.Data?.Result ?? Array.Empty<ProTvEpisode>();
    }

    public async Task<ProTvEpisode?> GetEpisodeDetailAsync(int episodeId, CancellationToken cancellationToken)
    {
        var response = await SendAsync<ProTvResponse<ProTvEpisode>>(
            HttpMethod.Get,
            $"api/Episodes/{episodeId}",
            addDeviceHeaders: true,
            acceptLanguage: "en-US",
            cancellationToken);
        return response.Data;
    }

    public async Task<IReadOnlyList<ProTvVodCategory>> GetCategoriesAsync(string categoryType, CancellationToken cancellationToken)
    {
        var response = await SendAsync<ProTvListResponse<ProTvVodCategory>>(
            HttpMethod.Get,
            $"api/Meta/Categories?Page=1&Count=-1&Search.CategoryType={categoryType}&{VodStatusFilter}",
            addDeviceHeaders: true,
            acceptLanguage: null,
            cancellationToken);
        return response.Data?.Result ?? Array.Empty<ProTvVodCategory>();
    }

    // A paged VOD list with whatever filters the caller assembled. Kept generic so the VOD
    // catalogue can grow without another method per endpoint.
    internal Task<T> GetAuthorizedAsync<T>(string relativePath, CancellationToken cancellationToken)
        => SendAsync<T>(HttpMethod.Get, relativePath, addDeviceHeaders: true, acceptLanguage: null, cancellationToken);

    // Makes a stored poster/thumbnail path absolute against the PROTV content host.
    public string? NormalizeContentImageUrl(string? posterPath) => NormalizeImageUrl(posterPath);

    // Same for any other file PROTV stores on its content host (subtitles, for example).
    public string? NormalizeContentUrl(string? contentPath) => NormalizeImageUrl(contentPath);

    // Picks the link a client should play: the HLS link if there is one (Jellyfin handles HLS over
    // HTTP natively), otherwise the RTSP link, in both cases preferring the best quality and
    // ignoring links that do not decrypt to a usable URL.
    public static string? SelectStreamUrl(IReadOnlyList<ProTvVodLink>? links)
    {
        if (links is null || links.Count == 0)
        {
            return null;
        }

        var ordered = links.OrderByDescending(link => QualityRank(link.Quality)).ToList();
        foreach (var link in ordered)
        {
            var hls = Usable(ProTvCrypto.TryDecrypt(link.Path));
            if (hls is not null)
            {
                return hls;
            }
        }

        foreach (var link in ordered)
        {
            var rtsp = Usable(ProTvCrypto.TryDecrypt(link.PathRTSP));
            if (rtsp is not null)
            {
                return rtsp;
            }
        }

        return null;
    }

    private static int QualityRank(string? quality) => quality?.Trim().ToUpperInvariant() switch
    {
        "FHD" => 4,
        "HD" => 3,
        "SD" => 2,
        "MD" => 1,
        _ => 0
    };

    private static string? Usable(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "rtsp" or "rtmp" or "rtp" or "udp"
            ? url
            : null;
    }
}
