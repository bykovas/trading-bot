using System.Text.Json;
using Npgsql;
using Serilog.Core;
using Serilog.Events;

namespace EasyBot.Logging;

/// <summary>
/// Serilog sink that writes every log event into the bot_events table so the dashboard can
/// show recent log activity without tailing files. Never throws back into the logging
/// pipeline - failures are swallowed after being written to stderr.
/// </summary>
public sealed class PostgresBotEventSink : ILogEventSink
{
    private readonly string _connectionString;

    public PostgresBotEventSink(string connectionString) => _connectionString = connectionString;

    public void Emit(LogEvent logEvent)
    {
        _ = EmitAsync(logEvent);
    }

    private async Task EmitAsync(LogEvent logEvent)
    {
        try
        {
            await using var connection = new NpgsqlConnection(_connectionString);
            await connection.OpenAsync();

            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO bot_events (ts, level, message, data) VALUES (@ts, @level, @message, @data::jsonb)";
            command.Parameters.AddWithValue("ts", logEvent.Timestamp.UtcDateTime);
            command.Parameters.AddWithValue("level", logEvent.Level.ToString());
            command.Parameters.AddWithValue("message", logEvent.RenderMessage());

            var properties = logEvent.Properties.Count > 0
                ? JsonSerializer.Serialize(logEvent.Properties.ToDictionary(p => p.Key, p => p.Value.ToString()))
                : null;
            command.Parameters.AddWithValue("data", (object?)properties ?? DBNull.Value);

            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[PostgresBotEventSink] failed to write log event: {ex.Message}");
        }
    }
}

public static class PostgresBotEventSinkExtensions
{
    public static Serilog.LoggerConfiguration BotEventsPostgres(this Serilog.Configuration.LoggerSinkConfiguration sinkConfiguration, string connectionString) =>
        sinkConfiguration.Sink(new PostgresBotEventSink(connectionString));
}
