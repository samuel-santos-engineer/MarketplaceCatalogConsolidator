namespace MarketplaceCatalogConsolidator.Domain;

public sealed record CatalogProduct(
    long Id,
    string Name,
    string? Brand,
    string? Category,
    string NormalizedName,
    string? NormalizedBrand,
    string? NormalizedCategory);

public sealed record ProductIdentity(string NormalizedBrand, string NormalizedName, string NormalizedCategory);

public sealed record SellerOffer(
    long? Id,
    string SellerName,
    long ProductId,
    string SellerProductId,
    string SourceFingerprint,
    DateTimeOffset CreatedAtUtc);
