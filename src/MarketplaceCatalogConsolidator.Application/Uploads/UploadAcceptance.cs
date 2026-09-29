using MarketplaceCatalogConsolidator.Domain;

namespace MarketplaceCatalogConsolidator.Application.Uploads;

public enum UploadAcceptanceFailure
{
    InvalidRequest,
    PayloadTooLarge,
    InvalidJson,
    IdempotencyConflict,
    InsufficientStorage
}

public sealed class UploadAcceptanceException(UploadAcceptanceFailure failure, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public UploadAcceptanceFailure Failure { get; } = failure;
}

public sealed record UploadAcceptanceResult(UploadRecord Upload, bool Created);
