namespace TradingBot.Core.MarketData;

public static class MarketDataVenue
{
    public const string Spot = "spot";
    public const string Futures = "futures";

    public static string Normalize(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            Spot => Spot,
            Futures => Futures,
            _ => Spot
        };

    public static string BotInstancePrefix(string venue) =>
        Normalize(venue) == Futures ? "futures-" : "spot-";
}
