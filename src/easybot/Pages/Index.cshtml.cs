using EasyBot.Data;
using EasyBot.Trading;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace EasyBot.Pages;

public sealed class IndexModel : PageModel
{
    private readonly IExchangeClient _exchange;
    private readonly ITradeRepository _tradeRepository;
    private readonly IBotEventRepository _eventRepository;
    private readonly IAppStateRepository _appStateRepository;
    private readonly BotOptions _options;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        IExchangeClient exchange,
        ITradeRepository tradeRepository,
        IBotEventRepository eventRepository,
        IAppStateRepository appStateRepository,
        IOptions<BotOptions> options,
        ILogger<IndexModel> logger)
    {
        _exchange = exchange;
        _tradeRepository = tradeRepository;
        _eventRepository = eventRepository;
        _appStateRepository = appStateRepository;
        _options = options.Value;
        _logger = logger;
    }

    public string Pair => _options.Pair;
    public ExchangePosition? Position { get; private set; }
    public ExchangeOrder? StopOrder { get; private set; }
    public decimal Equity { get; private set; }
    public IReadOnlyList<TradeRecord> RecentTrades { get; private set; } = [];
    public IReadOnlyList<BotEventRecord> RecentEvents { get; private set; } = [];
    public string Status { get; private set; } = "unknown";
    public bool Paused { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadAsync(ct);
    }

    public async Task<IActionResult> OnPostTogglePauseAsync(CancellationToken ct)
    {
        var current = await _appStateRepository.GetAsync("paused", ct) == "true";
        var next = !current;
        await _appStateRepository.SetAsync("paused", next ? "true" : "false", ct);
        await _eventRepository.LogAsync("info", next ? "Trading paused from dashboard" : "Trading resumed from dashboard", ct: ct);
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Status = await _appStateRepository.GetAsync("status", ct) ?? "unknown";
        Paused = await _appStateRepository.GetAsync("paused", ct) == "true";
        RecentTrades = await _tradeRepository.GetRecentAsync(20, ct);
        RecentEvents = await _eventRepository.GetRecentAsync(50, ct);

        try
        {
            Position = await _exchange.GetOpenPositionAsync(_options.Pair, ct);
            var orders = await _exchange.GetOpenOrdersAsync(_options.Pair, ct);
            StopOrder = orders.FirstOrDefault(o => o.Type == Kraken.Net.Enums.FuturesOrderType.Stop);
            Equity = await _exchange.GetEquityAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Dashboard failed to load live exchange state");
            Status = "error";
        }
    }
}
