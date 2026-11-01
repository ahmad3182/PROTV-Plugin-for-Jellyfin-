using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace PROTV.Plugin;

// What the VOD sync has produced so far. Kept in memory and written next to the libraries so the
// dashboard can report progress and the counts survive a restart.
public sealed class ProTvVodSyncState
{
    public int Movies { get; set; }

    public int Series { get; set; }

    public int Episodes { get; set; }

    // How many subtitle files the sync has downloaded.
    public int Subtitles { get; set; }

    // Everything the sync has downloaded (posters, subtitles, the library icons), so it can stop
    // before the disk budget an admin set is used up.
    public long AssetBytes { get; set; }

    public bool BudgetReached { get; set; }

    // Layout versions. When one changes, the next sync rebuilds what it owns: the libraries
    // themselves (LibrariesVersion) or the metadata files (NfoVersion).
    public int LibrariesVersion { get; set; }

    public int NfoVersion { get; set; }

    public DateTime? LastSyncUtc { get; set; }

    public string? LastError { get; set; }

    // How far the run in flight has got. The counters above are only replaced once a complete run
    // has seen the whole catalogue, so the dashboard reads these while a sync is running.
    public int RunMovies { get; set; }

    public int RunSeries { get; set; }

    public int RunEpisodes { get; set; }

    // When the run in flight started (or when the last run started, once it is done).
    public DateTime? SyncStartedUtc { get; set; }

    // running | completed | cancelled | failed. Empty for a state written by an older build.
    public string LastRunStatus { get; set; } = string.Empty;

    // The stream base URL the .strm files were written with. When it changes (a new server
    // address), the next sync rewrites every file.
    public string? StreamBaseUrl { get; set; }

    // The PROTV ids the sync has already asked about subtitles - including the ones PROTV has no
    // subtitles for. Without this every single sync would ask PROTV about all 66.000 items again.
    public HashSet<int> SubtitleCheckedIds { get; set; } = new();

    // Version of the stream links in the .strm files. Links are signed from version 1 on, so a
    // state without it makes the next sync rewrite every .strm (and only the .strm files).
    public int StreamTokenVersion { get; set; }

    // Posters PROTV could not give us (path relative to the VOD folder -> when we last tried), so
    // the sync does not ask for the same missing poster again on every run.
    public Dictionary<string, DateTime> PosterFailures { get; set; } = new();
}

// Writes the PROTV VOD catalogue into two Jellyfin libraries the plugin owns: one Films library and
// one Shows library. Every film and episode becomes a ".strm" (pointing at the plugin's own stream
// endpoint), a small ".nfo" with the metadata, the poster and the subtitles PROTV has for it, so
// the whole catalogue plays through Jellyfin like any other library - in the web client, on Android
// TV and in every other app.
internal static class ProTvVodLibrary
{
    private const string StateFileName = "sync-state.json";

    // Bumped when the library layout changes: films and series now live in two libraries of their
    // own type instead of one "mixed" library that Jellyfin read as a single series.
    private const int CurrentLibrariesVersion = 2;

    // Bumped when the metadata the sync writes changes shape: episodes now carry the name PROTV
    // has for them, or the series name and the episode numbers when PROTV has none.
    private const int CurrentNfoVersion = 3;

    // How often per-item work is written back to the state file, so an interrupted sync does not
    // have to ask PROTV about everything again.
    private const int StateSaveEveryItems = 2000;

    // Bumped when the links inside the .strm files change shape (signed links = 1).
    private const int CurrentStreamTokenVersion = 1;

    // A poster that failed is not asked for again for this long.
    private static readonly TimeSpan PosterRetryAfter = TimeSpan.FromDays(7);

    // How many series are prepared at once. Each one costs a few requests before its episodes are
    // reached; the episodes themselves stay inside the shared MaxParallelItems limit.
    private const int MaxParallelSeries = 4;

    // Files the running sync has written. Jellyfin only needs a library scan when this is not 0.
    private static long _filesWritten;

    // Jellyfin's own library scan holds the files the sync writes open for a moment, and Windows
    // then refuses to replace them, so a failed write is retried a few times.
    private const int ReplaceAttempts = 8;

    private static readonly TimeSpan ReplaceRetryDelay = TimeSpan.FromMilliseconds(150);

    // A whole write (temporary file + replace) is attempted this many times before the item is
    // left for the next sync. A fresh temporary name is used for every attempt.
    private const int WriteAttempts = 3;

    private static readonly TimeSpan WriteRetryDelay = TimeSpan.FromMilliseconds(200);

    // How long one wait for a playback lasts before the sync carries on regardless. The next item
    // that has something to download waits again, so a real playback keeps the sync out of the way
    // while a playback that never ends cannot stop it for good.
    private static readonly TimeSpan ViewerPauseLimit = TimeSpan.FromMinutes(15);

    // How many items the sync prepares at the same time. Every item costs a metadata call and a
    // subtitle download, and PROTV answers one request at a time: a handful in flight turns hours
    // of syncing into a fraction of that. Kept modest so the PROTV server stays responsive for
    // playback.
    private const int MaxParallelItems = 8;

    private static readonly object Gate = new();

    // Guards the sync state, which the parallel item work counts into and adds to.
    private static readonly object StateGate = new();
    private static readonly HttpClient DownloadClient = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static ILibraryManager? _libraryManager;
    private static IServerApplicationHost? _applicationHost;
    private static ISessionManager? _sessionManager;
    private static ILogger? _logger;
    private static string? _dataPath;
    private static bool _registered;
    private static int _running;

    public static ProTvVodSyncState State { get; } = new();

    public static bool IsRunning => Volatile.Read(ref _running) != 0;

    public static bool IsRegistered
    {
        get
        {
            lock (Gate)
            {
                return _registered;
            }
        }
    }

    // The folder Jellyfin is pointed at for the films (the series have their own library next to
    // it). Shown on the dashboard page, because it is where the sync writes.
    public static string LibraryRoot
    {
        get
        {
            lock (Gate)
            {
                return _dataPath is null ? string.Empty : ProTvVodPaths.MoviesLibraryRoot(_dataPath);
            }
        }
    }

    public static string SeriesLibraryRoot
    {
        get
        {
            lock (Gate)
            {
                return _dataPath is null ? string.Empty : ProTvVodPaths.SeriesLibraryRoot(_dataPath);
            }
        }
    }

    // True once Jellyfin has the series library too. The dashboard reports both separately, so an
    // admin can see which one is missing.
    public static bool IsSeriesLibraryRegistered
    {
        get
        {
            ILibraryManager? manager;
            string? dataPath;
            lock (Gate)
            {
                manager = _libraryManager;
                dataPath = _dataPath;
            }

            if (manager is null || dataPath is null)
            {
                return false;
            }

            var folder = FindLibrary(manager, ProTvVodPaths.SeriesLibraryName);
            return folder is not null && PointsAt(folder, ProTvVodPaths.SeriesLibraryRoot(dataPath));
        }
    }

    // The address the library's streams point back at: what the last sync wrote, or what a sync
    // started now would write. Shown on the dashboard page, because on a server with several
    // network adapters this is the one thing an admin may want to correct.
    public static string ActiveStreamBaseUrl
        => State.StreamBaseUrl ?? ResolveStreamBaseUrl(Plugin.Instance?.Configuration);

    // The limit, in bytes, on what the sync may download. 0 means no limit.
    public static long AssetBudgetBytes(PluginConfiguration? configuration)
    {
        var gigabytes = configuration?.VodAssetBudgetGb ?? 5;
        return gigabytes <= 0 ? 0 : (long)Math.Round(gigabytes * 1024 * 1024 * 1024, MidpointRounding.AwayFromZero);
    }

    // Called once while the plugin starts, to hand the VOD side the server services it needs.
    public static void Attach(
        ILibraryManager libraryManager,
        IServerApplicationHost applicationHost,
        ISessionManager sessionManager,
        string dataPath,
        ILogger logger)
    {
        lock (Gate)
        {
            _libraryManager = libraryManager;
            _applicationHost = applicationHost;
            _sessionManager = sessionManager;
            _dataPath = dataPath;
            _logger = logger;
        }

        LoadState();
        RefreshRegistered();
    }

