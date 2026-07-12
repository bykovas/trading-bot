namespace TradingBot.Core.MarketData;

public static class CandlePairSelector
{
    public static IReadOnlyList<InstrumentOptions> Select(
        string venue,
        IReadOnlyList<InstrumentOptions> universe,
        IReadOnlyList<InstrumentMarketState> lightStates,
        IReadOnlyList<string> heldPairs,
        MarketDataIngestionOptions options)
    {
        var byPair = universe.ToDictionary(instrument => instrument.Pair, StringComparer.OrdinalIgnoreCase);
        var selected = new Dictionary<string, InstrumentOptions>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in heldPairs)
        {
            if (byPair.TryGetValue(pair, out var instrument))
            {
                selected[instrument.Pair] = instrument;
            }
        }

        foreach (var pair in options.ForceInclude)
        {
            var instrument = universe.FirstOrDefault(candidate =>
                candidate.Pair.Equals(pair, StringComparison.OrdinalIgnoreCase)
                || candidate.KrakenPair.Equals(pair, StringComparison.OrdinalIgnoreCase));
            if (instrument is not null)
            {
                selected[instrument.Pair] = instrument;
            }
        }

        var blacklist = options.Blacklist
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var state in lightStates
                     .Where(candidate => candidate.Quote is not null)
                     .OrderByDescending(candidate => candidate.Quote!.VolumeToday * candidate.Quote.Last)
                     .Take(Math.Max(1, options.TopVolumePairs)))
        {
            if (blacklist.Contains(state.Instrument.Pair))
            {
                continue;
            }

            selected[state.Instrument.Pair] = state.Instrument;
        }

        foreach (var state in lightStates
                     .Where(candidate => candidate.Quote is not null)
                     .OrderByDescending(candidate => Math.Abs(candidate.Quote!.ChangePercent ?? 0m))
                     .Take(Math.Max(0, options.TopMoverPairs)))
        {
            if (state.Quote?.ChangePercent is not { } change
                || Math.Abs(change) < options.StrongMoverPercent
                || blacklist.Contains(state.Instrument.Pair))
            {
                continue;
            }

            selected[state.Instrument.Pair] = state.Instrument;
        }

        return selected.Values
            .OrderByDescending(instrument => heldPairs.Contains(instrument.Pair, StringComparer.OrdinalIgnoreCase))
            .ThenByDescending(instrument => IsStrongMover(lightStates, instrument.Pair, options.StrongMoverPercent))
            .ThenByDescending(instrument => AbsChange(lightStates, instrument.Pair))
            .ThenByDescending(instrument => QuoteVolume(lightStates, instrument.Pair))
            .Take(Math.Max(1, options.MaxCandlePairs))
            .ToList();
    }

    private static bool IsStrongMover(IReadOnlyList<InstrumentMarketState> lightStates, string pair, decimal threshold) =>
        AbsChange(lightStates, pair) >= threshold;

    private static decimal AbsChange(IReadOnlyList<InstrumentMarketState> lightStates, string pair)
    {
        var state = lightStates.FirstOrDefault(candidate =>
            candidate.Instrument.Pair.Equals(pair, StringComparison.OrdinalIgnoreCase));
        return state?.Quote is null ? 0m : Math.Abs(state.Quote.ChangePercent ?? 0m);
    }

    private static decimal QuoteVolume(IReadOnlyList<InstrumentMarketState> lightStates, string pair)
    {
        var state = lightStates.FirstOrDefault(candidate =>
            candidate.Instrument.Pair.Equals(pair, StringComparison.OrdinalIgnoreCase));
        return state?.Quote is null ? 0m : state.Quote.VolumeToday * state.Quote.Last;
    }
}
