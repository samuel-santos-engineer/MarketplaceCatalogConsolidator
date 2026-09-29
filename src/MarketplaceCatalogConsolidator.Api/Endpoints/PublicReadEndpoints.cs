using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using MarketplaceCatalogConsolidator.Api.Contracts;
using MarketplaceCatalogConsolidator.Api.Operations;
using MarketplaceCatalogConsolidator.Application.Ports;
using MarketplaceCatalogConsolidator.Application.Reports;
using MarketplaceCatalogConsolidator.Domain;

namespace MarketplaceCatalogConsolidator.Api.Endpoints;

internal static class PublicReadEndpoints
{
    public static void MapPublicReadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/uploads", ListAsync).WithName("ListUploads")
            .RequireRateLimiting(ApiHardening.ReadPolicy).Produces<ApiErrorResponse>(429).Produces<ApiErrorResponse>(500)
            .WithSummary("List upload attempts, newest first")
            .WithDescription("Optional status enum filter. page defaults to 1; pageSize defaults to 25, maximum 100. Ties use upload ID descending.")
            .Produces<Page<PublicUploadResponse>>().Produces<ApiErrorResponse>(400);
        endpoints.MapGet("/api/v1/uploads/{uploadId}/status", StatusAsync).WithName("UploadStatus")
            .RequireRateLimiting(ApiHardening.ReadPolicy).Produces<ApiErrorResponse>(429).Produces<ApiErrorResponse>(500)
            .WithSummary("Read persisted upload metadata and item outcomes")
            .WithDescription("Items are ordered by source index. page defaults to 1; pageSize defaults to 50, maximum 100.")
            .Produces<UploadStatusResponse>().Produces<ApiErrorResponse>(400).Produces<ApiErrorResponse>(404);
        endpoints.MapGet("/api/v1/uploads/{uploadId}/report", ReportAsync).WithName("UploadReport")
            .RequireRateLimiting(ApiHardening.ReadPolicy).Produces<ApiErrorResponse>(429)
            .WithSummary("Download the verified immutable final JSON report")
            .Produces(200, contentType: "application/json").Produces<ApiErrorResponse>(404)
            .Produces<ApiErrorResponse>(409).Produces<ApiErrorResponse>(500);
        endpoints.MapGet("/api/v1/catalog", CatalogAsync).WithName("QueryCatalog")
            .RequireRateLimiting(ApiHardening.ReadPolicy).Produces<ApiErrorResponse>(429).Produces<ApiErrorResponse>(500)
            .WithSummary("Query canonical products and seller offers")
            .WithDescription("Category, brand and sellerName match whole normalized values; name matches a literal substring. Filters ignore case, accents and repeated whitespace. Seller filtering restricts returned offers. Products use ID ascending. page defaults to 1; pageSize defaults to 25, maximum 100.")
            .Produces<Page<CatalogResult>>().Produces<ApiErrorResponse>(400);
        endpoints.MapGet("/api/v1/health", () => new HealthResponse("healthy")).WithName("Health")
            .RequireRateLimiting(ApiHardening.ReadPolicy).Produces<ApiErrorResponse>(429)
            .WithSummary("Process liveness; performs no dependency checks").Produces<HealthResponse>();
        endpoints.MapGet("/api/v1/ready", ReadyAsync).WithName("Ready")
            .RequireRateLimiting(ApiHardening.ReadPolicy).Produces<ApiErrorResponse>(429)
            .WithSummary("Verify SQLite foreign keys and staging/report writability")
            .Produces<HealthResponse>().Produces<ApiErrorResponse>(503);
    }

    private static async Task<IResult> ListAsync(HttpContext context, IPublicReadStore store, string? status = null, string? page = null, string? pageSize = null)
    {
        if (!Pagination(page, pageSize, 25, out var number, out var size))
            return Error(400, "invalid_pagination", "Page must be positive and pageSize must be between 1 and 100.");
        UploadStatus? filter = null;
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<UploadStatus>(status.Trim(), true, out var parsed) || !Enum.IsDefined(parsed) || !Enum.GetNames<UploadStatus>().Any(value => value.Equals(status.Trim(), StringComparison.OrdinalIgnoreCase)))
                return Error(400, "invalid_status", "Status must be a supported upload state.");
            filter = parsed;
        }
        var result = await store.ListUploadsAsync(filter, number, size, context.RequestAborted);
        return Results.Ok(new Page<PublicUploadResponse>(result.Items.Select(Project).ToArray(), number, size, result.TotalCount));
    }

    private static async Task<IResult> StatusAsync(string uploadId, HttpContext context, IUploadStore uploads, IPublicReadStore store, string? page = null, string? pageSize = null)
    {
        var upload = Guid.TryParseExact(uploadId, "D", out var id) ? await uploads.FindByIdAsync(id, context.RequestAborted) : null;
        if (upload is null) return Error(404, "upload_not_found", "Upload was not found.");
        if (!Pagination(page, pageSize, 50, out var number, out var size))
            return Error(400, "invalid_pagination", "Page must be positive and pageSize must be between 1 and 100.");
        var items = await store.ListItemsAsync(id, number, size, context.RequestAborted);
        var projected = items.Items.Select(item => new UploadReportItem(item.SourceIndex, item.SourceProductId,
            item.RawSellerName, item.RawName, item.RawBrand, item.RawCategory, item.Status.ToString(), item.ActionTaken,
            item.CleanedSellerName, item.CleanedName, item.CleanedBrand, item.CleanedCategory, item.MatchedProductId)).ToArray();
        return Results.Ok(new UploadStatusResponse(Project(upload), upload.TraceId,
            upload.FailureCode is null ? null : upload.FailureCode is "workflow_failed" or "report_generation_failed" ? upload.FailureCode : "workflow_failed", upload.FailureCode is null ? null : "The upload could not be completed.",
            new Page<UploadReportItem>(projected, number, size, items.TotalCount)));
    }

    private static async Task<IResult> ReportAsync(string uploadId, HttpContext context, IUploadStore uploads, IReportFileStore reports, ILoggerFactory loggerFactory)
    {
        var traceId = Guid.NewGuid();
        var upload = Guid.TryParseExact(uploadId, "D", out var id) ? await uploads.FindByIdAsync(id, context.RequestAborted) : null;
        if (upload is null) return Error(404, "upload_not_found", "Upload was not found.");
        if (!Terminal(upload.Status)) return Error(409, "report_not_available", "The final report is not available yet.");
        try
        {
            if (!Available(upload) || !Path.GetFullPath(upload.ReportFilePath).Equals(Path.GetFullPath(reports.GetReportPath(id)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("Invalid final report metadata.");
            await using var source = await reports.OpenReadAsync(id, context.RequestAborted);
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, context.RequestAborted);
            var bytes = buffer.ToArray();
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(upload.ReportSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Report hash mismatch.");
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.GetProperty("uploadId").GetString() != id.ToString("D") || json.RootElement.GetProperty("status").GetString() != upload.Status.ToString())
                throw new InvalidDataException("Report identity mismatch.");
            return Results.File(bytes, "application/json", $"{id:D}.json");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            loggerFactory.CreateLogger("PublicReportDownload").LogError("Final report verification failed for upload {UploadId}, trace {TraceId}, type {ExceptionType}", id, traceId, exception.GetType().Name);
            return Results.Json(new ApiErrorResponse(traceId.ToString("D"), "report_unavailable", "The final report could not be read safely."), statusCode: 500);
        }
    }

    private static async Task<IResult> CatalogAsync(HttpContext context, IPublicReadStore store, string? category = null, string? brand = null, string? name = null, string? sellerName = null, string? page = null, string? pageSize = null)
    {
        if (!Pagination(page, pageSize, 25, out var number, out var size))
            return Error(400, "invalid_pagination", "Page must be positive and pageSize must be between 1 and 100.");
        return Results.Ok(await store.QueryCatalogAsync(category, brand, name, sellerName, number, size, context.RequestAborted));
    }

    private static async Task<IResult> ReadyAsync(HttpContext context, IReadinessCheck check, ILoggerFactory loggerFactory)
    {
        var traceId = Guid.NewGuid();
        try
        {
            await check.CheckAsync(context.RequestAborted);
            return Results.Ok(new HealthResponse("ready"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            loggerFactory.CreateLogger("Readiness").LogWarning("Dependency readiness failed, trace {TraceId}, type {ExceptionType}", traceId, exception.GetType().Name);
            return Results.Json(new ApiErrorResponse(traceId.ToString("D"), "not_ready", "Application dependencies are unavailable."), statusCode: 503);
        }
    }

    private static bool Pagination(string? page, string? pageSize, int defaultSize, out int number, out int size)
    {
        number = 1;
        size = defaultSize;
        return (page is null || int.TryParse(page, NumberStyles.None, CultureInfo.InvariantCulture, out number)) && number > 0
            && (pageSize is null || int.TryParse(pageSize, NumberStyles.None, CultureInfo.InvariantCulture, out size)) && size is >= 1 and <= 100;
    }

    private static bool Terminal(UploadStatus status) => status is UploadStatus.Completed or UploadStatus.CompletedWithRejections or UploadStatus.Failed;
    private static bool Available(UploadRecord upload) => Terminal(upload.Status) && upload.CompletedAtUtc is not null && upload.ReportGeneratedAtUtc is not null && !string.IsNullOrEmpty(upload.ReportFilePath) && !string.IsNullOrEmpty(upload.ReportSha256);
    private static PublicUploadResponse Project(UploadRecord upload) => new(upload.Id, upload.FileName, upload.Status, upload.StartedAtUtc, upload.CompletedAtUtc,
        new UploadReportSummary(upload.ReceivedCount, upload.ApprovedCount, upload.CleanedCount, upload.RejectedCount), Available(upload));
    private static IResult Error(int status, string code, string message) => Results.Json(new ApiErrorResponse(Guid.NewGuid().ToString("D"), code, message), statusCode: status);
}
