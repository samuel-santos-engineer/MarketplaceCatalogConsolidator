using System.Security.Cryptography;
using System.Text;
using MarketplaceCatalogConsolidator.Application.Parsing;
using MarketplaceCatalogConsolidator.Application.Ports;
using MarketplaceCatalogConsolidator.Domain;

namespace MarketplaceCatalogConsolidator.Application.Uploads;

public sealed class UploadAcceptanceService(
    IUploadStore uploadStore,
    IStagingFileStore stagingFileStore,
    IReportFileStore reportFileStore,
    SourceDocumentParser parser,
    IWorkflowLock? workflowLock = null)
{
    public const int MaximumFileSizeBytes = 500_000;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly IUploadStore _uploadStore = uploadStore ?? throw new ArgumentNullException(nameof(uploadStore));
    private readonly IStagingFileStore _stagingFileStore = stagingFileStore ?? throw new ArgumentNullException(nameof(stagingFileStore));
    private readonly IReportFileStore _reportFileStore = reportFileStore ?? throw new ArgumentNullException(nameof(reportFileStore));
    private readonly SourceDocumentParser _parser = parser ?? throw new ArgumentNullException(nameof(parser));

    public async Task<UploadAcceptanceResult> AcceptAsync(
        Guid idempotencyKey,
        string originalFileName,
        Stream content,
        Guid traceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (idempotencyKey == Guid.Empty)
        {
            throw new ArgumentException("A non-empty idempotency key is required.", nameof(idempotencyKey));
        }

        var payload = await ReadBoundedAsync(content, cancellationToken).ConfigureAwait(false);
        if (payload.Length == 0)
        {
            throw new UploadAcceptanceException(UploadAcceptanceFailure.InvalidRequest, "The uploaded file is empty.");
        }

        try
        {
            _ = StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException exception)
        {
            throw new UploadAcceptanceException(UploadAcceptanceFailure.InvalidJson, "The uploaded file must contain UTF-8 JSON.", exception);
        }

        IReadOnlyList<SourceProductEntry> entries;
        try
        {
            await using var validationStream = new MemoryStream(payload, writable: false);
            entries = await _parser.ParseAsync(validationStream, cancellationToken).ConfigureAwait(false);
        }
        catch (SourceDocumentFormatException exception)
        {
            throw new UploadAcceptanceException(UploadAcceptanceFailure.InvalidJson, exception.Message, exception);
        }

        await using var lease = workflowLock is null ? null : await workflowLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var fileHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var existing = await _uploadStore.FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return MatchExisting(existing, fileHash);
        }

        var uploadId = Guid.NewGuid();
        var staged = false;
        var createAttempted = false;
        try
        {
            await using var stagedContent = new MemoryStream(payload, writable: false);
            var stagedPath = await _stagingFileStore.StageAsync(uploadId, stagedContent, cancellationToken).ConfigureAwait(false);
            staged = true;
            var upload = new UploadRecord(
                uploadId,
                idempotencyKey,
                NormalizeDisplayFileName(originalFileName),
                fileHash,
                stagedPath,
                _reportFileStore.GetReportPath(uploadId),
                UploadStatus.Queued,
                DateTimeOffset.UtcNow,
                null,
                entries.Count,
                0,
                0,
                0,
                traceId,
                null,
                null,
                null);

            createAttempted = true;
            var persisted = await _uploadStore.CreateOrGetByIdempotencyKeyAsync(upload, cancellationToken).ConfigureAwait(false);
            if (!persisted.Created)
            {
                await DeleteStagedAsync(uploadId, cancellationToken).ConfigureAwait(false);
                staged = false;
                return MatchExisting(persisted.Upload, fileHash);
            }

            staged = false;
            return new UploadAcceptanceResult(persisted.Upload, Created: true);
        }
        catch (UploadAcceptanceException)
        {
            if (staged)
            {
                await CleanupUnacceptedStageAsync(uploadId, createAttempted).ConfigureAwait(false);
            }

            throw;
        }
        catch (IOException exception)
        {
            if (staged)
            {
                await CleanupUnacceptedStageAsync(uploadId, createAttempted).ConfigureAwait(false);
            }

            throw new UploadAcceptanceException(UploadAcceptanceFailure.InsufficientStorage, "Persistent upload storage is unavailable.", exception);
        }
        catch
        {
            if (staged)
            {
                await CleanupUnacceptedStageAsync(uploadId, createAttempted).ConfigureAwait(false);
            }

            throw;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream content, CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await content.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read >= MaximumFileSizeBytes)
            {
                throw new UploadAcceptanceException(UploadAcceptanceFailure.PayloadTooLarge, $"The uploaded file must be smaller than {MaximumFileSizeBytes} bytes.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return buffer.ToArray();
    }

    private static UploadAcceptanceResult MatchExisting(UploadRecord existing, string fileHash)
    {
        if (!string.Equals(existing.FileHash, fileHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new UploadAcceptanceException(UploadAcceptanceFailure.IdempotencyConflict, "The Idempotency-Key was already used for different file content.");
        }

        return new UploadAcceptanceResult(existing, Created: false);
    }

    private async Task DeleteStagedAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        try
        {
            await _stagingFileStore.DeleteAsync(uploadId, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
        }
    }

    private async Task CleanupUnacceptedStageAsync(Guid uploadId, bool createAttempted)
    {
        if (createAttempted)
        {
            try
            {
                if (await _uploadStore.FindByIdAsync(uploadId, CancellationToken.None).ConfigureAwait(false) is not null)
                {
                    return;
                }
            }
            catch
            {
                return;
            }
        }

        await DeleteStagedAsync(uploadId, CancellationToken.None).ConfigureAwait(false);
    }

    private static string NormalizeDisplayFileName(string originalFileName)
    {
        if (string.IsNullOrWhiteSpace(originalFileName))
        {
            return "upload.json";
        }

        var portableName = originalFileName.Replace('\\', '/');
        var displayName = portableName[(portableName.LastIndexOf('/') + 1)..];
        var cleaned = new string(displayName.Where(character => !char.IsControl(character)).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "upload.json" : cleaned;
    }
}
