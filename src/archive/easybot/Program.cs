using CryptoExchange.Net.Authentication;
using EasyBot.Data;
using EasyBot.Logging;
using EasyBot.Trading;
using Kraken.Net.Clients;
using Kraken.Net.Interfaces.Clients;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

var pgConnectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Postgres configuration.");

builder.Host.UseSerilog((context, services, loggerConfiguration) => loggerConfiguration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/easybot-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 30)
    .WriteTo.BotEventsPostgres(pgConnectionString));

builder.Services.AddRazorPages();

builder.Services.Configure<BotOptions>(builder.Configuration.GetSection(BotOptions.SectionName));
builder.Services.Configure<KrakenOptions>(builder.Configuration.GetSection(KrakenOptions.SectionName));

builder.Services.AddSingleton<IDbConnectionFactory, DbConnectionFactory>();
builder.Services.AddSingleton<Migrator>();
builder.Services.AddSingleton<ITradeRepository, TradeRepository>();
builder.Services.AddSingleton<ICandleRepository, CandleRepository>();
builder.Services.AddSingleton<IBotEventRepository, BotEventRepository>();
builder.Services.AddSingleton<IAppStateRepository, AppStateRepository>();

builder.Services.AddSingleton<IKrakenRestClient>(sp =>
{
    var botOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BotOptions>>().Value;
    var krakenOptions = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<KrakenOptions>>().Value;

    return new KrakenRestClient(options =>
    {
        options.ApiCredentials = new Kraken.Net.KrakenCredentials(
            new HMACCredential("", ""),
            new HMACCredential(krakenOptions.FuturesApiKey, krakenOptions.FuturesApiSecret));

        // Demo mode (default) points at Kraken's public Futures demo environment so no real
        // funds are ever at risk until DemoMode is explicitly set to false in configuration.
        options.Environment = botOptions.DemoMode
            ? Kraken.Net.KrakenEnvironment.CreateCustom(
                "futures-demo",
                "https://api.kraken.com",
                "wss://ws.kraken.com",
                "wss://ws-auth.kraken.com/",
                "https://demo-futures.kraken.com",
                "wss://demo-futures.kraken.com/ws/v1")
            : Kraken.Net.KrakenEnvironment.Live;
    });
});

builder.Services.AddSingleton<IExchangeClient, ExchangeClient>();
builder.Services.AddSingleton<BotState>();
builder.Services.AddHostedService<TradingWorker>();

// Dashboard binds to localhost only; there is no authentication in this scaffold.
builder.WebHost.UseUrls("http://127.0.0.1:5080");

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var migrator = scope.ServiceProvider.GetRequiredService<Migrator>();
    await migrator.ApplyAsync();
}

app.UseSerilogRequestLogging();
app.UseStaticFiles();
app.MapRazorPages();

app.Run();
