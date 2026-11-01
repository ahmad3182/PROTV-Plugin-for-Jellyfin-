using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace PROTV.Plugin;

// The VOD half of the PROTV API surface.
//   /PROTV/Vod/Stream/{kind}/{id}/{signature}.m3u8 - what every ".strm" in the PROTV VOD library
//                                        points at. Open to anonymous callers on purpose (Jellyfin's
//                                        own ffmpeg and a direct-playing client fetch it without a
//                                        Jellyfin token), but only with the signature this server
//                                        put in the link.
//   /PROTV/Vod/Movies, /Series, ...    - what the home page tab browses.
//   /PROTV/Vod/Status, /Sync           - what the dashboard page reports and triggers.
public sealed partial class ProTvTabController
{
    // Links written before they were signed: refused. The next VOD sync rewrites every .strm with
    // a signed link, so run the sync once after updating.
    [HttpGet("Vod/Stream/{kind}/{id:int}.m3u8")]
    [AllowAnonymous]
    public ActionResult GetUnsignedVodStream(string kind, int id) => NotFound();

    [HttpGet("Vod/Stream/{kind}/{id:int}/{signature}.m3u8")]
    [AllowAnonymous]
    public async Task<ActionResult> GetVodStream(string kind, int id, string signature, CancellationToken cancellationToken)
    {
        var parsed = ProTvVodData.ParseKind(kind);
        if (parsed is null || id <= 0 || !ProTvVodData.IsValidStreamToken(parsed.Value, id, signature))
        {
            return NotFound();
        }

        var url = await ProTvVodData.ResolveStreamUrlAsync(parsed.Value, id, cancellationToken);
        if (string.IsNullOrWhiteSpace(url))
        {
            return NotFound();
        }

        // The location is short-lived enough that it is better resolved on every play.
        Response.Headers.CacheControl = "no-store";
        return Redirect(url!);
    }

    [HttpGet("Vod/Movies")]
    [Authorize]
    public async Task<ActionResult<ProTvTabVodPage>> GetVodMovies(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 60,
        [FromQuery] string? search = null,
        [FromQuery] int? categoryId = null,
        CancellationToken cancellationToken = default)
        => await ProTvVodData.GetMoviesAsync(page, pageSize, search, categoryId, cancellationToken);

    [HttpGet("Vod/Series")]
    [Authorize]
    public async Task<ActionResult<ProTvTabVodPage>> GetVodSeries(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 60,
        [FromQuery] string? search = null,
        [FromQuery] int? categoryId = null,
        CancellationToken cancellationToken = default)
        => await ProTvVodData.GetSeriesAsync(page, pageSize, search, categoryId, cancellationToken);

    [HttpGet("Vod/SeriesDetail")]
    [Authorize]
    public async Task<ActionResult<ProTvTabVodSeriesDetail>> GetVodSeriesDetail(
        [FromQuery] int seriesId,
        CancellationToken cancellationToken = default)
    {
        var detail = await ProTvVodData.GetSeriesDetailAsync(seriesId, cancellationToken);
        return detail is null ? NotFound() : detail;
    }

    [HttpGet("Vod/Categories")]
    [Authorize]
    public async Task<ActionResult<IReadOnlyList<ProTvTabVodCategory>>> GetVodCategories(
        [FromQuery] bool series = false,
        CancellationToken cancellationToken = default)
        => Ok(await ProTvVodData.GetCategoriesAsync(series, cancellationToken));

    [HttpGet("Vod/Status")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<ProTvVodStatus> GetVodStatus() => ProTvVodData.GetStatus(ProTvVodLibrary.IsRunning);

    [HttpPost("Vod/Sync")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<ProTvVodStatus> StartVodSync()
    {
        var taskManager = HttpContext.RequestServices.GetService<ITaskManager>();
        var scheduled = taskManager?.ScheduledTasks
            .Any(worker => worker.ScheduledTask is ProTvVodSyncTask) ?? false;

        if (scheduled)
        {
            // Queue it through Jellyfin so the dashboard shows the job and its progress too.
            taskManager!.QueueScheduledTask<ProTvVodSyncTask>();
        }
        else
        {
            // Jellyfin has not picked the task up (yet): run the sync in the background anyway.
            _ = Task.Run(() => ProTvVodLibrary.SyncAsync(null, CancellationToken.None));
        }

        return ProTvVodData.GetStatus(ProTvVodLibrary.IsRunning);
    }

}
