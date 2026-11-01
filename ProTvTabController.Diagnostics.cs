using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace PROTV.Plugin;

// Admin-only helpers for the PROTV dashboard page.
//   POST /PROTV/TestConnection - tries a login with the values on screen (not saved yet).
//   GET  /PROTV/Diagnostics    - a plain-text report that is safe to paste into a bug report.
public sealed partial class ProTvTabController
{
    [HttpPost("TestConnection")]
    [Authorize(Policy = "RequiresElevation")]
    public async Task<ActionResult<ProTvTestResult>> TestConnection(
        [FromBody] ProTvTestRequest request,
        CancellationToken cancellationToken)
        => await ProTvDiagnostics.TestConnectionAsync(request, cancellationToken);

    [HttpGet("Diagnostics")]
    [Authorize(Policy = "RequiresElevation")]
    public ActionResult GetDiagnostics()
    {
        var host = HttpContext.RequestServices.GetService(typeof(IServerApplicationHost)) as IServerApplicationHost;
        Response.Headers.CacheControl = "no-store";
        return Content(ProTvDiagnostics.BuildReport(host), "text/plain; charset=utf-8");
    }
}
