using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace PROTV.Plugin;

// Live stream engine endpoint.
//   GET /PROTV/ServiceStatus - whether PROTV is answering right now. Any signed-in user may read it.
public sealed partial class ProTvTabController
{
    [HttpGet("ServiceStatus")]
    [Authorize]
    public ActionResult<ProTvServiceStatus> GetServiceStatus()
    {
        // A status that is a minute old is worse than none, so never let a browser or proxy keep it.
        Response.Headers.CacheControl = "no-store";
        return ProTvSharedClient.Get().GetServiceStatus();
    }
}
