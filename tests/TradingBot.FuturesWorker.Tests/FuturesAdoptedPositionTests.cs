using TradingBot.Core.Common;
using Xunit;

namespace TradingBot.FuturesWorker.Tests;

// A position the bot did not order reaches its state as KRAKEN_SYNC, and until now the
// page had to stay silent about all of them: the bot's own record, lost when a container
// died in the seconds before it was saved, is indistinguishable from someone else's
// position - both are simply "not in my state".
//
// The moment of arrival separates them. Before the first exchange sync of a process the
// bot has seen nothing and cannot claim anything. After it, the bot has been watching:
// a position that turns up without it ordering one was opened by a person.
public sealed class FuturesAdoptedPositionTests
{
    [Fact]
    public void The_flag_defaults_to_false_so_silence_is_what_an_unmigrated_row_gets()
    {
        Assert.False(new PortfolioPosition().AdoptedWhileRunning);
    }

    // The flag has to survive the clone the journal takes, or a position would lose the
    // one fact that distinguishes a hand from an unknown on the next save.
    [Fact]
    public void The_flag_survives_a_clone()
    {
        var position = new PortfolioPosition
        {
            Pair = "ETH/USD",
            Side = "LONG",
            Origin = PositionOrigins.KrakenSync,
            AdoptedWhileRunning = true
        };

        var clone = position.Clone();

        Assert.True(clone.AdoptedWhileRunning);
        Assert.Equal(PositionOrigins.KrakenSync, clone.Origin);
    }

    // Origin must NOT change with it. The exit path and the TP/SL orchestrator both key
    // off KRAKEN_SYNC to keep their hands off a position the bot did not open; a new
    // origin value for "a hand opened this" would quietly hand those positions back to
    // the bot to manage.
    [Fact]
    public void Marking_a_hand_does_not_change_the_origin_the_exit_paths_read()
    {
        var position = new PortfolioPosition
        {
            Pair = "ETH/USD",
            Side = "LONG",
            Origin = PositionOrigins.KrakenSync,
            AdoptedWhileRunning = true
        };

        Assert.Equal(PositionOrigins.KrakenSync, position.Origin);
        Assert.NotEqual(PositionOrigins.Bot, position.Origin);
    }

}
