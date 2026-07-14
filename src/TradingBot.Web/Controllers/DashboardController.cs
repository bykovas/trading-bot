using Microsoft.AspNetCore.Mvc;
using TradingBot.Web.Data;

namespace TradingBot.Web.Controllers;

public sealed class DashboardController(TradingBotReadStore store) : Controller
{
    [HttpGet("/")]
    [HttpGet("/dashboard")]
    public async Task<IActionResult> Index(string? botInstanceId, CancellationToken cancellationToken) =>
        View(await store.GetDashboardAsync(botInstanceId, cancellationToken));

    [HttpGet("/error")]
    public IActionResult Error() => View();
}
