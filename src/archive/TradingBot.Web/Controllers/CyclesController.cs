using Microsoft.AspNetCore.Mvc;
using TradingBot.Web.Data;

namespace TradingBot.Web.Controllers;

public sealed class CyclesController(TradingBotReadStore store) : Controller
{
    [HttpGet("/cycles")]
    public async Task<IActionResult> Index(string? botInstanceId, int? limit, int? offset, CancellationToken cancellationToken) =>
        View(await store.GetCyclesAsync(botInstanceId, Math.Clamp(limit ?? 50, 1, 200), Math.Max(offset ?? 0, 0), false, cancellationToken));

    [HttpGet("/cycles/{id}")]
    public async Task<IActionResult> Detail(string id, string? botInstanceId, CancellationToken cancellationToken)
    {
        var model = await store.GetCycleDetailAsync(id, botInstanceId, cancellationToken);
        return model is null ? NotFound() : View(model);
    }
}
