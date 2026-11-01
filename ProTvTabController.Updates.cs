using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace PROTV.Plugin;

public sealed partial class ProTvTabController
{
    // Dashboard update notice. Admin-only and cached server-side for six hours; force=true is
    // used by the "Check now" button.
    [HttpGet("Update")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<ProTvUpdateInfo>> GetUpdate(
        [FromQuery] bool force = false,
        CancellationToken cancellationToken = default)
        => await ProTvUpdates.CheckAsync(force, cancellationToken);
}
