using MarketplaceCatalogConsolidator.Domain;

namespace MarketplaceCatalogConsolidator.Application.Ports;

public sealed record ConsolidationCandidate(
    Guid UploadId,
    SourceProductEntry Source,
    string SourceProductId,
    string SellerName,
    string Name,
    string? Brand,
    string? Category,
    string SourceFingerprint);

public interface IConsolidationItemStore
{
    Task<UploadItemProcessingResult> ConsolidateAsync(ConsolidationCandidate candidate, CancellationToken cancellationToken = default);
}
