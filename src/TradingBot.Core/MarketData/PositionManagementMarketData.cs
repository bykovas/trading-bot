namespace TradingBot.Core.MarketData;

public sealed class PositionManagementDataOptions
{
    public string CandlePriceType { get; set; } = "trade";

    public string ValidatedPriceType() => CandlePriceType.Trim().ToLowerInvariant() switch
    {
        "trade" => "trade",
        "mark" => "mark",
        _ => throw new ArgumentException("PositionManagementData.CandlePriceType must be trade or mark.")
    };
}

public sealed record PositionCandleWindow(DateTimeOffset End, DateTimeOffset AsOf, int Minutes)
{
    public const int HistoryBars = 120;

    public static DateTimeOffset NormalizeAsOf(DateTimeOffset time) =>
        DateTimeOffset.FromUnixTimeMilliseconds(time.ToUnixTimeMilliseconds());

    public static PositionCandleWindow At(DateTimeOffset now, int timeframeMinutes)
    {
        var minutes = timeframeMinutes == 1 ? 1 : 15;
        var step = TimeSpan.FromMinutes(minutes).Ticks;
        return new(new DateTimeOffset(now.UtcTicks / step * step, TimeSpan.Zero), NormalizeAsOf(now), minutes);
    }
}

public sealed record PositionCandleRead(IReadOnlyList<Candle> Candles, string? Warning);

public interface IDebykoPositionMarketClient
{
    Task<IReadOnlyList<InstrumentMarketState>> GetLiveStatesAsync(
        IReadOnlyList<InstrumentOptions> instruments, CancellationToken cancellationToken);
    Task<PositionCandleRead> GetPositionCandlesAsync(string pair, PositionCandleWindow window,
        string priceType, CancellationToken cancellationToken);
}

// Both execution modes share this source and window policy. There is no database fallback.
public sealed class DebykoPositionMarketDataSource(
    IDebykoPositionMarketClient client, PositionManagementDataOptions options, IClock? clock = null) : IMarketDataSource
{
    private readonly IClock _clock = clock ?? SystemClock.Instance;
    private readonly string _priceType = options.ValidatedPriceType();

    public Task<IReadOnlyList<InstrumentMarketState>> GetLightMarketStatesAsync(
        IReadOnlyList<InstrumentOptions> instruments, CancellationToken cancellationToken) =>
        client.GetLiveStatesAsync(instruments, cancellationToken);

    public async Task<IReadOnlyList<InstrumentMarketState>> GetFullMarketStatesAsync(
        IReadOnlyList<InstrumentOptions> instruments, int timeframeMinutes,
        IReadOnlyList<InstrumentMarketState> lightStates, CancellationToken cancellationToken)
    {
        var window = PositionCandleWindow.At(_clock.UtcNow, timeframeMinutes);
        var states = new List<InstrumentMarketState>();
        foreach (var instrument in instruments.Where(x => x.Enabled))
        {
            var read = await client.GetPositionCandlesAsync(instrument.Pair, window, _priceType, cancellationToken);
            if (read.Warning is not null)
                Console.WriteLine($"debyko-exit-frame unavailable pair={instrument.Pair} reason={read.Warning}");
            states.Add(new InstrumentMarketState
            {
                Instrument = instrument,
                Candles = read.Candles,
                Quote = lightStates.FirstOrDefault(x => x.Instrument.Pair == instrument.Pair)?.Quote,
                DataWarning = read.Warning
            });
        }
        return states;
    }
}

// Only held-position candles change source; quotes and entry candidates keep their original path.
public sealed class HeldPositionMarketDataSource(
    IMarketDataSource entrySource, IMarketDataSource managementSource,
    Func<IReadOnlySet<string>> heldPairs) : IMarketDataSource
{
    public Task<IReadOnlyList<InstrumentMarketState>> GetLightMarketStatesAsync(
        IReadOnlyList<InstrumentOptions> instruments, CancellationToken cancellationToken) =>
        entrySource.GetLightMarketStatesAsync(instruments, cancellationToken);

    public Task<IReadOnlyList<InstrumentMarketState>> GetFullMarketStatesAsync(
        IReadOnlyList<InstrumentOptions> instruments, int timeframeMinutes,
        IReadOnlyList<InstrumentMarketState> lightStates, CancellationToken cancellationToken) =>
        ReadAsync(instruments, (source, selected) => source.GetFullMarketStatesAsync(
            selected, timeframeMinutes, lightStates, cancellationToken));

    private async Task<IReadOnlyList<InstrumentMarketState>> ReadAsync(IReadOnlyList<InstrumentOptions> instruments,
        Func<IMarketDataSource, IReadOnlyList<InstrumentOptions>, Task<IReadOnlyList<InstrumentMarketState>>> read)
    {
        var held = heldPairs();
        var entries = instruments.Where(x => !held.Contains(x.Pair)).ToArray();
        var positions = instruments.Where(x => held.Contains(x.Pair)).ToArray();
        var states = new List<InstrumentMarketState>();
        if (entries.Length > 0) states.AddRange(await read(entrySource, entries));
        if (positions.Length > 0) states.AddRange(await read(managementSource, positions));
        var byPair = states.ToDictionary(x => x.Instrument.Pair, StringComparer.OrdinalIgnoreCase);
        return instruments.Where(x => byPair.ContainsKey(x.Pair)).Select(x => byPair[x.Pair]).ToArray();
    }
}
