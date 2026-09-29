using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MarketplaceCatalogConsolidator.Application.Ports;
using MarketplaceCatalogConsolidator.Application.Reports;
using MarketplaceCatalogConsolidator.Domain;

namespace MarketplaceCatalogConsolidator.Application.Workflow;

public sealed class UploadReportFinalizationService(
    IUploadStore uploadStore,
    IUploadItemStore itemStore,
    IReportFileStore reportFileStore,
    IStagingFileStore? stagingFileStore = null,
    IWorkflowLock? workflowLock = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly IUploadStore _uploadStore = uploadStore ?? throw new ArgumentNullException(nameof(uploadStore));
    private readonly IUploadItemStore _itemStore = itemStore ?? throw new ArgumentNullException(nameof(itemStore));
    private readonly IReportFileStore _reportFileStore = reportFileStore ?? throw new ArgumentNullException(nameof(reportFileStore));
    private readonly IStagingFileStore? _stagingFileStore = stagingFileStore;

    public async Task<bool> FinalizeAsync(Guid uploadId, CancellationToken cancellationToken = default)
    {
        await using var lease = workflowLock is null ? null : await workflowLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        return await FinalizeUnderGateAsync(uploadId, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> FinalizeUnderGateAsync(Guid uploadId, CancellationToken cancellationToken = default)
    {
        var upload = await _uploadStore.FindByIdAsync(uploadId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Upload '{uploadId:D}' does not exist.");
        var items = await _itemStore.GetByUploadIdAsync(uploadId, cancellationToken).ConfigureAwait(false);
        var reportPath = Path.GetFullPath(_reportFileStore.GetReportPath(uploadId));

        if (IsTerminal(upload.Status))
        {
            await VerifyExistingTerminalReportAsync(upload, items, reportPath, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (upload.Status != UploadStatus.ReportPending || upload.IntendedTerminalStatus is not { } intendedStatus || !IsTerminal(intendedStatus))
        {
            throw new InvalidOperationException("Only a report-pending upload with an intended terminal status can be finalized.");
        }

        var existing = await ReadExistingReportAsync(uploadId, cancellationToken).ConfigureAwait(false);
        byte[] reportBytes;
        DateTimeOffset reportGeneratedAtUtc;
        DateTimeOffset terminalAtUtc;
        if (existing is not null)
        {
            (reportBytes, reportGeneratedAtUtc, terminalAtUtc) = VerifyPendingReport(upload, items, existing);
        }
        else
        {
            reportGeneratedAtUtc = DateTimeOffset.UtcNow;
            terminalAtUtc = DateTimeOffset.UtcNow;
            var document = CreateDocument(upload, items, intendedStatus, reportGeneratedAtUtc, terminalAtUtc);
            reportBytes = JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions);
            await using var reportContent = new MemoryStream(reportBytes, writable: false);
            try
            {
                await _reportFileStore.WriteAtomicallyAsync(uploadId, reportContent, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                var racedReport = await ReadExistingReportAsync(uploadId, cancellationToken).ConfigureAwait(false);
                if (racedReport is null)
                {
                    throw;
                }

                (reportBytes, reportGeneratedAtUtc, terminalAtUtc) = VerifyPendingReport(upload, items, racedReport);
            }
        }

        var publishedBytes = await ReadExistingReportAsync(uploadId, cancellationToken).ConfigureAwait(false);
        if (publishedBytes is null || !await _reportFileStore.ExistsAsync(uploadId, cancellationToken).ConfigureAwait(false))
        {
            throw new IOException("The published report could not be verified on disk.");
        }

        var publishedReport = DeserializeReport(publishedBytes);
        var expectedPublishedReport = CreateDocument(
            upload,
            items,
            intendedStatus,
            ParseUtcTimestamp(publishedReport.ReportGeneratedAtUtc, "reportGeneratedAtUtc"),
            ParseUtcTimestamp(publishedReport.TerminalAtUtc, "terminalAtUtc"));
        VerifyDocumentBytes(publishedBytes, expectedPublishedReport);
        var reportHash = Convert.ToHexString(SHA256.HashData(publishedBytes)).ToLowerInvariant();

        if (_stagingFileStore is not null)
        {
            await _stagingFileStore.DeleteAsync(uploadId, cancellationToken).ConfigureAwait(false);
        }

        var finalized = await _uploadStore.CompleteReportFinalizationAsync(
            uploadId,
            reportPath,
            reportHash,
            reportGeneratedAtUtc,
            terminalAtUtc,
            cancellationToken).ConfigureAwait(false);
        if (!finalized)
        {
            throw new InvalidOperationException("The upload could not be finalized after report publication.");
        }

        return true;
    }

    private async Task VerifyExistingTerminalReportAsync(
        UploadRecord upload,
        IReadOnlyList<UploadItemRecord> items,
        string reportPath,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetFullPath(upload.ReportFilePath), reportPath, PathComparison())
            || upload.ReportSha256 is null
            || upload.ReportGeneratedAtUtc is null
            || upload.CompletedAtUtc is null)
        {
            throw new InvalidDataException("A terminal upload has incomplete report metadata.");
        }

        var bytes = await ReadExistingReportAsync(upload.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new FileNotFoundException("The immutable report for a terminal upload is missing.", reportPath);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(hash, upload.ReportSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The immutable report hash does not match its persisted metadata.");
        }

        var report = DeserializeReport(bytes);
        var expected = CreateDocument(upload, items, upload.Status, upload.ReportGeneratedAtUtc.Value, upload.CompletedAtUtc.Value);
        VerifyDocumentBytes(bytes, expected);
        _ = report;
    }

    private (byte[] Bytes, DateTimeOffset ReportGeneratedAtUtc, DateTimeOffset TerminalAtUtc) VerifyPendingReport(
        UploadRecord upload,
        IReadOnlyList<UploadItemRecord> items,
        byte[] bytes)
    {
        var existing = DeserializeReport(bytes);
        var reportGeneratedAtUtc = ParseUtcTimestamp(existing.ReportGeneratedAtUtc, "reportGeneratedAtUtc");
        var terminalAtUtc = ParseUtcTimestamp(existing.TerminalAtUtc, "terminalAtUtc");
        var expected = CreateDocument(upload, items, upload.IntendedTerminalStatus!.Value, reportGeneratedAtUtc, terminalAtUtc);
        VerifyDocumentBytes(bytes, expected);
        return (bytes, reportGeneratedAtUtc, terminalAtUtc);
    }

    private async Task<byte[]?> ReadExistingReportAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        await using var stream = await _reportFileStore.OpenReadIfExistsAsync(uploadId, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return null;
        }

        await using var content = new MemoryStream();
        await stream.CopyToAsync(content, cancellationToken).ConfigureAwait(false);
        return content.ToArray();
    }

    private static UploadReportDocument DeserializeReport(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<UploadReportDocument>(bytes, SerializerOptions)
                ?? throw new InvalidDataException("The report JSON document is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The existing report is not valid JSON for this contract.", exception);
        }
    }

    private static void VerifyDocumentBytes(byte[] actual, UploadReportDocument expected)
    {
        var expectedBytes = JsonSerializer.SerializeToUtf8Bytes(expected, SerializerOptions);
        if (!actual.AsSpan().SequenceEqual(expectedBytes))
        {
            throw new InvalidDataException("The existing immutable report does not match durable upload data.");
        }
    }

    private static UploadReportDocument CreateDocument(
        UploadRecord upload,
        IReadOnlyList<UploadItemRecord> items,
        UploadStatus terminalStatus,
        DateTimeOffset reportGeneratedAtUtc,
        DateTimeOffset terminalAtUtc)
    {
        if (upload.ConsolidationFinishedAtUtc is not { } finishedAt)
        {
            throw new InvalidDataException("The report-pending upload is missing its consolidation-finished timestamp.");
        }

        var summary = new UploadReportSummary(upload.ReceivedCount, upload.ApprovedCount, upload.CleanedCount, upload.RejectedCount);
        var reportItems = items
            .OrderBy(item => item.SourceIndex)
            .Select(item => new UploadReportItem(
                item.SourceIndex,
                item.SourceProductId,
                item.RawSellerName,
                item.RawName,
                item.RawBrand,
                item.RawCategory,
                item.Status.ToString(),
                item.ActionTaken,
                item.CleanedSellerName,
                item.CleanedName,
                item.CleanedBrand,
                item.CleanedCategory,
                item.MatchedProductId))
            .ToArray();

        var failed = terminalStatus == UploadStatus.Failed;
        return new UploadReportDocument(
            upload.Id.ToString("D"),
            upload.FileName,
            upload.FileHash,
            upload.TraceId.ToString("D"),
            FormatUtc(upload.StartedAtUtc),
            FormatUtc(finishedAt),
            FormatUtc(reportGeneratedAtUtc),
            FormatUtc(terminalAtUtc),
            terminalStatus.ToString(),
            summary,
            failed ? SafeFailureCode(upload.FailureCode) : null,
            failed ? "Upload processing could not be completed." : null,
            reportItems);
    }

    private static DateTimeOffset ParseUtcTimestamp(string timestamp, string fieldName)
    {
        if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            || value.Offset != TimeSpan.Zero
            || !string.Equals(FormatUtc(value), timestamp, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The existing report has an invalid {fieldName} timestamp.");
        }

        return value;
    }

    private static string FormatUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string SafeFailureCode(string? failureCode) => failureCode is "workflow_failed" or "report_generation_failed"
        ? failureCode
        : "workflow_failed";

    private static bool IsTerminal(UploadStatus status) => status is UploadStatus.Completed or UploadStatus.CompletedWithRejections or UploadStatus.Failed;

    private static StringComparison PathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
