namespace MarketplaceCatalogConsolidator.Domain;

public sealed record SourceProductEntry(
    int SourceIndex,
    string? Id,
    string? SellerName,
    string? Name,
    string? Brand,
    string? Category,
    string? ParseError = null);
