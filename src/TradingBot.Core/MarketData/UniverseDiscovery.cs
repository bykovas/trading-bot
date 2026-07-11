namespace TradingBot.Core.MarketData;

public interface IUniverseProvider
{
    Task<UniverseSelection> GetUniverseAsync(CancellationToken cancellationToken);
}

public sealed record UniverseSelection(
    IReadOnlyList<InstrumentOptions> Instruments,
    UniverseSelectionDiagnostics Diagnostics);

public sealed record UniverseSelectionDiagnostics(
    string Source,
    int DiscoveredCount,
    int ConfiguredCount,
    int IncludedCount,
    int BlacklistedCount,
    string? Warning = null);

public sealed class ConfiguredUniverseProvider(IReadOnlyList<InstrumentOptions> instruments) : IUniverseProvider
{
    public Task<UniverseSelection> GetUniverseAsync(CancellationToken cancellationToken)
    {
        var enabled = instruments.Where(instrument => instrument.Enabled).ToList();
        return Task.FromResult(new UniverseSelection(
            enabled,
            new UniverseSelectionDiagnostics(
                "configured",
                DiscoveredCount: 0,
                ConfiguredCount: instruments.Count,
                IncludedCount: enabled.Count,
                BlacklistedCount: 0)));
    }
}
