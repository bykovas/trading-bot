using System.Text.Json.Nodes;
using TradingBot.Arena;
using Xunit;

namespace TradingBot.Arena.Tests;

public sealed class ArenaCurrentSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly ArenaHeldPosition Bot = new("PF_DOTUSD", "short", 44.8m, 1.115m, Now.AddHours(-5), 10);
    private static JsonObject Native => JsonNode.Parse("""{"openPositions":[{"symbol":"PF_DOTUSD","side":"short","size":"44.8","price":"1.115"},{"symbol":"PF_ETHUSD","side":"long","size":"1","price":"2000"}]}""")!.AsObject();
    [Fact]
    public void NativeFactsAndHistoricalLeveragePreserved_ManualScopeIsNotReported()
    {
        var body = ArenaCurrentSnapshot.Create(Native, [Bot], Now, Now);
        var position = Assert.Single(body["positions"]!.AsArray())!;
        Assert.Equal("live", body["account"]!.ToString()); Assert.Equal("44.8", position["size"]!.ToString());
        Assert.Equal("1.115", position["entry_price"]!.ToString()); Assert.Equal("10", position["leverage"]!.ToString());
        Assert.Equal("PF_DOTUSD", position["instrument"]!.ToString());
        Assert.DoesNotContain("PF_ETHUSD", body.ToJsonString());
    }
    [Fact]
    public void FlatIsAnExplicitEmptyArray_NotAMissingNativeResponse()
    {
        Assert.Empty(ArenaCurrentSnapshot.Create(Native, [], Now, Now)["positions"]!.AsArray());
        Assert.Throws<InvalidDataException>(() => ArenaCurrentSnapshot.Create(new(), [], Now, Now));
    }
    [Theory]
    [InlineData(61)]
    [InlineData(-1)]
    public void NoStaleOrFutureObservation(int seconds) => Assert.Throws<InvalidDataException>(() => ArenaCurrentSnapshot.Create(Native, [Bot], Now.AddSeconds(-seconds), Now));
    [Theory]
    [InlineData("size", "44.7")]
    [InlineData("price", "1.116")]
    [InlineData("side", "long")]
    public void NoModeledMismatchBecomesAFalseFlat(string field, string value)
    {
        var native = Native; native["openPositions"]![0]![field] = value;
        Assert.Throws<InvalidDataException>(() => ArenaCurrentSnapshot.Create(native, [Bot], Now, Now));
    }
    [Fact]
    public void MissingOrAmbiguousHeldListingFailsWholeSnapshot()
    {
        Assert.Throws<InvalidDataException>(() => ArenaCurrentSnapshot.Create(new JsonObject { ["openPositions"] = new JsonArray() }, [Bot], Now, Now));
        Assert.Throws<InvalidDataException>(() => ArenaCurrentSnapshot.Create(Native, [Bot, Bot], Now, Now));
    }
}
