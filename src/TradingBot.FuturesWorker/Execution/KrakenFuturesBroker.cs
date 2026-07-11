using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradingBot.FuturesWorker;

// Minimal Kraken Derivatives private REST adapter for live futures execution.
// Uses v3 auth (APIKey + Authent) and keeps order semantics deliberately narrow:
// market entries/exits only, and all exits must be reduce-only.
internal sealed class KrakenFuturesBroker(HttpClient httpClient, KrakenOptions options) : IFuturesBroker
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly KrakenOptions _options = options;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.ApiKey)
        && !string.IsNullOrWhiteSpace(_options.ApiSecret);

    public async Task<IReadOnlyList<FuturesAccountBalance>> GetAccountsAsync(CancellationToken cancellationToken)
    {
        using var doc = await SendPrivateAsync(HttpMethod.Get, "/derivatives/api/v3/accounts", Array.Empty<KeyValuePair<string, string>>(), cancellationToken);
        var root = doc.RootElement;
        EnsureSuccess(root, "accounts");

        var balances = new List<FuturesAccountBalance>();
        if (!root.TryGetProperty("accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Object)
        {
            return balances;
        }

        foreach (var account in accounts.EnumerateObject())
        {
            var value = account.Value;
            var currency = GetString(value, "currency")
                ?? GetString(value, "unit")
                ?? account.Name;
            var marginBalance = GetDecimal(value, "marginBalance")
                ?? GetDecimal(value, "balanceValue")
                ?? GetDecimal(value, "portfolioValue")
                ?? GetDecimal(value, "balance")
                ?? 0m;
            var availableMargin = GetDecimal(value, "availableMargin")
                ?? GetDecimal(value, "availableFunds")
                ?? GetDecimal(value, "availableBalance")
                ?? marginBalance;
            balances.Add(new FuturesAccountBalance(currency, marginBalance, availableMargin));
        }

        return balances;
    }

    public async Task<IReadOnlyList<FuturesOpenPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken)
    {
        using var doc = await SendPrivateAsync(HttpMethod.Get, "/derivatives/api/v3/openpositions", Array.Empty<KeyValuePair<string, string>>(), cancellationToken);
        var root = doc.RootElement;
        EnsureSuccess(root, "openpositions");

        var positions = new List<FuturesOpenPosition>();
        if (!root.TryGetProperty("openPositions", out var openPositions) || openPositions.ValueKind != JsonValueKind.Array)
        {
            return positions;
        }

        foreach (var item in openPositions.EnumerateArray())
        {
            var symbol = GetString(item, "symbol") ?? string.Empty;
            var side = (GetString(item, "side") ?? string.Empty).Equals("short", StringComparison.OrdinalIgnoreCase)
                ? "SHORT"
                : "LONG";
            var size = GetDecimal(item, "size") ?? 0m;
            var entry = GetDecimal(item, "price") ?? GetDecimal(item, "entryPrice") ?? 0m;
            var mark = GetDecimal(item, "markPrice") ?? GetDecimal(item, "price") ?? 0m;
            var leverage = GetDecimal(item, "maxFixedLeverage") ?? GetDecimal(item, "leverage") ?? 1m;
            if (!string.IsNullOrWhiteSpace(symbol) && size > 0m)
            {
                positions.Add(new FuturesOpenPosition(symbol, side, size, entry, mark, leverage));
            }
        }

        return positions;
    }

    public async Task<FuturesOrderResult> SendOrderAsync(
        string symbol,
        string side,
        decimal size,
        bool reduceOnly,
        decimal leverage,
        CancellationToken cancellationToken)
    {
        if (size <= 0m)
        {
            return FuturesOrderResult.Rejected("size must be positive");
        }

        var parameters = new List<KeyValuePair<string, string>>
        {
            new("orderType", "mkt"),
            new("symbol", symbol),
            new("side", side.Equals("sell", StringComparison.OrdinalIgnoreCase) ? "sell" : "buy"),
            new("size", FormatDecimal(size)),
            new("reduceOnly", reduceOnly ? "true" : "false"),
            new("cliOrdId", $"tb-{Guid.NewGuid():N}")
        };

        using var doc = await SendPrivateAsync(HttpMethod.Post, "/derivatives/api/v3/sendorder", parameters, cancellationToken);
        var root = doc.RootElement;
        if (!IsSuccess(root))
        {
            return FuturesOrderResult.Rejected(ErrorText(root, "sendorder failed"));
        }

        var sendStatus = root.TryGetProperty("sendStatus", out var statusElement)
            ? statusElement
            : root;
        var status = GetString(sendStatus, "status") ?? "unknown";
        var orderId = GetString(sendStatus, "order_id") ?? GetString(sendStatus, "orderId");
        return new FuturesOrderResult(status, orderId, null);
    }

    public async Task CancelAllAfterAsync(int timeoutSeconds, CancellationToken cancellationToken)
    {
        var parameters = new[]
        {
            new KeyValuePair<string, string>("timeout", Math.Max(1, timeoutSeconds).ToString(CultureInfo.InvariantCulture))
        };
        using var doc = await SendPrivateAsync(HttpMethod.Post, "/derivatives/api/v3/cancelallordersafter", parameters, cancellationToken);
        EnsureSuccess(doc.RootElement, "cancelallordersafter");
    }

    internal string SignForTest(string endpointPath, string nonce, string postData) =>
        Sign(endpointPath, nonce, postData, _options.ApiSecret);

    private async Task<JsonDocument> SendPrivateAsync(
        HttpMethod method,
        string endpointPath,
        IReadOnlyList<KeyValuePair<string, string>> parameters,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("Kraken Futures API key/secret are not configured.");
        }

        var nonce = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var postData = BuildPostData(parameters);
        var uri = $"{_options.BaseUrl.TrimEnd('/')}{endpointPath}";
        HttpContent? content = null;
        if (method == HttpMethod.Get && !string.IsNullOrWhiteSpace(postData))
        {
            uri = $"{uri}?{postData}";
        }
        else if (method != HttpMethod.Get)
        {
            content = new StringContent(postData, Encoding.UTF8, "application/x-www-form-urlencoded");
        }

        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("APIKey", _options.ApiKey);
        request.Headers.Add("Nonce", nonce);
        request.Headers.Add("Authent", Sign(SigningPath(endpointPath), nonce, postData, _options.ApiSecret));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = content;

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Kraken Futures HTTP {(int)response.StatusCode}: {Trim(body)}");
        }

        return JsonDocument.Parse(body);
    }

    private static string Sign(string endpointPath, string nonce, string postData, string apiSecret)
    {
        var payload = Encoding.UTF8.GetBytes(postData + nonce + endpointPath);
        var sha = SHA256.HashData(payload);
        var secret = Convert.FromBase64String(apiSecret);
        using var hmac = new HMACSHA512(secret);
        return Convert.ToBase64String(hmac.ComputeHash(sha));
    }

    private static string BuildPostData(IReadOnlyList<KeyValuePair<string, string>> parameters) =>
        string.Join("&", parameters.Select(parameter =>
            $"{Uri.EscapeDataString(parameter.Key)}={Uri.EscapeDataString(parameter.Value)}"));

    private static string SigningPath(string endpointPath) =>
        endpointPath.StartsWith("/derivatives", StringComparison.Ordinal)
            ? endpointPath["/derivatives".Length..]
            : endpointPath;

    private static bool IsSuccess(JsonElement root) =>
        root.TryGetProperty("result", out var result)
        && result.GetString()?.Equals("success", StringComparison.OrdinalIgnoreCase) == true;

    private static void EnsureSuccess(JsonElement root, string operation)
    {
        if (!IsSuccess(root))
        {
            throw new InvalidOperationException($"Kraken Futures {operation} error: {ErrorText(root, "unknown error")}");
        }
    }

    private static string ErrorText(JsonElement root, string fallback)
    {
        if (root.TryGetProperty("error", out var error))
        {
            return error.ValueKind switch
            {
                JsonValueKind.String => error.GetString() ?? fallback,
                JsonValueKind.Array => string.Join(", ", error.EnumerateArray().Select(item => item.ToString())),
                _ => error.ToString()
            };
        }

        return fallback;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static decimal? GetDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDecimal(out var parsed) => parsed,
            JsonValueKind.String when decimal.TryParse(value.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };
    }

    private static string FormatDecimal(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static string Trim(string value) =>
        value.Length <= 500 ? value : value[..500] + "...";
}
