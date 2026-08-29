using Microsoft.AspNetCore.Mvc;
using TradingBot.Web.Data;
using TradingBot.Web.Models;

namespace TradingBot.Web.Controllers;

public sealed class SimulationController(TradingBotReadStore store) : Controller
{
    [HttpGet("/simulation")]
    public async Task<IActionResult> Index(string? botInstanceId, int? lastHours, double? spread, double? score, double? sl, double? tp, double? notional, double? fee, bool? btcFilter, string? exclude, CancellationToken cancellationToken)
    {
        var input = new SimulationInput(Math.Clamp(lastHours ?? 24, 1, 720), spread ?? 0.30, score ?? 0.90, sl ?? 2.5, tp ?? 6.0, notional ?? 10.0, fee ?? 0.35, btcFilter ?? false, exclude ?? string.Empty);
        return View(await store.GetSimulationAsync(botInstanceId, input, cancellationToken));
    }
}
