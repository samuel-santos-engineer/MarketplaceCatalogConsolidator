using MarketplaceCatalogConsolidator.Application.Reports;
using MarketplaceCatalogConsolidator.Domain;

namespace MarketplaceCatalogConsolidator.Api.Contracts;

public sealed record PublicUploadResponse(Guid UploadId, string FileName, UploadStatus Status, DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc, UploadReportSummary Summary, bool ReportAvailable);
public sealed record UploadStatusResponse(PublicUploadResponse Upload, Guid TraceId, string? FailureCode, string? FailureMessage,
    Page<UploadReportItem> Items);
public sealed record HealthResponse(string Status);
