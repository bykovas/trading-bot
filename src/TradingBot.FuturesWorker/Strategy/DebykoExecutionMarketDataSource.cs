namespace TradingBot.FuturesWorker;

internal sealed class DebykoExecutionMarketDataSource(IDebykoMarketClient client, int candleCount = 120) : IMarketDataSource
{
    public Task<IReadOnlyList<InstrumentMarketState>> GetLightMarketStatesAsync(
        IReadOnlyList<InstrumentOptions> instruments, CancellationToken cancellationToken)
        => client.GetLiveStatesAsync(instruments, cancellationToken);

    public async Task<IReadOnlyList<InstrumentMarketState>> GetFullMarketStatesAsync(
        IReadOnlyList<InstrumentOptions> instruments, int timeframeMinutes,
        IReadOnlyList<InstrumentMarketState> lightStates, CancellationToken cancellationToken)
    {
        var states = new List<InstrumentMarketState>();
        var now = DateTimeOffset.UtcNow;
        foreach (var instrument in instruments.Where(x => x.Enabled))
        {
            var minutes = timeframeMinutes == 1 ? 1 : 15;
            var step = TimeSpan.FromMinutes(minutes).Ticks;
            var end = new DateTimeOffset(now.UtcTicks / step * step, TimeSpan.Zero);
            var read = await client.GetExitFrameAsync(instrument.Pair, end, now, minutes, candleCount, cancellationToken);
            states.Add(new InstrumentMarketState
            {
                Instrument = instrument,
                Candles = (minutes == 1 ? read.Frame?.OneMinute : read.Frame?.FifteenMinute) ?? [],
                Quote = lightStates.FirstOrDefault(x => x.Instrument.Pair == instrument.Pair)?.Quote,
                DataWarning = read.Frame is null ? read.Reason : null
            });
        }
        return states;
    }
}
