using System.Text;
using MarketplaceCatalogConsolidator.Application.Parsing;

namespace MarketplaceCatalogConsolidator.UnitTests;

public sealed class SourceDocumentParserTests
{
    [Fact]
    public async Task PreservesArrayIndexesAndRawStringValues()
    {
        const string json = "[{\"Id\":\" A1B2C3D4-E5F6-4A5B-8C9D-0E1F2A3B4C5D \",\"SellerName\":\" Mega  Store \",\"Name\":\"Smartphone  Galaxy S23\",\"Brand\":\"Samsung\",\"Category\":\"Electronics\"},{\"Id\":\"b2c3d4e5-f6a7-4b5c-9d0e-1f2a3b4c5d6e\",\"SellerName\":\"TechWorld\",\"Name\":\"Camera\",\"Brand\":null,\"Category\":\"Photo\"}]";
        var parser = new SourceDocumentParser();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        var entries = await parser.ParseAsync(stream);

        Assert.Equal(2, entries.Count);
        Assert.Equal(0, entries[0].SourceIndex);
        Assert.Equal(" A1B2C3D4-E5F6-4A5B-8C9D-0E1F2A3B4C5D ", entries[0].Id);
        Assert.Equal(" Mega  Store ", entries[0].SellerName);
        Assert.Equal("Smartphone  Galaxy S23", entries[0].Name);
        Assert.Equal(1, entries[1].SourceIndex);
        Assert.Null(entries[1].Brand);
        Assert.Equal("Photo", entries[1].Category);
        Assert.Null(entries[0].ParseError);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    public async Task RejectsMalformedOrNonArrayDocuments(string json)
    {
        var parser = new SourceDocumentParser();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        await Assert.ThrowsAsync<SourceDocumentFormatException>(() => parser.ParseAsync(stream));
    }

    [Fact]
    public async Task MarksMalformedEntriesAndWrongFieldTypesWithoutLosingIndexes()
    {
        var parser = new SourceDocumentParser();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("[null,{\"Id\":true,\"Name\":\"Camera\"}]"));

        var entries = await parser.ParseAsync(stream);

        Assert.Equal(2, entries.Count);
        Assert.Equal(0, entries[0].SourceIndex);
        Assert.NotNull(entries[0].ParseError);
        Assert.Equal(1, entries[1].SourceIndex);
        Assert.Equal("true", entries[1].Id);
        Assert.Contains("Id", entries[1].ParseError ?? string.Empty, StringComparison.Ordinal);
    }
}
