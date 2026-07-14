using Microsoft.AspNetCore.Mvc;
using TradingBot.Web.Data;

namespace TradingBot.Web.Controllers;

public sealed class TradesController(TradingBotReadStore store) : Controller
{
    [HttpGet("/trades")]
    public async Task<IActionResult> Index(string? botInstanceId, int? limit, int? offset, CancellationToken cancellationToken) =>
        View(await store.GetCyclesAsync(botInstanceId, Math.Clamp(limit ?? 50, 1, 200), Math.Max(offset ?? 0, 0), true, cancellationToken));
}
