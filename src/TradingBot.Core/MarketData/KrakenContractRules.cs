using System.Globalization;
using System.Text.Json;

namespace TradingBot.Core.MarketData;

public sealed record KrakenContractRules(string Symbol, decimal PriceTick, decimal QuantityStep)
{
    public decimal RoundPrice(decimal price, bool shortEntry) =>
        (shortEntry ? Math.Ceiling(price / PriceTick) : Math.Floor(price / PriceTick)) * PriceTick;
    public decimal RoundQuantity(decimal quantity) => Math.Floor(quantity / QuantityStep) * QuantityStep;
}

public static class KrakenContractMetadata
{
    public static bool Flag(JsonElement row, string name) => row.TryGetProperty(name, out var value)
        && (value.ValueKind == JsonValueKind.True
            || value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var flag) && flag);

    public static bool IsExecutable(JsonElement row) => Flag(row, "tradeable")
        && !Flag(row, "postOnly") && !Flag(row, "suspended");

    public static decimal Decimal(JsonElement row, string name) => row.TryGetProperty(name, out var value)
        && decimal.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0m;

    public static int? Precision(JsonElement row) => row.TryGetProperty("contractValueTradePrecision", out var value)
        && int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

    public static KrakenContractRules? Rules(JsonElement row)
    {
        var symbol = row.GetProperty("symbol").GetString();
        var tick = Decimal(row, "tickSize");
        var precision = Precision(row);
        if (string.IsNullOrWhiteSpace(symbol) || tick <= 0m || precision is null or < -8 or > 12) return null;
        var step = 1m;
        for (var i = 0; i < Math.Abs(precision.Value); i++) step = precision >= 0 ? step / 10m : step * 10m;
        return new(symbol, tick, step);
    }

    public static string Pair(string symbol)
    {
        var asset = symbol[3..^3];
        return (asset is "BTC" or "XBT" ? "XBT" : asset is "XDG" ? "DOGE" : asset) + "/USD";
    }

    public static int DecimalPlaces(decimal value)
    {
        var text = value.ToString("0.############################", CultureInfo.InvariantCulture);
        var dot = text.IndexOf('.');
        return dot < 0 ? 0 : text.Length - dot - 1;
    }
}
