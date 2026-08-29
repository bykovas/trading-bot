using Microsoft.AspNetCore.Mvc;

namespace TradingBot.Web.Controllers;

public sealed class ExportController : Controller
{
    [HttpGet("/export/cycles-and-snapshots.csv")]
    public IActionResult CyclesAndSnapshots() =>
        StatusCode(StatusCodes.Status410Gone, "CSV export is disabled while reports use normalized database tables.");
}
