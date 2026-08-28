using System.Text;
using System.Text.Json;

namespace TradingBot.FuturesWorker;

internal interface ITelegramNotifier
{
    Task SendAsync(string text, CancellationToken cancellationToken);

    // A 🚨 line for something the bot could not do - a full book, a broken exchange call.
    // Self-throttled so a condition that repeats every cycle posts at most once per window;
    // the reason is the human half of the sentence, the head and the throttle are the
    // notifier's own.
    Task SendAlertAsync(string reason, CancellationToken cancellationToken);
}

// Posts to one Telegram chat. Everything here is subordinate to one rule: a message that
// cannot be delivered must never disturb trading. Every failure is caught and logged,
// the send has its own short timeout, and nothing upstream waits on the result beyond it.
internal sealed class TelegramNotifier(TelegramNotificationOptions options, HttpClient? httpClient = null)
    : ITelegramNotifier
{
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(10) };

    // One 🚨 per half hour, whatever the reason: a full book refuses candidates on every
    // cycle and a broken exchange call can repeat as fast, so an unthrottled alert would
    // bury the channel. The window is shared across reasons - the reader wants to know the
    // bot is stuck, not to be told thirty times which pair it was this minute.
    private static readonly TimeSpan AlertWindow = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http = httpClient ?? Shared;
    private readonly object _alertGate = new();
    private DateTimeOffset _lastAlertUtc = DateTimeOffset.MinValue;

    public Task SendAlertAsync(string reason, CancellationToken cancellationToken)
    {
        if (!options.IsConfigured || string.IsNullOrWhiteSpace(reason))
        {
            return Task.CompletedTask;
        }

        // The throttle is checked and claimed synchronously so a hot per-candidate loop
        // pays nothing after the first alert: only the send that wins the window awaits.
        var now = DateTimeOffset.UtcNow;
        lock (_alertGate)
        {
            if (now - _lastAlertUtc < AlertWindow)
            {
                return Task.CompletedTask;
            }

            _lastAlertUtc = now;
        }

        var head = string.IsNullOrWhiteSpace(options.Emoji) ? "🚨 " : options.Emoji + "🚨 ";
        var label = string.IsNullOrWhiteSpace(options.Label) ? "" : options.Label + " · ";
        return SendAsync(head + label + Escape(reason), cancellationToken);
    }

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

    // HTML parse mode needs these three neutralised; a reason is machine-built text but an
    // exchange message can carry any of them.
    private static string Escape(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

internal sealed class NullTelegramNotifier : ITelegramNotifier
{
    public Task SendAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task SendAlertAsync(string reason, CancellationToken cancellationToken) => Task.CompletedTask;
}
