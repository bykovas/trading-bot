using TradingBot.FuturesWorker;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

public sealed class ArenaPaperSourceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-10-26T09:43:13")]
    [InlineData("2999-01-01T00:00:00Z")]
    public void Paper_boundary_must_be_explicit_and_utc(string? value) =>
        Assert.Throws<InvalidDataException>(() => ArenaPaperSource.ParseSince(value));

    [Fact]
    public void Paper_boundary_is_not_inferred_from_current_balance_or_clock() =>
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 9, 43, 13, TimeSpan.Zero), ArenaPaperSource.ParseSince("2026-10-26T09:43:13Z"));

    [Fact]
    public void Live_mode_is_refused_before_constructing_database_or_exchange_clients()
    {
        var config = new FuturesBotConfiguration();
        config.Futures.LiveTradingEnabled = true;
        using var http = new HttpClient();
        Assert.Throws<InvalidOperationException>(() => new ArenaPaperSource(config, http, DateTimeOffset.UnixEpoch));
    }
}
