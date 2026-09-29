using System.Text.Json.Serialization;

namespace MarketplaceCatalogConsolidator.Application.Reports;

public sealed record UploadReportDocument(
    [property: JsonPropertyName("uploadId"), JsonPropertyOrder(0)] string UploadId,
    [property: JsonPropertyName("fileName"), JsonPropertyOrder(1)] string FileName,
    [property: JsonPropertyName("fileHash"), JsonPropertyOrder(2)] string FileHash,
    [property: JsonPropertyName("traceId"), JsonPropertyOrder(3)] string TraceId,
    [property: JsonPropertyName("startedAtUtc"), JsonPropertyOrder(4)] string StartedAtUtc,
    [property: JsonPropertyName("consolidationFinishedAtUtc"), JsonPropertyOrder(5)] string ConsolidationFinishedAtUtc,
    [property: JsonPropertyName("reportGeneratedAtUtc"), JsonPropertyOrder(6)] string ReportGeneratedAtUtc,
    [property: JsonPropertyName("terminalAtUtc"), JsonPropertyOrder(7)] string TerminalAtUtc,
    [property: JsonPropertyName("status"), JsonPropertyOrder(8)] string Status,
    [property: JsonPropertyName("summary"), JsonPropertyOrder(9)] UploadReportSummary Summary,
    [property: JsonPropertyName("failureCode"), JsonPropertyOrder(10)] string? FailureCode,
    [property: JsonPropertyName("failureMessage"), JsonPropertyOrder(11)] string? FailureMessage,
    [property: JsonPropertyName("items"), JsonPropertyOrder(12)] IReadOnlyList<UploadReportItem> Items);

public sealed record UploadReportSummary(
    [property: JsonPropertyName("received"), JsonPropertyOrder(0)] int Received,
    [property: JsonPropertyName("approved"), JsonPropertyOrder(1)] int Approved,
    [property: JsonPropertyName("cleaned"), JsonPropertyOrder(2)] int Cleaned,
    [property: JsonPropertyName("rejected"), JsonPropertyOrder(3)] int Rejected);

public sealed record UploadReportItem(
    [property: JsonPropertyName("sourceIndex"), JsonPropertyOrder(0)] int SourceIndex,
    [property: JsonPropertyName("id"), JsonPropertyOrder(1)] string? Id,
    [property: JsonPropertyName("sellerName"), JsonPropertyOrder(2)] string? SellerName,
    [property: JsonPropertyName("name"), JsonPropertyOrder(3)] string? Name,
    [property: JsonPropertyName("brand"), JsonPropertyOrder(4)] string? Brand,
    [property: JsonPropertyName("category"), JsonPropertyOrder(5)] string? Category,
    [property: JsonPropertyName("status"), JsonPropertyOrder(6)] string Status,
    [property: JsonPropertyName("actionTaken"), JsonPropertyOrder(7)] string ActionTaken,
    [property: JsonPropertyName("cleanedSellerName"), JsonPropertyOrder(8)] string? CleanedSellerName,
    [property: JsonPropertyName("cleanedName"), JsonPropertyOrder(9)] string? CleanedName,
    [property: JsonPropertyName("cleanedBrand"), JsonPropertyOrder(10)] string? CleanedBrand,
    [property: JsonPropertyName("cleanedCategory"), JsonPropertyOrder(11)] string? CleanedCategory,
    [property: JsonPropertyName("canonicalProductId"), JsonPropertyOrder(12)] long? CanonicalProductId);
