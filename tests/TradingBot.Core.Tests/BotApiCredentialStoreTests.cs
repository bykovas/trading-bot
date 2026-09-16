using TradingBot.Core.Common;
using Xunit;

namespace TradingBot.Core.Tests;

public sealed class BotApiCredentialStoreTests
{
    [Fact]
    public async Task Database_credentials_replace_environment_credentials()
    {
        var options = new KrakenOptions { ApiKey = "environment-key", ApiSecret = "environment-secret" };
        var store = new MutableCredentialStore
        {
            Credentials = new BotApiCredentials("database-key", "database-secret")
        };
        var provider = new KrakenApiCredentialProvider(
            "futures-live",
            BotApiCredentialScope.KrakenFutures,
            options,
            store);

        await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal("database-key", options.ApiKey);
        Assert.Equal("database-secret", options.ApiSecret);
        Assert.Equal("futures-live", store.LastBotInstanceId);
        Assert.Equal(BotApiCredentialScope.KrakenFutures, store.LastScope);
    }

    [Fact]
    public async Task Missing_database_credentials_restore_the_startup_environment_credentials()
    {
        var options = new KrakenOptions { ApiKey = "environment-key", ApiSecret = "environment-secret" };
        var store = new MutableCredentialStore
        {
            Credentials = new BotApiCredentials("database-key", "database-secret")
        };
        var provider = new KrakenApiCredentialProvider(
            "spot-live",
            BotApiCredentialScope.KrakenSpot,
            options,
            store);

        await provider.RefreshAsync(CancellationToken.None);
        store.Credentials = null;
        await provider.RefreshAsync(CancellationToken.None);

        Assert.Equal("environment-key", options.ApiKey);
        Assert.Equal("environment-secret", options.ApiSecret);
    }

    private sealed class MutableCredentialStore : IBotApiCredentialStore
    {
        public BotApiCredentials? Credentials { get; set; }
        public string? LastBotInstanceId { get; private set; }
        public BotApiCredentialScope? LastScope { get; private set; }

        public Task<BotApiCredentials?> LoadAsync(
            string botInstanceId,
            BotApiCredentialScope scope,
            CancellationToken cancellationToken)
        {
            LastBotInstanceId = botInstanceId;
            LastScope = scope;
            return Task.FromResult(Credentials);
        }
    }
}