    // Adds the two PROTV libraries to Jellyfin the first time (and re-adds them if an admin removed
    // them), pointing at the folders the sync fills.
    public static async Task<bool> EnsureLibraryAsync(CancellationToken cancellationToken)
    {
        ILibraryManager? manager;
        string? dataPath;
        lock (Gate)
        {
            manager = _libraryManager;
            dataPath = _dataPath;
        }

        if (manager is null || dataPath is null)
        {
            return false;
        }

        var moviesRoot = ProTvVodPaths.MoviesLibraryRoot(dataPath);
        var seriesRoot = ProTvVodPaths.SeriesLibraryRoot(dataPath);
        Directory.CreateDirectory(moviesRoot);
        Directory.CreateDirectory(seriesRoot);

        var changed = State.LibrariesVersion != CurrentLibrariesVersion;
        changed |= await RemoveLegacyLibraryAsync(manager, dataPath);
        changed |= await EnsureLibraryAsync(
            manager,
            ProTvVodPaths.LibraryName,
            CollectionTypeOptions.movies,
            moviesRoot);
        changed |= await EnsureLibraryAsync(
            manager,
            ProTvVodPaths.SeriesLibraryName,
            CollectionTypeOptions.tvshows,
            seriesRoot);
        changed |= WriteLibraryIcons(dataPath);
        State.LibrariesVersion = CurrentLibrariesVersion;
        SetRegistered(FindLibrary(manager, ProTvVodPaths.LibraryName) is not null
            && FindLibrary(manager, ProTvVodPaths.SeriesLibraryName) is not null);

        // Only when the libraries themselves changed. Jellyfin's scan reads the very files the sync
        // rewrites, so starting one for every sync would have the two pulling at each other; a sync
        // asks for a scan once it is done instead.
        if (changed)
        {
            QueueLibraryScan();
        }

        return IsRegistered;
    }

