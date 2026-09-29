namespace MarketplaceCatalogConsolidator.Domain;

public enum UploadStatus
{
    Queued,
    Processing,
    ReportPending,
    Completed,
    CompletedWithRejections,
    Failed
}

public enum UploadItemStatus
{
    Approved,
    Cleaned,
    Rejected
}

public sealed record UploadRecord(
    Guid Id,
    Guid IdempotencyKey,
    string FileName,
    string FileHash,
    string StagedFilePath,
    string ReportFilePath,
    UploadStatus Status,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int ReceivedCount,
    int ApprovedCount,
    int CleanedCount,
    int RejectedCount,
    Guid TraceId,
    string? FailureCode,
    string? FailureMessage,
    string? ReportSha256,
    UploadStatus? IntendedTerminalStatus = null,
    DateTimeOffset? ConsolidationFinishedAtUtc = null,
    DateTimeOffset? ReportGeneratedAtUtc = null,
    string? ReportGenerationFailureCode = null,
    string? ReportGenerationFailureMessage = null);

public sealed record UploadItemRecord(
    Guid UploadId,
    int SourceIndex,
    string? SourceProductId,
    string? RawSellerName,
    string? RawName,
    string? RawBrand,
    string? RawCategory,
    string? CleanedSellerName,
    string? CleanedName,
    string? CleanedBrand,
    string? CleanedCategory,
    UploadItemStatus Status,
    string ActionTaken,
    long? MatchedProductId);

public sealed record Page<T>(IReadOnlyList<T> Items, int PageNumber, int PageSize, long TotalCount);
