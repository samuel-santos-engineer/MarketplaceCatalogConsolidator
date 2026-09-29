using MarketplaceCatalogConsolidator.Domain;

namespace MarketplaceCatalogConsolidator.Application.Ports;

public interface IUploadStore
{
    Task<UploadRecord?> FindByIdempotencyKeyAsync(Guid idempotencyKey, CancellationToken cancellationToken = default);
    Task<UploadRecord?> FindByIdAsync(Guid uploadId, CancellationToken cancellationToken = default);
    Task<UploadRecord> CreateAsync(UploadRecord upload, CancellationToken cancellationToken = default);
    Task<UploadCreateResult> CreateOrGetByIdempotencyKeyAsync(UploadRecord upload, CancellationToken cancellationToken = default);
    Task<Page<UploadRecord>> ListAsync(int pageNumber, int pageSize, CancellationToken cancellationToken = default);
    Task UpdateAsync(UploadRecord upload, CancellationToken cancellationToken = default);
    Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default);
    Task<UploadRecord?> ClaimNextQueuedAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UploadRecord>> GetReportPendingAsync(CancellationToken cancellationToken = default);
    Task<bool> BeginReportFinalizationAsync(Guid uploadId, UploadStatus intendedTerminalStatus, int approvedCount, int cleanedCount, int rejectedCount, DateTimeOffset consolidationFinishedAtUtc, string? failureCode, string? failureMessage, CancellationToken cancellationToken = default);
    Task<bool> CompleteReportFinalizationAsync(Guid uploadId, string reportFilePath, string reportSha256, DateTimeOffset reportGeneratedAtUtc, DateTimeOffset terminalAtUtc, CancellationToken cancellationToken = default);
    Task<bool> RecordReportGenerationFailureAsync(Guid uploadId, CancellationToken cancellationToken = default);
    Task<bool> RequeueProcessingAsync(Guid uploadId, CancellationToken cancellationToken = default);
}

public sealed record UploadCreateResult(UploadRecord Upload, bool Created);

public interface IUploadItemStore
{
    Task SaveProgressAsync(UploadItemRecord item, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<UploadItemRecord>> GetByUploadIdAsync(Guid uploadId, CancellationToken cancellationToken = default);
}

public sealed record UploadItemProcessingResult(
    UploadItemStatus Status,
    string ActionTaken,
    long? MatchedProductId = null,
    string? SourceProductId = null,
    string? RawSellerName = null,
    string? RawName = null,
    string? RawBrand = null,
    string? RawCategory = null,
    string? CleanedSellerName = null,
    string? CleanedName = null,
    string? CleanedBrand = null,
    string? CleanedCategory = null,
    bool OutcomePersisted = false);

public sealed class ItemProcessingException : Exception
{
    public ItemProcessingException(Exception? innerException = null)
        : base("The source item could not be accepted.", innerException)
    {
    }
}

public interface IUploadItemProcessor
{
    bool IsAvailable { get; }
    Task<UploadItemProcessingResult> ProcessAsync(UploadRecord upload, int sourceIndex, CancellationToken cancellationToken = default);
}