    // Pages the whole PROTV catalogue into the libraries. Safe to run again: everything already on
    // disk is left alone, so a second run only picks up what appeared since.
    public static async Task SyncAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            _logger?.LogInformation("A PROTV VOD sync is already running.");
            return;
        }

        var state = State;
        SyncContext? context = null;
        try
        {
            var configuration = Plugin.Instance?.Configuration;
            var moviesEnabled = configuration?.VodMoviesEnabled ?? true;
            var seriesEnabled = configuration?.VodSeriesEnabled ?? true;

            // The progress counters are reset here, under the same lock the item work counts into,
            // so a status request can never read a half-updated run. A kind that is switched off
            // keeps its stored count so the dashboard does not show an empty library mid-run.
            lock (StateGate)
            {
                state.RunMovies = moviesEnabled ? 0 : state.Movies;
                state.RunSeries = seriesEnabled ? 0 : state.Series;
                state.RunEpisodes = seriesEnabled ? 0 : state.Episodes;
                state.SyncStartedUtc = DateTime.UtcNow;
                state.LastRunStatus = "running";
            }

            // Record that a run started before the first item is written: if the server stops hard,
            // the next start can show the run as interrupted instead of looking like a fresh install.
            SaveState();

            var client = ProTvVodData.CreateClient(configuration);
            if (client is null)
            {
                throw new InvalidOperationException(
                    "PROTV is not configured. Save the PROTV address, username and password first.");
            }

            string dataPath;
            lock (Gate)
            {
                dataPath = _dataPath ?? throw new InvalidOperationException("The PROTV VOD libraries are not ready yet.");
            }

            await EnsureLibraryAsync(cancellationToken);

            // The budget is about the disk, not about one run, so what the libraries already take
            // up is measured rather than remembered - the subtitle count with it, because PROTV is
            // only asked about an item once and later runs add to what is already there.
            var assets = MeasureAssets(dataPath);
            state.AssetBytes = assets.Bytes;
            state.Subtitles = assets.Subtitles;

            var streamBaseUrl = ResolveStreamBaseUrl(configuration);
            context = new SyncContext(
                client,
                dataPath,
                streamBaseUrl,
                // A new stream base URL (or a new library layout) invalidates every .strm file.
                !string.Equals(state.StreamBaseUrl, streamBaseUrl, StringComparison.OrdinalIgnoreCase),
                state.NfoVersion < CurrentNfoVersion,
                configuration?.VodDownloadPosters ?? true,
                configuration?.VodDownloadSubtitles ?? true,
                AssetBudgetBytes(configuration),
                progress,
                cancellationToken);

            if (context.BudgetBytes > 0 && state.AssetBytes >= context.BudgetBytes)
            {
                context.BudgetReached = true;
            }

            context.ForceStreams = context.Force || state.StreamTokenVersion < CurrentStreamTokenVersion;
            Interlocked.Exchange(ref _filesWritten, 0);

            _logger?.LogInformation(
                "PROTV VOD sync started. Movies={Movies}, Series={Series}, Subtitles={Subtitles}, Library={LibraryPath}, StreamAddress={StreamBaseUrl}, Downloaded={Downloaded}, Budget={Budget}.",
                moviesEnabled,
                seriesEnabled,
                context.DownloadSubtitles,
                ProTvVodPaths.RootDirectory(dataPath),
                streamBaseUrl,
                DescribeBytes(state.AssetBytes),
                context.BudgetBytes > 0 ? DescribeBytes(context.BudgetBytes) : "unlimited");

            // What the previous complete run listed, to tell a short answer from PROTV apart from a
            // real removal when the sweep decides what to delete.
            var previousMovies = state.Movies;
            var previousSeries = state.Series;

            var movies = 0;
            if (moviesEnabled)
            {
                movies = await SyncMoviesAsync(context);
                SaveState();
            }
            else
            {
                progress?.Report(50);
            }

            var series = 0;
            var episodes = 0;
            if (seriesEnabled)
            {
                (series, episodes) = await SyncSeriesAsync(context);
                SaveState();
            }

            // The stored counts are what the next run's sweep guard compares against. They are only
            // replaced by a listing that was complete and not suspiciously short, so a bad answer
            // from PROTV cannot lower the bar for the run after it (and let that one sweep the
            // library). A kind that is switched off keeps its count for the same reason.
            var moviesListingTrusted = context.MoviesListComplete
                && (previousMovies <= 0 || (long)movies * 2 >= previousMovies);
            var seriesListingTrusted = context.SeriesListComplete
                && (previousSeries <= 0 || (long)series * 2 >= previousSeries);
            lock (StateGate)
            {
                if (moviesEnabled)
                {
                    state.RunMovies = movies;
                    if (moviesListingTrusted)
                    {
                        state.Movies = movies;
                    }
                }

                if (seriesEnabled)
                {
                    state.RunSeries = series;
                    state.RunEpisodes = episodes;
                    if (seriesListingTrusted)
                    {
                        state.Series = series;
                        state.Episodes = episodes;
                    }
                }

                state.LastSyncUtc = DateTime.UtcNow;
                state.LastError = null;
                state.LastRunStatus = "completed";
                state.StreamBaseUrl = streamBaseUrl;
                state.NfoVersion = CurrentNfoVersion;
                state.LibrariesVersion = CurrentLibrariesVersion;
                state.StreamTokenVersion = CurrentStreamTokenVersion;
                state.BudgetReached = context.BudgetReached;
            }

            // Only after a complete run: a run that stopped early has not seen the whole catalogue.
            var removed = SweepRemovedTitles(context, configuration, movies, previousMovies, series, previousSeries);
            SaveState();
            progress?.Report(100);

            _logger?.LogInformation(
                "PROTV VOD sync finished. Movies={Movies}, Series={Series}, Episodes={Episodes}, Subtitles={Subtitles}, Downloaded={Downloaded}.",
                movies,
                series,
                episodes,
                state.Subtitles,
                DescribeBytes(state.AssetBytes));

            if (context.BudgetReached)
            {
                _logger?.LogWarning(
                    "PROTV VOD reached the download limit of {Budget}: posters and subtitles were skipped from there on. Raise the limit in the PROTV settings and run the sync again to fetch them.",
                    DescribeBytes(context.BudgetBytes));
            }

            // Everything the sync wrote (new items, subtitles, posters) is picked up now. Jellyfin's
            // scan covers every library on the server, so it is skipped when nothing changed.
            if (Interlocked.Read(ref _filesWritten) > 0 || removed > 0)
            {
                QueueLibraryScan();
            }
            else
            {
                _logger?.LogInformation("PROTV VOD sync: nothing changed, so no library scan was started.");
            }
        }
        catch (OperationCanceledException)
        {
            lock (StateGate)
            {
                state.LastRunStatus = "cancelled";
            }

            _logger?.LogInformation("The PROTV VOD sync was cancelled.");
            SaveState();
            throw;
        }
        catch (Exception exception)
        {
            lock (StateGate)
            {
                state.LastError = exception.Message;
                state.LastRunStatus = "failed";
            }

            SaveState();
            _logger?.LogError(exception, "The PROTV VOD sync failed.");
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private static async Task<int> SyncMoviesAsync(SyncContext context)
    {
        var movies = 0;
        var page = 1;
        while (true)
        {
            context.Token.ThrowIfCancellationRequested();
            var data = await context.Client.GetMoviesPageAsync(page, ProTvApiClient.VodPageSize, context.Token);
            if (data?.Result is null || data.Result.Count == 0)
            {
                break;
            }

            await Parallel.ForEachAsync(
                data.Result,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelItems, CancellationToken = context.Token },
                async (movie, _) => await TryItemAsync("film", movie.Id, () => WriteMovieAsync(context, movie)));
            movies += data.Result.Count;
            lock (StateGate)
            {
                State.RunMovies = movies;
            }

            var pages = Math.Max(1, data.PageCount);
            context.Progress?.Report(Math.Min(50d, 50d * page / pages));
            if (page >= pages)
            {
                context.MoviesListComplete = true;
                break;
            }

            page++;
        }

        context.Progress?.Report(50);
        return movies;
    }

    private static async Task<(int Series, int Episodes)> SyncSeriesAsync(SyncContext context)
    {
        var seriesCount = 0;
        var episodeCount = 0;
        var page = 1;
        while (true)
        {
            context.Token.ThrowIfCancellationRequested();
            var data = await context.Client.GetSeriesPageAsync(page, 100, context.Token);
            if (data?.Result is null || data.Result.Count == 0)
            {
                break;
            }

            await Parallel.ForEachAsync(
                data.Result,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelSeries, CancellationToken = context.Token },
                async (series, _) =>
                {
                    var written = await TryItemAsync("series", series.Id, () => WriteSeriesAsync(context, series));
                    Interlocked.Add(ref episodeCount, written);
                    var doneSeries = Interlocked.Increment(ref seriesCount);
                    lock (StateGate)
                    {
                        State.RunSeries = doneSeries;
                        State.RunEpisodes = Volatile.Read(ref episodeCount);
                    }
                });

            var pages = Math.Max(1, data.PageCount);
            context.Progress?.Report(50d + Math.Min(50d, 50d * page / pages));
            if (page >= pages)
            {
                context.SeriesListComplete = true;
                break;
            }

            page++;
        }

        context.Progress?.Report(100);
        return (seriesCount, episodeCount);
    }

    private static async Task WriteMovieAsync(SyncContext context, ProTvMovie movie)
    {
        // Counted before any work, so a movie that fails to write is not mistaken for a removed one.
        context.SeenMovieFolders[ProTvVodPaths.MovieFolder(movie)] = 0;

        var directory = ProTvVodPaths.MovieDirectory(context.DataPath, movie);
        Directory.CreateDirectory(directory);

        var strmPath = ProTvVodPaths.MovieStrmFile(context.DataPath, movie);
        await EnsureTextFileAsync(strmPath, StreamUrl(context, ProTvVodKind.Movie, movie.Id), context.ForceStreams, context.Token);

        // The list response carries an empty Categories array; only the detail call fills it. The
        // same detail response is reused for the subtitles below, so a film with genres to repair
        // costs one call and not two.
        var nfoPath = Path.Combine(directory, "movie.nfo");
        var nfoNeedsWrite = context.Force || context.ForceMetadata || NfoNeedsGenres(nfoPath);
        ProTvMovie? detail = null;
        if (nfoNeedsWrite && !HasCategories(movie.Categories))
        {
            detail = await TryLoadMovieDetailAsync(context, movie.Id);
        }

        var categories = HasCategories(movie.Categories) ? movie.Categories : detail?.Categories;
        // A forced rewrite (new stream address or metadata version) must not drop genres just
        // because this one detail call failed: carry the genres already in the NFO forward.
        if (!HasCategories(categories) && (context.Force || context.ForceMetadata))
        {
            categories = ExistingGenres(nfoPath);
        }

        var rewriteNfo = context.Force
            || context.ForceMetadata
            || (nfoNeedsWrite && HasCategories(categories));
        await EnsureTextFileAsync(
            nfoPath,
            MovieNfo(movie, categories),
            rewriteNfo,
            context.Token,
            compareContent: false);

        if (context.DownloadPosters)
        {
            await DownloadAsync(
                context,
                context.Client.NormalizeContentImageUrl(movie.Poster),
                Path.Combine(directory, "poster.jpg"),
                context.Force,
                rememberFailure: true);
        }

        context.ItemDone();
        await SyncSubtitlesAsync(
            context,
            movie.Id,
            strmPath,
            async token => detail is not null
                ? detail.Subtitles
                : (await context.Client.GetMovieDetailAsync(movie.Id, token))?.Subtitles);
    }

    private static async Task<int> WriteSeriesAsync(SyncContext context, ProTvSeries series)
    {
        context.SeenSeriesFolders[ProTvVodPaths.SeriesFolder(series)] = 0;

        var seriesDirectory = ProTvVodPaths.SeriesRootDirectory(context.DataPath, series);
        Directory.CreateDirectory(seriesDirectory);

        // Like films, a series list carries no categories; the detail call is only made when the
        // tvshow.nfo is missing, is being rewritten, or still has no genre.
        var nfoPath = Path.Combine(seriesDirectory, "tvshow.nfo");
        var nfoNeedsWrite = context.Force || context.ForceMetadata || NfoNeedsGenres(nfoPath);
        ProTvSeries? detail = null;
        if (nfoNeedsWrite && !HasCategories(series.Categories))
        {
            detail = await TryLoadSeriesDetailAsync(context, series.Id);
        }

        var categories = HasCategories(series.Categories) ? series.Categories : detail?.Categories;
        if (!HasCategories(categories) && (context.Force || context.ForceMetadata))
        {
            categories = ExistingGenres(nfoPath);
        }

        var rewriteNfo = context.Force
            || context.ForceMetadata
            || (nfoNeedsWrite && HasCategories(categories));
        await EnsureTextFileAsync(
            nfoPath,
            SeriesNfo(series, categories),
            rewriteNfo,
            context.Token,
            compareContent: false);
        if (context.DownloadPosters)
        {
            await DownloadAsync(
                context,
                context.Client.NormalizeContentImageUrl(series.Poster),
                Path.Combine(seriesDirectory, "poster.jpg"),
                context.Force,
                rememberFailure: true);
        }

        var seasons = await context.Client.GetSeasonsForSeriesAsync(series.Id, context.Token);
        var episodes = 0;
        foreach (var season in seasons)
        {
            context.Token.ThrowIfCancellationRequested();
            var seasonIndex = season.Index > 0 ? season.Index : 1;
            var seasonDirectory = ProTvVodPaths.SeasonDirectory(context.DataPath, series, seasonIndex);
            Directory.CreateDirectory(seasonDirectory);

            await EnsureTextFileAsync(
                Path.Combine(seasonDirectory, "season.nfo"),
                SeasonNfo(season, seasonIndex),
                context.Force || context.ForceMetadata,
                context.Token,
                compareContent: false);
            if (context.DownloadPosters)
            {
                await DownloadAsync(
                    context,
                    context.Client.NormalizeContentImageUrl(FirstNonEmpty(season.Poster, season.EpisodePoster, series.Poster)),
                    Path.Combine(seasonDirectory, "poster.jpg"),
                    context.Force,
                rememberFailure: true);
            }

            var seasonEpisodes = await context.Client.GetEpisodesForSeasonAsync(season.Id, context.Token);
            await Parallel.ForEachAsync(
                seasonEpisodes,
                new ParallelOptions { MaxDegreeOfParallelism = MaxParallelItems, CancellationToken = context.Token },
                async (episode, token) =>
                {
                    // One slot per episode in flight, shared with the other series running at once.
                    await context.ItemGate.WaitAsync(token);
                    try
                    {
                        await TryItemAsync(
                            "episode",
                            episode.Id,
                            () => WriteEpisodeAsync(context, series, seasonIndex, episode));
                    }
                    finally
                    {
                        context.ItemGate.Release();
                    }
                });
            episodes += seasonEpisodes.Count;
        }

        return episodes;
    }

    private static async Task WriteEpisodeAsync(
        SyncContext context,
        ProTvSeries series,
        int seasonIndex,
        ProTvEpisode episode)
    {
        var strmPath = ProTvVodPaths.EpisodeStrmFile(context.DataPath, series, seasonIndex, episode);
        await EnsureTextFileAsync(strmPath, StreamUrl(context, ProTvVodKind.Episode, episode.Id), context.ForceStreams, context.Token);
        await EnsureTextFileAsync(
            Path.ChangeExtension(strmPath, ".nfo"),
            EpisodeNfo(series, seasonIndex, episode),
            context.Force || context.ForceMetadata,
            context.Token,
            compareContent: false);

        context.ItemDone();
        await SyncSubtitlesAsync(
            context,
            episode.Id,
            strmPath,
            async token => (await context.Client.GetEpisodeDetailAsync(episode.Id, token))?.Subtitles);
    }

    // Downloads the subtitles PROTV has for an item next to its stream, so Jellyfin offers them
    // while playing. PROTV only lists subtitles on the per-item call, which is why the sync asks
    // about each item once, and remembers the ids that had none.
    private static async Task SyncSubtitlesAsync(
        SyncContext context,
        int itemId,
        string mediaFilePath,
        Func<CancellationToken, Task<IReadOnlyList<ProTvVodSubtitle>?>> loadSubtitles)
    {
        if (!context.DownloadSubtitles || context.BudgetReached)
        {
            return;
        }

        // The in-memory answer first: HasSubtitles reads the item's folder from disk.
        if (IsSubtitleChecked(itemId) || HasSubtitles(mediaFilePath))
        {
            return;
        }

        // Nothing is downloaded below this point, so this is where the sync gives way to a playback.
        await WaitForViewersAsync(context).ConfigureAwait(false);

        IReadOnlyList<ProTvVodSubtitle>? subtitles;
        try
        {
            subtitles = await loadSubtitles(context.Token);
        }
        catch (OperationCanceledException) when (context.Token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Ask again next time: PROTV may simply be unreachable right now.
            _logger?.LogDebug(exception, "Could not read the PROTV subtitles of item {ItemId}.", itemId);
            return;
        }

        var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var complete = true;
        foreach (var subtitle in subtitles ?? Array.Empty<ProTvVodSubtitle>())
        {
            var url = context.Client.NormalizeContentUrl(ProTvCrypto.TryDecrypt(subtitle.Path));
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var language = SubtitleLanguage(subtitle.Language);
            if (!languages.Add(language))
            {
                // Jellyfin shows one subtitle per language per item.
                continue;
            }

            var path = ProTvVodPaths.SubtitleFile(mediaFilePath, language, SubtitleExtension(url));
            if (await DownloadAsync(context, url, path, force: false))
            {
                CountSubtitle();
            }
            else
            {
                complete = false;
            }
        }

        if (complete)
        {
            MarkSubtitleChecked(itemId);
        }
    }

    // The subtitle bookkeeping is written from several items at a time, so it is locked.
    private static bool IsSubtitleChecked(int itemId)
    {
        lock (StateGate)
        {
            return State.SubtitleCheckedIds.Contains(itemId);
        }
    }

    private static void MarkSubtitleChecked(int itemId)
    {
        lock (StateGate)
        {
            State.SubtitleCheckedIds.Add(itemId);
        }
    }

    private static void CountSubtitle()
    {
        lock (StateGate)
        {
            State.Subtitles++;
        }
    }

    private static string StreamUrl(SyncContext context, ProTvVodKind kind, int id)
        => context.StreamBaseUrl + ProTvVodData.StreamPath(kind, id);

    // The .strm files point back at this server, so the address has to be one both the server and
    // its clients can reach. Jellyfin knows its own LAN address; an admin can override it.
    private static string ResolveStreamBaseUrl(PluginConfiguration? configuration)
    {
        var configured = configuration?.VodStreamBaseUrl;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        IServerApplicationHost? host;
        lock (Gate)
        {
            host = _applicationHost;
        }

        var port = host?.HttpPort > 0 ? host.HttpPort : 8096;

        // Prefer the machine's real LAN address. Jellyfin's own answer is used second, because on a
        // host with VMware, Hyper-V or VPN adapters it can pick one of those, which clients cannot
        // reach. The address can always be overridden in the settings.
        var lanAddress = TryFindLanAddress();
        if (lanAddress is not null)
        {
            return $"http://{lanAddress}:{port.ToString(CultureInfo.InvariantCulture)}";
        }

        try
        {
            var url = host?.GetApiUrlForLocalAccess(null, false);
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url.TrimEnd('/');
            }
        }
        catch (Exception exception)
        {
            _logger?.LogDebug(exception, "Could not ask Jellyfin for its own LAN address; falling back to the loopback address.");
        }

        return $"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}";
    }

    // Adapter names that are almost never the address a television or phone can reach.
    private static readonly string[] VirtualAdapterMarkers =
    [
        "vmware", "virtualbox", "hyper-v", "vethernet", "virtual", "tap-", "wsl", "loopback",
        "vpn", "bluetooth", "docker", "zerotier", "tailscale"
    ];

    // Walks the network interfaces and answers with the address most likely to be the LAN one: a
    // real (non-virtual) adapter that has a default gateway, then anything else that is up.
    private static string? TryFindLanAddress()
    {
        try
        {
            var candidates = new List<(int Score, IPAddress Address)>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                var properties = nic.GetIPProperties();
                var hasGateway = properties.GatewayAddresses
                    .Any(gateway => gateway.Address is not null && !gateway.Address.Equals(IPAddress.Any));
                var description = nic.Name + " " + nic.Description;
                var isVirtual = VirtualAdapterMarkers.Any(
                    marker => description.Contains(marker, StringComparison.OrdinalIgnoreCase));

                foreach (var unicast in properties.UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork
                        || IPAddress.IsLoopback(address)
                        || address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var score = (hasGateway ? 0 : 10) + (isVirtual ? 20 : 0);
                    candidates.Add((score, address));
                }
            }

            return candidates
                .OrderBy(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Address.ToString(), StringComparer.Ordinal)
                .Select(candidate => candidate.Address.ToString())
                .FirstOrDefault();
        }
        catch (Exception exception)
        {
            _logger?.LogDebug(exception, "Could not read the network interfaces to pick a stream address.");
            return null;
        }
    }

    private static TypeOptions NoRemoteProviders(string type) => new()
    {
        Type = type,
        MetadataFetchers = Array.Empty<string>(),
        MetadataFetcherOrder = Array.Empty<string>(),
        ImageFetchers = Array.Empty<string>(),
        ImageFetcherOrder = Array.Empty<string>()
    };

    // Nothing in these libraries is matched against the internet: every title carries its own nfo
    // and poster, and letting Jellyfin try to match thousands of PROTV titles online would make a
    // scan last for hours.
    private static LibraryOptions BuildLibraryOptions(string root) => new()
    {
        EnableRealtimeMonitor = false,
        EnableChapterImageExtraction = false,
        ExtractChapterImagesDuringLibraryScan = false,
        ExtractTrickplayImagesDuringLibraryScan = false,
        SaveLocalMetadata = false,
        PathInfos = new[] { new MediaPathInfo { Path = root } },
        TypeOptions = new[]
        {
            NoRemoteProviders("Movie"),
            NoRemoteProviders("Series"),
            NoRemoteProviders("Season"),
            NoRemoteProviders("Episode"),
            NoRemoteProviders("Video")
        }
    };

    // An earlier build kept films and series in one "mixed" library, which Jellyfin then read as a
    // single series. Take that library out; the files themselves are untouched and the two typed
    // libraries pick them up again.
    private static async Task<bool> RemoveLegacyLibraryAsync(ILibraryManager manager, string dataPath)
    {
        try
        {
            var folder = manager.GetVirtualFolders().FirstOrDefault(candidate =>
                string.Equals(candidate.Name, ProTvVodPaths.LibraryName, StringComparison.OrdinalIgnoreCase)
                && !PointsAt(candidate, ProTvVodPaths.MoviesLibraryRoot(dataPath)));
            if (folder is null)
            {
                return false;
            }

            _logger?.LogInformation(
                "PROTV is replacing its old mixed library with a films library and a series library.");
            await manager.RemoveVirtualFolder(folder.Name, false);
            return true;
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Could not remove the old PROTV VOD library.");
            return false;
        }
    }

    private static async Task<bool> EnsureLibraryAsync(
        ILibraryManager manager,
        string name,
        CollectionTypeOptions type,
        string root)
    {
        var changed = false;
        var existing = FindLibrary(manager, name);
        if (existing is not null && (!PointsAt(existing, root) || existing.CollectionType != type))
        {
            // The library is there with another type or another location (an older build): take it
            // out and add it again. Jellyfin refuses to add a library whose folder already exists.
            _logger?.LogInformation("PROTV is re-creating its {LibraryName} library at {LibraryPath}.", name, root);
            try
            {
                await manager.RemoveVirtualFolder(existing.Name, false);
                changed = true;
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(exception, "Could not remove the old {LibraryName} library.", name);
            }

            existing = FindLibrary(manager, name);
        }

        if (existing is null)
        {
            _logger?.LogInformation("PROTV is creating its {LibraryName} library in Jellyfin at {LibraryPath}.", name, root);
            await manager.AddVirtualFolder(name, type, BuildLibraryOptions(root), false);
            existing = FindLibrary(manager, name);
            changed = true;
        }

        if (existing is null)
        {
            _logger?.LogWarning("The {LibraryName} library could not be created in Jellyfin.", name);
            return changed;
        }

        if (!PointsAt(existing, root))
        {
            try
            {
                manager.AddMediaPath(existing.Name, new MediaPathInfo { Path = root });
                changed = true;
            }
            catch (Exception exception)
            {
                _logger?.LogWarning(exception, "Could not point the {LibraryName} library at {LibraryPath}.", existing.Name, root);
            }
        }

        return changed;
    }

    private static VirtualFolderInfo? FindLibrary(ILibraryManager manager, string name)
    {
        try
        {
            return manager.GetVirtualFolders()
                .FirstOrDefault(folder => string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool PointsAt(VirtualFolderInfo folder, string root)
        => folder.Locations?.Any(location => string.Equals(
            location.TrimEnd('\\', '/'),
            root.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase)) ?? false;

    private static void RefreshRegistered()
    {
        ILibraryManager? manager;
        string? dataPath;
        lock (Gate)
        {
            manager = _libraryManager;
            dataPath = _dataPath;
        }

        if (manager is null || dataPath is null)
        {
            return;
        }

        SetRegistered(LibrariesPresent(manager, dataPath));
    }

    private static bool LibrariesPresent(ILibraryManager manager, string dataPath)
    {
        var movies = FindLibrary(manager, ProTvVodPaths.LibraryName);
        var series = FindLibrary(manager, ProTvVodPaths.SeriesLibraryName);
        return movies is not null
            && PointsAt(movies, ProTvVodPaths.MoviesLibraryRoot(dataPath))
            && series is not null
            && PointsAt(series, ProTvVodPaths.SeriesLibraryRoot(dataPath));
    }

    private static void SetRegistered(bool value)
    {
        lock (Gate)
        {
            _registered = value;
        }
    }

    private static void QueueLibraryScan()
    {
        try
        {
            ILibraryManager? manager;
            lock (Gate)
            {
                manager = _libraryManager;
            }

            manager?.QueueLibraryScan();
        }
        catch (Exception exception)
        {
            _logger?.LogDebug(exception, "Could not ask Jellyfin to scan the PROTV VOD libraries.");
        }
    }

    // Writes the library image into both library folders, which is where Jellyfin looks for the
    // picture it shows for a library (the same live_tv icon the PROTV entry in the dashboard menu
    // uses).
    private static bool WriteLibraryIcons(string dataPath)
    {
        var icon = ReadLibraryIcon();
        if (icon is null)
        {
            return false;
        }

        var wrote = false;
        foreach (var root in new[]
        {
            ProTvVodPaths.MoviesLibraryRoot(dataPath),
            ProTvVodPaths.SeriesLibraryRoot(dataPath)
        })
        {
            try
            {
                var path = ProTvVodPaths.LibraryIconFile(root);
                if (IsUsableFile(path) && new FileInfo(path).Length == icon.Length)
                {
                    continue;
                }

                var temporary = path + ".tmp";
                File.WriteAllBytes(temporary, icon);
                File.Move(temporary, path, overwrite: true);
                wrote = true;
            }
            catch (Exception exception)
            {
                // An icon is decoration: never let one hold up the catalogue.
                _logger?.LogDebug(exception, "Could not write the PROTV VOD library icon.");
            }
        }

        return wrote;
    }

    private static byte[]? ReadLibraryIcon()
    {
        try
        {
            var assembly = typeof(ProTvVodLibrary).Assembly;
            var name = assembly.GetManifestResourceNames()
                .FirstOrDefault(candidate =>
                    candidate.EndsWith("ProTvVodLibraryIcon.jpg", StringComparison.OrdinalIgnoreCase));
            if (name is null)
            {
                return null;
            }

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                return null;
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception exception)
        {
            _logger?.LogDebug(exception, "Could not read the bundled PROTV VOD library icon.");
            return null;
        }
    }

    // Writes only when the content is missing or different, or when the sync decided everything
    // must be rewritten (a new stream address or a new metadata layout). Comparing the content
    // repairs a file whose earlier write failed and also picks up a rotated stream-signing secret,
    // which used to leave old signed links in place after the token-version stamp was saved.
    private static async Task EnsureTextFileAsync(
        string path,
        string content,
        bool force,
        CancellationToken cancellationToken,
        bool compareContent = true)
    {
        if (!force && IsUsableFile(path)
            && (!compareContent || await FileContentEqualsAsync(path, content, cancellationToken).ConfigureAwait(false)))
        {
            return;
        }

        if (!await WriteTextFileAtomicallyAsync(path, content, cancellationToken).ConfigureAwait(false))
        {
            throw new IOException($"Could not write \"{path}\".");
        }
    }

    internal static async Task<bool> FileContentEqualsAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!IsUsableFile(path))
            {
                return false;
            }

            var existing = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            return string.Equals(existing, content, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Every write gets its own temporary name, so two writes can never collide on the ".tmp" file
    // (which is what left the earlier sync with an IOException). A failed attempt cleans its
    // temporary file up and is retried with a fresh name a moment later.
    private static async Task<bool> WriteTextFileAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
        => await WriteFileAtomicallyAsync(
            path,
            (temporary, token) => File.WriteAllTextAsync(temporary, content, Utf8NoBom, token),
            cancellationToken).ConfigureAwait(false);

    private static async Task<bool> WriteBytesFileAtomicallyAsync(
        string path,
        byte[] content,
        CancellationToken cancellationToken)
        => await WriteFileAtomicallyAsync(
            path,
            (temporary, token) => File.WriteAllBytesAsync(temporary, content, token),
            cancellationToken).ConfigureAwait(false);

    private static async Task<bool> WriteFileAtomicallyAsync(
        string path,
        Func<string, CancellationToken, Task> write,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        for (var attempt = 0; attempt < WriteAttempts; attempt++)
        {
            var temporary = TemporaryPath(path);
            try
            {
                await write(temporary, cancellationToken).ConfigureAwait(false);
                if (await TryReplaceAsync(temporary, path, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger?.LogDebug(exception, "A write of \"{Path}\" failed; retrying.", path);
            }
            finally
            {
                TryDelete(temporary);
            }

            if (attempt < WriteAttempts - 1)
            {
                await Task.Delay(WriteRetryDelay * (attempt + 1), cancellationToken).ConfigureAwait(false);
            }
        }

        _logger?.LogWarning(
            "Could not write \"{Path}\" after {Attempts} attempts; it is left for the next sync.",
            path,
            WriteAttempts);
        return false;
    }

    private static string TemporaryPath(string path)
        => path + "." + Guid.NewGuid().ToString("N") + ".tmp";

    // Synchronous twin of the helper above, used for the state file (SaveState runs under the state
    // lock). It must not count into the files-written marker: a state file is not library content.
    private static bool WriteTextFileAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        for (var attempt = 0; attempt < WriteAttempts; attempt++)
        {
            var temporary = TemporaryPath(path);
            try
            {
                File.WriteAllText(temporary, content, Utf8NoBom);
                if (TryReplace(temporary, path, countAsWritten: false))
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger?.LogDebug(exception, "A write of \"{Path}\" failed; retrying.", path);
            }
            finally
            {
                TryDelete(temporary);
            }

            if (attempt < WriteAttempts - 1)
            {
                Thread.Sleep(WriteRetryDelay * (attempt + 1));
            }
        }

        _logger?.LogWarning(
            "Could not write \"{Path}\" after {Attempts} attempts; it is left for the next sync.",
            path,
            WriteAttempts);
        return false;
    }

    // Puts a file that was just written next to its final name in place. Jellyfin's own library scan
    // reads the very files the sync writes, and Windows refuses to replace a file while it is open,
    // so a collision is retried briefly before the write is given up on.
    private static async Task<bool> TryReplaceAsync(
        string temporary,
        string path,
        CancellationToken cancellationToken,
        bool countAsWritten = true)
    {
        for (var attempt = 0; attempt < ReplaceAttempts; attempt++)
        {
            try
            {
                File.Move(temporary, path, overwrite: true);
                if (countAsWritten)
                {
                    Interlocked.Increment(ref _filesWritten);
                }

                return true;
            }
            catch (Exception exception) when (attempt < ReplaceAttempts - 1
                && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(ReplaceRetryDelay * (attempt + 1), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger?.LogDebug(exception, "Could not write \"{Path}\".", path);
                TryDelete(temporary);
                return false;
            }
        }

        TryDelete(temporary);
        return false;
    }

    private static bool TryReplace(string temporary, string path, bool countAsWritten)
    {
        for (var attempt = 0; attempt < ReplaceAttempts; attempt++)
        {
            try
            {
                File.Move(temporary, path, overwrite: true);
                if (countAsWritten)
                {
                    Interlocked.Increment(ref _filesWritten);
                }

                return true;
            }
            catch (Exception exception) when (attempt < ReplaceAttempts - 1
                && exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(ReplaceRetryDelay * (attempt + 1));
            }
            catch (Exception exception)
            {
                _logger?.LogDebug(exception, "Could not write \"{Path}\".", path);
                TryDelete(temporary);
                return false;
            }
        }

        TryDelete(temporary);
        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // A leftover ".tmp" is harmless: Jellyfin does not read it as media, and the next
            // write uses a fresh name instead of colliding with it.
        }
    }

    // Removes the films and series folders PROTV no longer lists. Only folders carrying the plugin's
    // own "[protv-<id>]" marker are ever touched, and only after a complete run that listed a sane
    // share of what the previous run did, so a short or failed answer from PROTV cannot empty the
    // library. Returns how many folders were removed.
    private static int SweepRemovedTitles(
        SyncContext context,
        PluginConfiguration? configuration,
        int moviesListed,
        int previousMovies,
        int seriesListed,
        int previousSeries)
    {
        if (!(configuration?.VodRemoveDeletedTitles ?? true) || context.Token.IsCancellationRequested)
        {
            return 0;
        }

        var removed = 0;
        try
        {
            if ((configuration?.VodMoviesEnabled ?? true) && context.MoviesListComplete && IsPlausibleListing(moviesListed, previousMovies, "films"))
            {
                removed += RemoveUnlisted(ProTvVodPaths.MoviesDirectory(context.DataPath), context.SeenMovieFolders);
            }

            if ((configuration?.VodSeriesEnabled ?? true) && context.SeriesListComplete && IsPlausibleListing(seriesListed, previousSeries, "series"))
            {
                removed += RemoveUnlisted(ProTvVodPaths.SeriesDirectory(context.DataPath), context.SeenSeriesFolders);
            }
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Could not remove the PROTV VOD titles that are no longer listed.");
        }

        if (removed > 0)
        {
            _logger?.LogInformation("PROTV VOD: removed {Count} titles PROTV no longer lists.", removed);
        }

        return removed;
    }

    // Only a listing of at least half of what the last complete run saw may drive a sweep. When
    // that previous count is unknown (0, e.g. after an upgrade or a run that never completed), the
    // sweep is skipped entirely: an empty or short library is preferable to deleting folders.
    internal static bool IsPlausibleListing(int listed, int previous, string kind)
    {
        if (previous <= 0)
        {
            _logger?.LogWarning(
                "PROTV VOD: the sweep of removed {Kind} was skipped because the previous count is unknown (0). The folders are left alone; the count from this run is stored for the next one.",
                kind);
            return false;
        }

        if (listed <= 0 || (long)listed * 2 < previous)
        {
            _logger?.LogWarning(
                "PROTV VOD: the sweep of removed {Kind} was skipped because PROTV listed {Listed} while the last complete run listed {Previous}. The folders are left alone this run.",
                kind,
                listed,
                previous);
            return false;
        }

        return true;
    }

    private static int RemoveUnlisted(string root, ConcurrentDictionary<string, byte> seen)
    {
        if (!Directory.Exists(root) || seen.IsEmpty)
        {
            return 0;
        }

        var removed = 0;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (name.Contains(" [protv-", StringComparison.Ordinal) && !seen.ContainsKey(name))
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                    removed++;
                }
                catch (Exception exception)
                {
                    _logger?.LogDebug(exception, "Could not remove \"{Folder}\".", name);
                }
            }
        }

        return removed;
    }

    // Runs the work for one film, series or episode. One file that cannot be written must never stop
    // the rest of the catalogue, so a failure is logged and the item is left for the next sync.
    private static async Task TryItemAsync(string itemType, int id, Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(
                exception,
                "Could not write the PROTV {ItemType} {ItemId}; it is left for the next sync.",
                itemType,
                id);
        }
    }

    private static async Task<T> TryItemAsync<T>(string itemType, int id, Func<Task<T>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(
                exception,
                "Could not write the PROTV {ItemType} {ItemId}; it is left for the next sync.",
                itemType,
                id);
            return default!;
        }
    }

    // PROTV serves the sync's downloads and the streams people watch from one small host, which has
    // little room to spare: while somebody is watching a PROTV film or episode the sync waits, so the
    // playback keeps the bandwidth it needs (without it, a stream starves and playback fails). A
    // playback that never ends cannot hold the sync up for good - the wait is bounded.
    private static async Task WaitForViewersAsync(SyncContext context)
    {
        ISessionManager? sessions;
        string? dataPath;
        lock (Gate)
        {
            sessions = _sessionManager;
            dataPath = _dataPath;
        }

        if (sessions is null || dataPath is null)
        {
            return;
        }

        var started = DateTime.UtcNow;
        while (!context.Token.IsCancellationRequested)
        {
            bool watching;
            try
            {
                watching = IsWatchingVod(sessions, dataPath);
            }
            catch (Exception exception)
            {
                // Never let the question itself hold up the sync.
                _logger?.LogDebug(exception, "Could not tell whether a PROTV item is playing.");
                return;
            }

            if (!watching)
            {
                if (context.PausedForViewer)
                {
                    context.PausedForViewer = false;
                    _logger?.LogInformation("PROTV VOD: the playback has stopped, the sync carries on.");
                }

                return;
            }

            if (!context.PausedForViewer)
            {
                context.PausedForViewer = true;
                _logger?.LogInformation(
                    "PROTV VOD: a film or episode is playing, so the sync waits for it to finish before downloading more.");
            }

            if (DateTime.UtcNow - started > ViewerPauseLimit)
            {
                context.PausedForViewer = false;
                _logger?.LogInformation(
                    "PROTV VOD: a playback has been running for longer than {Limit}, the sync carries on.",
                    ViewerPauseLimit);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), context.Token).ConfigureAwait(false);
        }
    }

    private static bool IsWatchingVod(ISessionManager sessions, string dataPath)
    {
        var root = ProTvVodPaths.RootDirectory(dataPath);
        return sessions.Sessions.Any(session =>
            session.NowPlayingItem?.Path is { Length: > 0 } path
            && path.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            && !(session.PlayState?.IsPaused ?? false));
    }

    // Downloads a file the library needs (a poster or a subtitle), keeping to the budget the admin
    // set. Answers whether the file is in place.
    private static async Task<bool> DownloadAsync(
        SyncContext context,
        string? url,
        string path,
        bool force,
        bool rememberFailure = false)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (!force && IsUsableFile(path))
        {
            return true;
        }

        if (context.BudgetReached)
        {
            return false;
        }

        // A poster PROTV could not give us is not asked for again for a week.
        string? failureKey = null;
        if (rememberFailure)
        {
            failureKey = Path.GetRelativePath(ProTvVodPaths.RootDirectory(context.DataPath), path);
            if (!force && RecentlyFailed(failureKey))
            {
                return false;
            }
        }

        try
        {
            using var response = await DownloadClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, context.Token);
            if (!response.IsSuccessStatusCode)
            {
                RememberFailure(failureKey);
                return false;
            }

            var replaced = IsUsableFile(path) ? new FileInfo(path).Length : 0;

            // Buffered first: the temporary file is then written with the same retrying atomic
            // helper the metadata files use, and a retry never has to ask PROTV for the bytes again.
            var bytes = await response.Content.ReadAsByteArrayAsync(context.Token).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                RememberFailure(failureKey);
                return false;
            }

            if (!await WriteBytesFileAtomicallyAsync(path, bytes, context.Token).ConfigureAwait(false))
            {
                return false;
            }

            context.Consume(new FileInfo(path).Length - replaced);
            ForgetFailure(failureKey);
            return true;
        }
        catch (OperationCanceledException) when (context.Token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A poster or a subtitle is decoration: never let one hold up the catalogue.
            _logger?.LogWarning(exception, "Could not download a PROTV file from {Host}; it is left for the next sync.", Describe(url));
            RememberFailure(failureKey);
            return false;
        }
    }

    private static bool RecentlyFailed(string key)
    {
        lock (StateGate)
        {
            return State.PosterFailures.TryGetValue(key, out var when) && DateTime.UtcNow - when < PosterRetryAfter;
        }
    }

    private static void RememberFailure(string? key)
    {
        if (key is null)
        {
            return;
        }

        lock (StateGate)
        {
            State.PosterFailures[key] = DateTime.UtcNow;
        }
    }

    private static void ForgetFailure(string? key)
    {
        if (key is null)
        {
            return;
        }

        lock (StateGate)
        {
            State.PosterFailures.Remove(key);
        }
    }

    private static bool IsUsableFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Adds up the posters, subtitles and library icons the sync has downloaded, so the download
    // limit covers what is on the disk and not only what this run adds. Counts the subtitle files
    // too, so the number the dashboard shows is the one on the disk and not a running total.
    private static (long Bytes, int Subtitles) MeasureAssets(string dataPath)
    {
        try
        {
            var root = ProTvVodPaths.RootDirectory(dataPath);
            if (!Directory.Exists(root))
            {
                return (0, 0);
            }

            long total = 0;
            var subtitles = 0;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(file);
                if (IsSubtitleExtension(extension))
                {
                    subtitles++;
                    total += new FileInfo(file).Length;
                }
                else if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
                {
                    total += new FileInfo(file).Length;
                }
            }

            return (total, subtitles);
        }
        catch (Exception exception)
        {
            _logger?.LogDebug(exception, "Could not measure what the PROTV VOD libraries take up.");
            return (State.AssetBytes, State.Subtitles);
        }
    }

    // Logs only the scheme and host of a PROTV URL: never a full PROTV path.
    private static string Describe(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Host}" : "(unknown)";

    private static string DescribeBytes(long bytes)
        => bytes >= 1024L * 1024 * 1024
            ? string.Format(CultureInfo.InvariantCulture, "{0:0.##} GB", (double)bytes / (1024 * 1024 * 1024))
            : string.Format(CultureInfo.InvariantCulture, "{0:0.#} MB", (double)bytes / (1024 * 1024));

    // Jellyfin picks a subtitle up from the file name, so the language PROTV reports has to become
    // the two or three letter code the name uses.
    private static string SubtitleLanguage(string? language)
    {
        var text = language?.Trim().ToLowerInvariant();
        return text switch
        {
            null or "" => "und",
            "arabic" or "ar" or "العربية" or "عربي" => "ar",
            "english" or "en" => "en",
            "french" or "fr" => "fr",
            "german" or "de" => "de",
            "spanish" or "es" => "es",
            "italian" or "it" => "it",
            "turkish" or "tr" => "tr",
            "kurdish" or "ku" => "ku",
            "persian" or "farsi" or "fa" => "fa",
            "urdu" or "ur" => "ur",
            "hindi" or "hi" => "hi",
            "russian" or "ru" => "ru",
            "hebrew" or "he" => "he",
            _ => CleanLanguage(text)
        };

        static string CleanLanguage(string value)
        {
            var letters = new string(value.Where(char.IsLetter).ToArray());
            return letters.Length is >= 2 and <= 3 && letters.All(char.IsAscii)
                ? letters
                : "und";
        }
    }

    private static string SubtitleExtension(string url)
    {
        var extension = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? Path.GetExtension(uri.AbsolutePath).ToLowerInvariant()
            : string.Empty;
        return IsSubtitleExtension(extension) ? extension : ".srt";
    }

    private static bool IsSubtitleExtension(string? extension) => extension?.ToLowerInvariant() switch
    {
        ".srt" or ".vtt" or ".ass" or ".ssa" or ".sub" or ".ttml" or ".dfxp" => true,
        _ => false
    };

    // True when a subtitle for this item is already next to it.
    private static bool HasSubtitles(string mediaFilePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(mediaFilePath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return false;
            }

            var prefix = Path.GetFileNameWithoutExtension(mediaFilePath) + ".";
            return Directory.EnumerateFiles(directory).Any(file =>
                Path.GetFileName(file).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && IsSubtitleExtension(Path.GetExtension(file)));
        }
        catch (Exception)
        {
            return false;
        }
    }

    // The list endpoints send Categories as an empty array; only the detail endpoint fills it. An
    // item without categories still gets a valid NFO - genres are simply absent and the next run
    // asks again.
    private static bool HasCategories(IReadOnlyList<ProTvVodCategory>? categories)
        => categories is not null
            && categories.Any(category => !string.IsNullOrWhiteSpace(FirstNonEmpty(category.TitleEn, category.TitleAr)));

    // True when the NFO is missing or was written before genres were known. Existing files are only
    // touched when they need this one repair, so adding genres does not rewrite the whole library.
    internal static bool NfoNeedsGenres(string path)
    {
        try
        {
            if (!IsUsableFile(path))
            {
                return true;
            }

            var text = File.ReadAllText(path, Encoding.UTF8);
            return text.IndexOf("<genre>", StringComparison.OrdinalIgnoreCase) < 0;
        }
        catch (Exception exception)
        {
            _logger?.LogDebug(exception, "Could not read \"{Path}\"; its NFO will be written again.", path);
            return true;
        }
    }

    // Genres already in an NFO, used only when a forced rewrite cannot ask PROTV for fresh ones.
    internal static IReadOnlyList<ProTvVodCategory>? ExistingGenres(string path)
    {
        try
        {
            if (!IsUsableFile(path))
            {
                return null;
            }

            var genres = XDocument.Load(path)
                .Descendants("genre")
                .Select(element => element.Value.Trim())
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select((value, index) => new ProTvVodCategory { Id = index + 1, TitleEn = value })
                .ToList();
            return genres.Count > 0 ? genres : null;
        }
        catch (Exception exception)
        {
            _logger?.LogDebug(exception, "Could not read the genres of \"{Path}\".", path);
            return null;
        }
    }

    private static async Task<ProTvMovie?> TryLoadMovieDetailAsync(SyncContext context, int movieId)
    {
        try
        {
            var detail = await context.Client.GetMovieDetailAsync(movieId, context.Token).ConfigureAwait(false);
            if (detail is null)
            {
                _logger?.LogWarning(
                    "PROTV returned no details for film {ItemId}; its NFO is written or kept without genres and the next sync tries again.",
                    movieId);
            }

            return detail;
        }
        catch (OperationCanceledException) when (context.Token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(
                exception,
                "Could not read the PROTV details of film {ItemId}; its NFO is written or kept without genres and the next sync tries again.",
                movieId);
            return null;
        }
    }

    private static async Task<ProTvSeries?> TryLoadSeriesDetailAsync(SyncContext context, int seriesId)
    {
        try
        {
            var detail = await context.Client.GetSeriesDetailAsync(seriesId, context.Token).ConfigureAwait(false);
            if (detail is null)
            {
                _logger?.LogWarning(
                    "PROTV returned no details for series {ItemId}; its NFO is written or kept without genres and the next sync tries again.",
                    seriesId);
            }

            return detail;
        }
        catch (OperationCanceledException) when (context.Token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(
                exception,
                "Could not read the PROTV details of series {ItemId}; its NFO is written or kept without genres and the next sync tries again.",
                seriesId);
            return null;
        }
    }

    private static string MovieNfo(ProTvMovie movie, IReadOnlyList<ProTvVodCategory>? categories)
    {
        var root = new XElement("movie");
        root.Add(new XElement("title", DisplayTitle(movie.Title, movie.Id)));
        AddText(root, "plot", FirstNonEmpty(movie.PlotEn, movie.PlotAr));
        if (movie.ReleaseYear is > 0)
        {
            root.Add(new XElement("year", movie.ReleaseYear.Value));
        }

        AddRating(root, movie.ImdbScore > 0 ? movie.ImdbScore : movie.ProtvScore);
        AddText(root, "mpaa", FirstNonEmpty(movie.AudienceRatingString, movie.AudienceRating));
        AddGenres(root, categories);
        return Nfo(root);
    }

    private static string SeriesNfo(ProTvSeries series, IReadOnlyList<ProTvVodCategory>? categories)
    {
        var root = new XElement("tvshow");
        root.Add(new XElement("title", DisplayTitle(series.Title, series.Id)));
        AddText(root, "plot", FirstNonEmpty(series.DescriptionEn, series.DescriptionAr));
        AddRating(root, series.ImdbScore);
        AddText(root, "mpaa", FirstNonEmpty(series.AudienceRatingString, series.AudienceRating));
        AddGenres(root, categories);
        return Nfo(root);
    }

    private static string SeasonNfo(ProTvSeason season, int seasonIndex)
    {
        var root = new XElement("season");
        root.Add(new XElement("title", $"Season {seasonIndex.ToString("00", CultureInfo.InvariantCulture)}"));
        root.Add(new XElement("seasonnumber", seasonIndex));
        AddText(root, "plot", FirstNonEmpty(season.PlotEn, season.PlotAr));
        if (season.ReleaseYear is > 0)
        {
            root.Add(new XElement("year", season.ReleaseYear.Value));
        }

        AddRating(root, season.ImdbScore);
        return Nfo(root);
    }

    private static string EpisodeNfo(ProTvSeries series, int seasonIndex, ProTvEpisode episode)
    {
        var episodeNumber = episode.Index > 0 ? episode.Index : 1;
        var root = new XElement("episodedetails");
        root.Add(new XElement("title", ProTvVodPaths.EpisodeTitle(series, seasonIndex, episode)));
        AddText(root, "plot", FirstNonEmpty(episode.DescrptionEn, episode.DescrptionAr));
        root.Add(new XElement("season", seasonIndex));
        root.Add(new XElement("episode", episodeNumber));
        AddText(root, "showtitle", SeriesDisplayTitle(series));
        if (episode.ReleaseYear is > 0)
        {
            root.Add(new XElement("year", episode.ReleaseYear.Value));
        }

        AddRating(root, episode.ImdbScore);
        return Nfo(root);
    }

    private static void AddText(XElement root, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            root.Add(new XElement(name, value.Trim()));
        }
    }

    private static void AddRating(XElement root, double rating)
    {
        if (rating > 0)
        {
            root.Add(new XElement("rating", Math.Round(rating, 1)));
        }
    }

    internal static void AddGenres(XElement root, IReadOnlyList<ProTvVodCategory>? categories)
    {
        if (categories is null)
        {
            return;
        }

        foreach (var name in categories
            .Select(category => FirstNonEmpty(category.TitleEn, category.TitleAr))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            root.Add(new XElement("genre", name));
        }
    }

    private static string Nfo(XElement root)
        => "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>" + Environment.NewLine + root;

    private static string DisplayTitle(string? title, int id)
        => string.IsNullOrWhiteSpace(title) ? $"PROTV {id}" : title.Trim();

    private static string SeriesDisplayTitle(ProTvSeries series) => DisplayTitle(series.Title, series.Id);

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string StatePath
    {
        get
        {
            lock (Gate)
            {
                return _dataPath is null ? string.Empty : Path.Combine(ProTvVodPaths.RootDirectory(_dataPath), StateFileName);
            }
        }
    }

    private static void LoadState()
    {
        var path = StatePath;
        if (path.Length == 0 || !File.Exists(path))
        {
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<ProTvVodSyncState>(File.ReadAllText(path, Encoding.UTF8));
            if (loaded is null)
            {
                _logger?.LogWarning(
                    "The PROTV VOD sync state at \"{Path}\" is empty; it is set aside and a fresh state is started.",
                    path);
                PreserveCorruptState(path);
                return;
            }

            State.Movies = loaded.Movies;
            State.Series = loaded.Series;
            State.Episodes = loaded.Episodes;
            State.Subtitles = loaded.Subtitles;
            State.AssetBytes = loaded.AssetBytes;
            State.BudgetReached = loaded.BudgetReached;
            State.LibrariesVersion = loaded.LibrariesVersion;
            State.NfoVersion = loaded.NfoVersion;
            State.LastSyncUtc = loaded.LastSyncUtc;
            State.LastError = loaded.LastError;
            State.RunMovies = loaded.RunMovies;
            State.RunSeries = loaded.RunSeries;
            State.RunEpisodes = loaded.RunEpisodes;
            State.SyncStartedUtc = loaded.SyncStartedUtc;
            // A state file that still says "running" belongs to a process that stopped before it
            // could write its final status; on this fresh start it is an interrupted (cancelled)
            // run, not one that is still going.
            State.LastRunStatus = string.Equals(loaded.LastRunStatus, "running", StringComparison.OrdinalIgnoreCase)
                ? "cancelled"
                : loaded.LastRunStatus ?? string.Empty;
            State.StreamBaseUrl = loaded.StreamBaseUrl;
            State.SubtitleCheckedIds = loaded.SubtitleCheckedIds ?? new HashSet<int>();
            State.StreamTokenVersion = loaded.StreamTokenVersion;
            State.PosterFailures = (loaded.PosterFailures ?? new Dictionary<string, DateTime>())
                .Where(pair => DateTime.UtcNow - pair.Value < PosterRetryAfter)
                .ToDictionary(pair => pair.Key, pair => pair.Value);
        }
        catch (JsonException exception)
        {
            _logger?.LogWarning(
                exception,
                "Could not read the PROTV VOD sync state; the corrupt file is set aside and a fresh state is started.");
            PreserveCorruptState(path);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Could not read the PROTV VOD sync state; starting from scratch.");
        }
    }

    // Keeps the unreadable state instead of silently overwriting it: it is the only record of what
    // the last run had downloaded, and an admin may want to look at it.
    private static void PreserveCorruptState(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var target = Path.Combine(directory, $"sync-state.corrupt-{stamp}.json");
            var suffix = 1;
            while (File.Exists(target))
            {
                target = Path.Combine(
                    directory,
                    $"sync-state.corrupt-{stamp}-{suffix.ToString(CultureInfo.InvariantCulture)}.json");
                suffix++;
            }

            File.Move(path, target);
            _logger?.LogWarning("The unreadable PROTV VOD sync state was kept as \"{Path}\".", target);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(
                exception,
                "Could not set aside the unreadable PROTV VOD sync state at \"{Path}\".",
                path);
        }
    }

    private static void SaveState()
    {
        var path = StatePath;
        if (path.Length == 0)
        {
            return;
        }

        try
        {
            // The item work in flight counts into the state, so it is written under the same lock.
            lock (StateGate)
            {
                // Written to a temporary file first and moved into place, so a crash in the middle
                // of a write cannot leave a half-written state file (which would reset the sync).
                WriteTextFileAtomically(path, JsonSerializer.Serialize(State));
            }
        }
        catch (Exception exception)
        {
            _logger?.LogWarning(exception, "Could not save the PROTV VOD sync state.");
        }
    }

    // Everything one sync run needs. The download budget lives here, because posters and subtitles
    // share it.
    private sealed class SyncContext
    {
        public SyncContext(
            ProTvApiClient client,
            string dataPath,
            string streamBaseUrl,
            bool force,
            bool forceMetadata,
            bool downloadPosters,
            bool downloadSubtitles,
            long budgetBytes,
            IProgress<double>? progress,
            CancellationToken token)
        {
            Client = client;
            DataPath = dataPath;
            StreamBaseUrl = streamBaseUrl;
            Force = force;
            ForceMetadata = forceMetadata;
            DownloadPosters = downloadPosters;
            DownloadSubtitles = downloadSubtitles;
            BudgetBytes = budgetBytes;
            Progress = progress;
            Token = token;
        }

        public ProTvApiClient Client { get; }

        public string DataPath { get; }

        public string StreamBaseUrl { get; }

        public bool Force { get; }

        public bool ForceMetadata { get; }

        // Rewrite the .strm files (their links changed) without redownloading anything else.
        public bool ForceStreams { get; set; }

        // Every item in flight in the series phase takes one slot, so series and episodes running
        // side by side never exceed MaxParallelItems requests together.
        public SemaphoreSlim ItemGate { get; } = new(MaxParallelItems, MaxParallelItems);

        // The folders the catalogue listed in this run: what is not in them any more can go.
        public ConcurrentDictionary<string, byte> SeenMovieFolders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public ConcurrentDictionary<string, byte> SeenSeriesFolders { get; } = new(StringComparer.OrdinalIgnoreCase);

        // Set only when the paging reached the last page PROTV reported. A page that came back
        // empty or failed ends the loop too, and then the folders seen are only part of the catalogue.
        public bool MoviesListComplete { get; set; }

        public bool SeriesListComplete { get; set; }

        public bool DownloadPosters { get; }

        public bool DownloadSubtitles { get; }

        // 0 means the admin set no limit.
        public long BudgetBytes { get; }

        public IProgress<double>? Progress { get; }

        public CancellationToken Token { get; }

        public bool BudgetReached
        {
            get => _budgetReached;

            set => _budgetReached = value;
        }

        // Set while the sync is giving way to somebody watching a PROTV film or episode.
        public bool PausedForViewer { get; set; }

        // Counts finished items so the state file is written now and then.
        private int _itemsDone;

        // Read and written from every item in flight.
        private volatile bool _budgetReached;

        public void ItemDone()
        {
            if (Interlocked.Increment(ref _itemsDone) % StateSaveEveryItems == 0)
            {
                SaveState();
            }
        }

        // Books what a download added to the disk and stops downloading once the budget is used up.
        public void Consume(long bytes)
        {
            if (bytes == 0)
            {
                return;
            }

            lock (StateGate)
            {
                State.AssetBytes = Math.Max(0, State.AssetBytes + bytes);
                if (BudgetBytes > 0 && State.AssetBytes >= BudgetBytes)
                {
                    BudgetReached = true;
                }
            }
        }
    }
}
