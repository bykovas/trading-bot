using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TradingBot.Arena;
using Xunit;

namespace TradingBot.Arena.Tests;

public sealed class ArenaCapitalTests
{
    private const string Key = "local-fixture-key";

    private sealed class LocalArena : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public readonly List<(string Method, string Path, string Authorization, string Body)> Requests = [];
        public JsonObject Profile { get; } = new()
        {
            ["handle"] = "omenas", ["bio"] = "Existing owner biography", ["declared_capital"] = "6", ["capital_ccy"] = "USD"
        };
        public int GetStatus { get; set; } = 200;
        public int PutStatus { get; set; } = 202;
        public string? GetBody { get; set; }
        public Uri Origin => new(_app.Urls.Single());

        private LocalArena(WebApplication app)
        {
            _app = app;
            app.MapMethods("/v1/profile", ["GET", "PUT"], async (HttpContext context) =>
            {
                using var reader = new StreamReader(context.Request.Body);
                string body = await reader.ReadToEndAsync();
                Requests.Add((context.Request.Method, context.Request.Path, context.Request.Headers.Authorization.ToString(), body));
                context.Response.ContentType = "application/json";
                if (context.Request.Method == "GET")
                {
                    context.Response.StatusCode = GetStatus;
                    await context.Response.WriteAsync(GetBody ?? Profile.ToJsonString());
                    return;
                }
                var update = JsonNode.Parse(body)!.AsObject();
                Profile["declared_capital"] = update["declared_capital"]!.DeepClone();
                Profile["capital_ccy"] = update["capital_ccy"]!.DeepClone();
                // Model an accepted mutation whose HTTP response can still fail.
                context.Response.StatusCode = PutStatus;
                await context.Response.WriteAsync("{\"accepted\":1,\"updated\":0}");
            });
        }

