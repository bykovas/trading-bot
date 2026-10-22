using System.Text.Json;
using TradingBot.Core.Common;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class ArenaPositionProvenanceTests
{
    [Fact]
    public void OpeningFiringSurvivesPortfolioCloneAndSerialization_LegacyPositionsDoNotInventOne()
    {
        var position = new PortfolioPosition { Pair = "DOT/USD", Side = "SHORT", Quantity = 44.8m,
            EntryPrice = 1.115m, Leverage = 10, Origin = PositionOrigins.Bot, DebykoFiringId = "opening-firing" };
        var state = new PortfolioState { Positions = [position] };
        var cloned = state.Clone();
        Assert.Equal("opening-firing", cloned.Positions.Single().DebykoFiringId);
        position.DebykoFiringId = "later-unrelated-firing";
        Assert.Equal("opening-firing", JsonSerializer.Deserialize<PortfolioState>(JsonSerializer.Serialize(cloned))!.Positions.Single().DebykoFiringId);
        Assert.Null(JsonSerializer.Deserialize<PortfolioPosition>("{\"Pair\":\"DOT/USD\",\"Quantity\":1}")!.DebykoFiringId);
    }
}
