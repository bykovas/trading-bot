using Microsoft.AspNetCore.Mvc;
using TradingBot.Web.Data;

namespace TradingBot.Web.Controllers;

public sealed class DecisionController(TradingBotReadStore store) : Controller
{
    [HttpGet("/decision")]
    public async Task<IActionResult> Index(string? botInstanceId, CancellationToken cancellationToken)
    {
        var model = await store.GetCyclesAsync(botInstanceId, 1, 0, false, cancellationToken);
        return View(model);
    }
}
