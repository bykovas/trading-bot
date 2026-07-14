using Microsoft.AspNetCore.Mvc;
using TradingBot.Web.Data;

namespace TradingBot.Web.Controllers;

public sealed class MarketSnapshotsController(TradingBotReadStore store) : Controller
{
    [HttpGet("/market-snapshots")]
    public async Task<IActionResult> Index(string? botInstanceId, string? pair, int? limit, int? offset, CancellationToken cancellationToken)
    {
        ViewBag.Pair = pair;
        return View(await store.GetMarketSnapshotsAsync(botInstanceId, pair, Math.Clamp(limit ?? 100, 1, 200), Math.Max(offset ?? 0, 0), cancellationToken));
    }
}
