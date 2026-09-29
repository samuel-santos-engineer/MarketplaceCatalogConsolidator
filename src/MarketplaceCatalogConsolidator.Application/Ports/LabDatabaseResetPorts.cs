namespace MarketplaceCatalogConsolidator.Application.Ports;

public sealed record LabDatabaseResetResult(DateTimeOffset ResetAtUtc, long ProductCount, long SellerProductCount);

public interface ILabDatabaseResetStorage
{
    bool HasPendingReset { get; }
    Task<LabDatabaseResetResult> ResetAsync(CancellationToken cancellationToken = default);
}

public interface ILabDatabaseResetService
{
    Task<LabDatabaseResetResult> ResetAsync(CancellationToken cancellationToken = default);
}

public sealed class LabDatabaseResetException() : Exception("The lab reset could not be completed.");
