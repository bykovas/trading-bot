using System.Text;
using System.Text.Json;

namespace TradingBot.FuturesWorker;

internal interface ITelegramNotifier
{
    Task SendAsync(string text, CancellationToken cancellationToken);
}

// Posts to one Telegram chat. Everything here is subordinate to one rule: a message that
// cannot be delivered must never disturb trading. Every failure is caught and logged,
// the send has its own short timeout, and nothing upstream waits on the result beyond it.
internal sealed class TelegramNotifier(TelegramNotificationOptions options, HttpClient? httpClient = null)
    : ITelegramNotifier
{
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly HttpClient _http = httpClient ?? Shared;

    public async Task SendAsync(string text, CancellationToken cancellationToken)
    {
        if (!options.IsConfigured || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            // HTML, not MarkdownV2: the posts carry <b> around the figures, and markdown
            // would demand a backslash in front of every dot and dash in a price. The
            // composer escapes the three characters HTML mode cares about.
            var payload = JsonSerializer.Serialize(new { chat_id = options.ChatId, text, parse_mode = "HTML" });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(
                $"https://api.telegram.org/bot{options.BotToken}/sendMessage",
                content,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                // The token is never logged; the body can carry a description but not it.
                Console.WriteLine($"TELEGRAM_SEND_FAILED status={(int)response.StatusCode} body={Trim(body)}");
            }
        }
        catch (Exception error)
        {
            Console.WriteLine($"TELEGRAM_SEND_FAILED error={error.GetType().Name}: {error.Message}");
        }
    }

    private static string Trim(string body) =>
        body.Length <= 300 ? body : body[..300];
}

internal sealed class NullTelegramNotifier : ITelegramNotifier
{
    public Task SendAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;
}
