using MarketplaceCatalogConsolidator.Domain;

namespace MarketplaceCatalogConsolidator.Application.Ports;

public interface IPublicReadStore
{
    Task<Page<UploadRecord>> ListUploadsAsync(UploadStatus? status, int page, int pageSize, CancellationToken cancellationToken);
    Task<Page<UploadItemRecord>> ListItemsAsync(Guid uploadId, int page, int pageSize, CancellationToken cancellationToken);
    Task<Page<CatalogResult>> QueryCatalogAsync(string? category, string? brand, string? name, string? sellerName, int page, int pageSize, CancellationToken cancellationToken);
}

public sealed record CatalogResult(long Id, string Name, string? Brand, string? Category, IReadOnlyList<PublicSellerOffer> SellerOffers);
public sealed record PublicSellerOffer(string SellerName, string SellerProductId);

public interface IReadinessCheck
{
    Task CheckAsync(CancellationToken cancellationToken);
}
