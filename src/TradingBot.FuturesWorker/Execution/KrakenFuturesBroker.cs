namespace TradingBot.FuturesWorker;

// TODO(futures): real Kraken Futures adapter (blueprint phase 5). Auth differs
// from Kraken Spot: requests to https://futures.kraken.com/derivatives/api/v3/*
// carry the headers
//   APIKey:  the public api key
//   Authent: Base64(HMAC-SHA512(SHA256(postData + nonce + endpointPath),
//                                Base64Decode(apiSecret)))
// (spot signs uri path + SHA256(nonce + postdata) with API-Key/API-Sign instead).
// Endpoints needed: /instruments (contract specs), /accounts (margin wallets),
// /openpositions, /sendorder (with reduceOnly), /cancelallordersafter (dead
// man's switch), /fills.
//
// This stub exists so the futures worker wiring compiles and runs dry-run today;
// every execution path throws until the adapter lands WITH its safety tests.
internal sealed class KrakenFuturesBroker(HttpClient httpClient, KrakenOptions options) : IFuturesBroker
{
    // Touch the injected dependencies so the scaffold compiles warning-free; the
    // real adapter will use both.
    private readonly HttpClient _httpClient = httpClient;
    private readonly KrakenOptions _options = options;

    public bool IsConfigured => false;

    public Task<IReadOnlyList<FuturesAccountBalance>> GetAccountsAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("Kraken Futures execution is scaffold-only; live adapter not implemented yet.");

    public Task<IReadOnlyList<FuturesOpenPosition>> GetOpenPositionsAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("Kraken Futures execution is scaffold-only; live adapter not implemented yet.");

    public Task<FuturesOrderResult> SendOrderAsync(
        string symbol,
        string side,
        decimal size,
        bool reduceOnly,
        decimal leverage,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Kraken Futures execution is scaffold-only; live adapter not implemented yet.");

    public Task CancelAllAfterAsync(int timeoutSeconds, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Kraken Futures execution is scaffold-only; live adapter not implemented yet.");
}