        public static async Task<LocalArena> StartAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var arena = new LocalArena(builder.Build());
            await arena._app.StartAsync();
            Assert.True(arena.Origin.IsLoopback);
            return arena;
        }

        public ArenaSubmission Client(HttpClient http) => new(http, Key, Origin);
        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private static HttpClient Http() => new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Unset_does_not_read_write_or_clear_existing_capital(string? value)
    {
        await using var arena = await LocalArena.StartAsync();
        using var http = Http();
        Assert.Equal(ArenaCapitalSync.Disabled, await arena.Client(http).SynchronizeDeclaredCapitalAsync(ArenaCapitalConfiguration.Parse(value), default));
        Assert.Empty(arena.Requests);
        Assert.Equal("6", arena.Profile["declared_capital"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1,000")]
    [InlineData("1,5")]
    [InlineData("NaN")]
    [InlineData("1e20")]
    [InlineData("99999999999999999999999999999999999999")]
    public void Invalid_configuration_is_not_a_fallback_to_balance_or_zero(string value) =>
        Assert.Throws<InvalidDataException>(() => ArenaCapitalConfiguration.Parse(value));

    [Fact]
    public async Task Startup_compares_fixed_USD_preserves_profile_and_only_explicit_config_change_writes_again()
    {
        await using var arena = await LocalArena.StartAsync();
        using var http = Http();
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("lt-LT");
            decimal? capital = ArenaCapitalConfiguration.Parse(" 250.50 ");
            Assert.Equal(250.50m, capital);
            Assert.Equal(ArenaCapitalSync.Updated, await arena.Client(http).SynchronizeDeclaredCapitalAsync(capital, default));
            Assert.Equal(new[] { "GET", "PUT" }, arena.Requests.Select(r => r.Method));
            JsonObject sent = JsonNode.Parse(arena.Requests[1].Body)!.AsObject();
            Assert.Equal("250.50", sent["declared_capital"]!.GetValue<string>());
            Assert.Equal("USD", sent["capital_ccy"]!.GetValue<string>());
            Assert.Equal("omenas", sent["handle"]!.GetValue<string>());
            Assert.Equal("Existing owner biography", sent["bio"]!.GetValue<string>());
            Assert.False(sent.ContainsKey("effective_from"));
            Assert.Equal(4, sent.Count);

            arena.Profile["equity"] = 10000; // Unrelated balance/deposit changes are not capital configuration.
            Assert.Equal(ArenaCapitalSync.Unchanged, await arena.Client(http).SynchronizeDeclaredCapitalAsync(capital, default));
            Assert.Single(arena.Requests, r => r.Method == "PUT");
            Assert.Equal(ArenaCapitalSync.Updated, await arena.Client(http).SynchronizeDeclaredCapitalAsync(300, default));
            Assert.Equal(2, arena.Requests.Count(r => r.Method == "PUT"));
            Assert.All(arena.Requests, r =>
            {
                Assert.Equal("/v1/profile", r.Path);
                Assert.Equal("Bearer " + Key, r.Authorization);
                Assert.DoesNotContain(Key, r.Body);
            });
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task No_current_capital_or_different_currency_requires_explicit_configured_declaration(bool cleared)
    {
        await using var arena = await LocalArena.StartAsync();
        arena.Profile["declared_capital"] = cleared ? null : JsonValue.Create(6);
        arena.Profile["capital_ccy"] = cleared ? null : "EUR";
        using var http = Http();
        Assert.Equal(ArenaCapitalSync.Updated, await arena.Client(http).SynchronizeDeclaredCapitalAsync(6, default));
        Assert.Single(arena.Requests, r => r.Method == "PUT");
    }

    [Fact]
    public async Task JSON_number_and_string_amounts_are_compared_numerically()
    {
        await using var arena = await LocalArena.StartAsync();
        using var http = Http();
        foreach (JsonNode value in new JsonNode[] { JsonValue.Create(6.00m)!, JsonValue.Create("6.000")! })
        {
            arena.Profile["declared_capital"] = value;
            Assert.Equal(ArenaCapitalSync.Unchanged, await arena.Client(http).SynchronizeDeclaredCapitalAsync(6, default));
        }
        Assert.All(arena.Requests, r => Assert.Equal("GET", r.Method));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task Failed_read_never_blindly_writes_or_retries(int status)
    {
        await using var arena = await LocalArena.StartAsync();
        arena.GetStatus = status;
        using var http = Http();
        var error = await Assert.ThrowsAsync<ArenaHttpException>(() => arena.Client(http).SynchronizeDeclaredCapitalAsync(100, default));
        Assert.Equal((HttpStatusCode)status, error.Status);
        Assert.Equal("GET", Assert.Single(arena.Requests).Method);
    }

    [Theory]
    [InlineData("{\"handle\":\"omenas\",\"bio\":\"ok\"}")]
    [InlineData("{\"handle\":null,\"bio\":\"ok\",\"declared_capital\":null}")]
    [InlineData("{\"handle\":\"omenas\",\"bio\":\"ok\",\"declared_capital\":0,\"capital_ccy\":\"USD\"}")]
    [InlineData("{\"handle\":\"omenas\",\"bio\":\"ok\",\"declared_capital\":6}")]
    public async Task Incomplete_profile_does_not_invent_a_profile_or_default_amount(string body)
    {
        await using var arena = await LocalArena.StartAsync();
        arena.GetBody = body;
        using var http = Http();
        await Assert.ThrowsAsync<InvalidDataException>(() => arena.Client(http).SynchronizeDeclaredCapitalAsync(100, default));
        Assert.Equal("GET", Assert.Single(arena.Requests).Method);
    }

    [Fact]
    public async Task Ambiguous_PUT_is_not_retried_and_restart_checks_server_state_first()
    {
        await using var arena = await LocalArena.StartAsync();
        arena.PutStatus = 503;
        using var http = Http();
        await Assert.ThrowsAsync<ArenaHttpException>(() => arena.Client(http).SynchronizeDeclaredCapitalAsync(100, default));
        Assert.Equal(new[] { "GET", "PUT" }, arena.Requests.Select(r => r.Method));
        Assert.Equal(ArenaCapitalSync.Unchanged, await arena.Client(http).SynchronizeDeclaredCapitalAsync(100, default));
        Assert.Equal(new[] { "GET", "PUT", "GET" }, arena.Requests.Select(r => r.Method));
    }
}
