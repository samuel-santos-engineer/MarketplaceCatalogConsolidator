using MarketplaceCatalogConsolidator.Domain;

namespace MarketplaceCatalogConsolidator.UnitTests;

public sealed class SqlControlSequencePolicyTests
{
    [Fact]
    public void RejectsSqlKeywordAfterSemicolonWithFieldSpecificReason()
    {
        var reason = SqlControlSequencePolicy.GetRejectionReason("Brand", "TestBrand'; SELECT 1; --");

        Assert.Equal("Brand contains a suspicious SQL control sequence", reason);
    }

    [Theory]
    [InlineData("O'Brian's Cameras")]
    [InlineData("C++ Accessories")]
    [InlineData("Product; name")]
    [InlineData("SELECTOR camera case")]
    public void AcceptsOrdinaryApostrophesAndPunctuation(string value)
    {
        Assert.Null(SqlControlSequencePolicy.GetRejectionReason("Name", value));
    }

    [Theory]
    [InlineData("label -- DROP TABLE x")]
    [InlineData("label /* SELECT */")]
    [InlineData("label UNION text */")]
    public void RejectsSqlCommentMarkerCombinedWithKeyword(string value)
    {
        Assert.NotNull(SqlControlSequencePolicy.GetRejectionReason("Name", value));
    }

    [Fact]
    public void NormalizationCollapsesWhitespaceAndRemovesAccents()
    {
        Assert.Equal("cafe creme", TextNormalization.NormalizeForComparison("  Café\t  Crème  "));
    }
}
