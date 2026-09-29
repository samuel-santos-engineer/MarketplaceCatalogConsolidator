using System.Text.Json;
using MarketplaceCatalogConsolidator.Application.Workflow;

namespace MarketplaceCatalogConsolidator.UnitTests;

public sealed class SourceTextCleanerTests
{
    [Theory]
    [InlineData("Headphones Sony WH-1000XM5")]
    [InlineData("Monitor LG UltraWide 34\"")]
    [InlineData("Tablet iPad Pro 12.9\"")]
    public void RequestedProductNamesRemainUnchangedAfterJsonRoundTripAndCleaning(string name)
    {
        var decodedName = JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(name));

        Assert.Equal(name, decodedName);
        Assert.Equal(name, SourceTextCleaner.Clean(decodedName));
    }

    [Fact]
    public void PreservesJsonDecodedQuoteAsVisibleAscii()
    {
        var name = JsonSerializer.Deserialize<string>("\"Monitor LG UltraWide 34\\u0022\"");

        Assert.Equal("Monitor LG UltraWide 34\"", SourceTextCleaner.Clean(name));
    }

    [Theory]
    [InlineData("Monitor \"LG\" UltraWide 34\"", "Monitor \"LG\" UltraWide 34\"")]
    [InlineData("\u201cMonitor\u201d LG", "Monitor LG")]
    [InlineData("Mo\u0000ni\u200btor\uFEFF", "Monitor")]
    [InlineData("  Monitor\tLG\r\nUltraWide 34  ", "Monitor LG UltraWide 34")]
    [InlineData("O'Brian's Camera: Compact!", "O'Brian's Camera: Compact!")]
    [InlineData("Caf\u00e9 \U0001F4F7 Camera", "Caf\u00e9 Camera")]
    [InlineData("\u4e2d\u6587 \u0661\u0662\u0663", "\u4e2d\u6587 \u0661\u0662\u0663")]
    [InlineData("Item\u00a9\u00ae\u20ac\u2014\uFF02", "Item")]
    [InlineData("\u200b\u0000 \u201c", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void CleansTextWithoutLosingPrintableContent(string? original, string? expected)
    {
        Assert.Equal(expected, SourceTextCleaner.Clean(original));
    }

    [Fact]
    public void PreservesEveryVisibleAsciiCharacter()
    {
        var visibleAscii = new string(Enumerable.Range(0x21, 0x7E - 0x21 + 1).Select(value => (char)value).ToArray());

        Assert.Equal(visibleAscii, SourceTextCleaner.Clean(visibleAscii));
    }
}
