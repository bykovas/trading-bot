using Xunit;

namespace TradingBot.FuturesWorker.Tests;

// Cycles land on a fixed wall-clock grid so two workers on the same interval poll the
// market at the same instant. Scheduling from the previous cycle's finish adds its own
// duration to every gap and lets the phase drift: futures-live and futures-lukas-live
// ran 120s apart on paper and 133s in practice, ending up 11 seconds out of step, which
// is how one saw a HYPE breakout the other did not - same score, opposite decision.
public sealed class FuturesCycleAlignmentTests
{
    [Fact]
    public void Two_workers_finishing_at_different_times_get_the_same_next_slot()
    {
        var grid = new DateTimeOffset(2026, 8, 25, 0, 42, 0, TimeSpan.Zero);
        var first = FuturesDecisionWorker.NextAlignedCycleUtc(grid.AddSeconds(16), 120);
        var second = FuturesDecisionWorker.NextAlignedCycleUtc(grid.AddSeconds(27), 120);

        Assert.Equal(first, second);
        Assert.Equal(grid.AddSeconds(120), first);
    }

    // The slot is strictly in the future: landing exactly on a grid point must not
    // return that same instant, or the loop would spin without waiting.
    [Fact]
    public void A_cycle_finishing_exactly_on_a_slot_waits_for_the_next_one()
    {
        var grid = new DateTimeOffset(2026, 8, 25, 0, 42, 0, TimeSpan.Zero);

        Assert.Equal(grid.AddSeconds(120), FuturesDecisionWorker.NextAlignedCycleUtc(grid, 120));
    }

    // An overrunning cycle skips its slot instead of pushing the whole schedule along,
    // which is what kept the two workers drifting further apart the longer they ran.
    [Fact]
    public void An_overrunning_cycle_misses_a_slot_rather_than_shifting_the_grid()
    {
        var grid = new DateTimeOffset(2026, 8, 25, 0, 42, 0, TimeSpan.Zero);

        var next = FuturesDecisionWorker.NextAlignedCycleUtc(grid.AddSeconds(150), 120);

        Assert.Equal(grid.AddSeconds(240), next);
    }

    [Fact]
    public void The_grid_repeats_every_interval_regardless_of_where_it_is_probed()
    {
        var start = new DateTimeOffset(2026, 8, 25, 6, 0, 0, TimeSpan.Zero);
        var slots = Enumerable.Range(0, 30)
            .Select(second => FuturesDecisionWorker.NextAlignedCycleUtc(start.AddSeconds(second * 7), 120))
            .Distinct()
            .OrderBy(slot => slot)
            .ToList();

        Assert.All(slots, slot => Assert.Equal(0, slot.UtcTicks % TimeSpan.FromSeconds(120).Ticks));
    }

    [Fact]
    public void Alignment_is_on_by_default_so_both_instances_share_it_without_configuring_it()
    {
        Assert.True(new FuturesBotConfiguration().Worker.AlignCyclesToClock);
    }
}
