using Microsoft.AspNetCore.Mvc;
using TradingBot.Web.Data;

namespace TradingBot.Web.Controllers;

public sealed class ExportController(TradingBotReadStore store) : Controller
{
    [HttpGet("/export/cycles-and-snapshots.csv")]
    public async Task<IActionResult> CyclesAndSnapshots(CancellationToken cancellationToken)
    {
        var stream = new MemoryStream();
        await store.WriteCyclesCsvAsync(stream, cancellationToken);
        stream.Position = 0;
        return File(stream, "text/csv; charset=utf-8", $"trading-bot-cycles-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.csv");
    }
}
