using Npgsql;

namespace TradingBot.Core.Common;

public enum BotApiCredentialScope
{
    KrakenSpot,
    KrakenFutures
}

public sealed record BotApiCredentials(string ApiKey, string ApiSecret);

public interface IBotApiCredentialStore
{
    Task<BotApiCredentials?> LoadAsync(
        string botInstanceId,
        BotApiCredentialScope scope,
        CancellationToken cancellationToken);
}

public sealed class NullBotApiCredentialStore : IBotApiCredentialStore
{
    public Task<BotApiCredentials?> LoadAsync(
        string botInstanceId,
        BotApiCredentialScope scope,
        CancellationToken cancellationToken) =>
        Task.FromResult<BotApiCredentials?>(null);
}

public sealed class PostgresBotApiCredentialStore : IBotApiCredentialStore
{
    private readonly string _connectionString;

    public PostgresBotApiCredentialStore(string connectionString)
    {
        _connectionString = connectionString;
        EnsureSchema();
    }

    public async Task<BotApiCredentials?> LoadAsync(
        string botInstanceId,
        BotApiCredentialScope scope,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            """
            select api_key, api_secret
            from bot_instance_api_credentials
            where bot_instance_id = @bot_instance_id
              and api_scope = @api_scope
            """,
            connection);
        command.Parameters.AddWithValue("bot_instance_id", botInstanceId);
        command.Parameters.AddWithValue("api_scope", ScopeValue(scope));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new BotApiCredentials(reader.GetString(0), reader.GetString(1))
            : null;
    }

    private void EnsureSchema()
    {
        using var connection = new NpgsqlConnection(_connectionString);
        connection.Open();
        using var command = new NpgsqlCommand(
            """
            create table if not exists bot_instance_api_credentials (
                bot_instance_id text not null,
                api_scope text not null,
                api_key text not null,
                api_secret text not null,
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now(),
                primary key (bot_instance_id, api_scope),
                check (btrim(bot_instance_id) <> ''),
                check (api_scope in ('kraken_spot', 'kraken_futures')),
                check (btrim(api_key) <> ''),
                check (btrim(api_secret) <> '')
            );
            """,
            connection);
        command.ExecuteNonQuery();
    }

    internal static string ScopeValue(BotApiCredentialScope scope) => scope switch
    {
        BotApiCredentialScope.KrakenSpot => "kraken_spot",
        BotApiCredentialScope.KrakenFutures => "kraken_futures",
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported API credential scope.")
    };
}

public sealed class KrakenApiCredentialProvider(
    string botInstanceId,
    BotApiCredentialScope scope,
    KrakenOptions options,
    IBotApiCredentialStore store)
{
    private readonly BotApiCredentials _fallback = new(options.ApiKey, options.ApiSecret);
    private string? _lastSource;
    private string? _lastFailureMessage;

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            var credentials = await store.LoadAsync(botInstanceId, scope, cancellationToken);
            var source = credentials is null ? "environment" : "database";
            var effective = credentials ?? _fallback;
            options.ApiKey = effective.ApiKey;
            options.ApiSecret = effective.ApiSecret;

            if (_lastFailureMessage is not null)
            {
                Console.WriteLine($"api-credentials: instance={botInstanceId} scope={PostgresBotApiCredentialStore.ScopeValue(scope)} refresh recovered");
                _lastFailureMessage = null;
            }

            if (!string.Equals(_lastSource, source, StringComparison.Ordinal))
            {
                Console.WriteLine($"api-credentials: instance={botInstanceId} scope={PostgresBotApiCredentialStore.ScopeValue(scope)} source={source}");
                _lastSource = source;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            if (!string.Equals(_lastFailureMessage, error.Message, StringComparison.Ordinal))
            {
                Console.WriteLine(
                    $"api-credentials: instance={botInstanceId} scope={PostgresBotApiCredentialStore.ScopeValue(scope)} refresh failed; retaining last effective credentials: {error.Message}");
                _lastFailureMessage = error.Message;
            }
        }
    }
}
