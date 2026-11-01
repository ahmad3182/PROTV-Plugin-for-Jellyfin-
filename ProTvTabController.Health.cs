using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace PROTV.Plugin;

// Channel health for the PROTV home page tab and the dashboard page.
//   /PROTV/Channels/Health       - what the last check found. Any signed-in user may read it: it is
//                                  the same answer the home page tab draws next to each channel.
//   /PROTV/Channels/Health/Check - starts a check now (admins only).
public sealed partial class ProTvTabController
{
    [HttpGet("Channels/Health")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<ProTvChannelHealthReport> GetChannelHealth()
    {
        Response.Headers.CacheControl = "no-store";
        return ProTvChannelHealth.GetStatus();
    }

    [HttpPost("Channels/Health/Check")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult<ProTvChannelHealthReport> StartChannelHealthCheck()
    {
        var taskManager = HttpContext.RequestServices.GetService<ITaskManager>();
        var scheduled = taskManager?.ScheduledTasks
            .Any(worker => worker.ScheduledTask is ProTvChannelHealthTask) ?? false;

        if (scheduled)
        {
            // Queue it through Jellyfin so the dashboard shows the job and its progress too.
            taskManager!.QueueScheduledTask<ProTvChannelHealthTask>();
        }
        else
        {
            // Jellyfin has not picked the task up (yet): run the check in the background anyway.
            _ = Task.Run(() => ProTvChannelHealth.RunAsync(null, CancellationToken.None));
        }

        return ProTvChannelHealth.GetStatus();
    }
}
