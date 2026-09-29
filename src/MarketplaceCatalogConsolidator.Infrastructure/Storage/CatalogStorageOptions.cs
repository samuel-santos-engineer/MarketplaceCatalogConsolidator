namespace MarketplaceCatalogConsolidator.Infrastructure.Storage;

public sealed record CatalogStorageOptions(string? StorageRoot = null, string? StarterDatabasePath = null)
{
    public static CatalogStorageOptions FromEnvironment() => new(
        Environment.GetEnvironmentVariable("Catalog__StorageRoot"),
        Environment.GetEnvironmentVariable("Catalog__StarterDatabasePath"));
}
