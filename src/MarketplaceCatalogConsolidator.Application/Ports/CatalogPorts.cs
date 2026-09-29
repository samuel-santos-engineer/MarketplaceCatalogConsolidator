using MarketplaceCatalogConsolidator.Domain;

namespace MarketplaceCatalogConsolidator.Application.Ports;

public interface IProductCatalog
{
    Task<CatalogProduct?> FindByIdentityAsync(ProductIdentity identity, CancellationToken cancellationToken = default);
    Task<long> CreateAsync(CatalogProduct product, CancellationToken cancellationToken = default);
}

public interface ISellerOfferStore
{
    Task<SellerOffer?> FindBySellerSourceIdAsync(string sellerName, string sellerProductId, CancellationToken cancellationToken = default);
    Task<long> AddAsync(SellerOffer offer, CancellationToken cancellationToken = default);
}
