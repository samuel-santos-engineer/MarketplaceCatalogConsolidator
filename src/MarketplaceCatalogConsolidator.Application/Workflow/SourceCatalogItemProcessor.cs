using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Concurrent;
using MarketplaceCatalogConsolidator.Application.Parsing;
using MarketplaceCatalogConsolidator.Application.Ports;
using MarketplaceCatalogConsolidator.Domain;

namespace MarketplaceCatalogConsolidator.Application.Workflow;

public sealed class SourceCatalogItemProcessor(
    IStagingFileStore stagingFileStore,
    IUploadItemStore uploadItemStore,
    IConsolidationItemStore consolidationItemStore,
    SourceDocumentParser parser) : IUploadItemProcessor
{
    private readonly IStagingFileStore _stagingFileStore = stagingFileStore ?? throw new ArgumentNullException(nameof(stagingFileStore));
    private readonly IUploadItemStore _uploadItemStore = uploadItemStore ?? throw new ArgumentNullException(nameof(uploadItemStore));
    private readonly IConsolidationItemStore _consolidationItemStore = consolidationItemStore ?? throw new ArgumentNullException(nameof(consolidationItemStore));
    private readonly SourceDocumentParser _parser = parser ?? throw new ArgumentNullException(nameof(parser));
    private readonly ConcurrentDictionary<Guid, Task<ParsedDocument>> _parsedDocuments = new();

    public bool IsAvailable => true;

    public async Task<UploadItemProcessingResult> ProcessAsync(UploadRecord upload, int sourceIndex, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(upload);

        try
        {
            return await ProcessCoreAsync(upload, sourceIndex, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _parsedDocuments.TryRemove(upload.Id, out _);
            throw;
        }
        finally
        {
            if (sourceIndex >= upload.ReceivedCount - 1)
            {
                _parsedDocuments.TryRemove(upload.Id, out _);
            }
        }
    }

    private async Task<UploadItemProcessingResult> ProcessCoreAsync(UploadRecord upload, int sourceIndex, CancellationToken cancellationToken)
    {
        var document = await GetDocumentAsync(upload.Id, cancellationToken).ConfigureAwait(false);
        if (document.Error is not null)
        {
            return await PersistRejectedAsync(upload.Id, sourceIndex, null, document.Error, cancellationToken).ConfigureAwait(false);
        }

        var entries = document.Entries;
        if (sourceIndex < 0 || sourceIndex >= entries.Count)
        {
            return await PersistRejectedAsync(upload.Id, sourceIndex, null, "The source index is outside the parsed document.", cancellationToken).ConfigureAwait(false);
        }

        var sourceEntry = entries[sourceIndex];
        if (sourceEntry.ParseError is not null)
        {
            return await PersistRejectedAsync(upload.Id, sourceIndex, sourceEntry, sourceEntry.ParseError, cancellationToken).ConfigureAwait(false);
        }

        var suspiciousValue = FindSuspiciousValue(sourceEntry);
        if (suspiciousValue is not null)
        {
            return await PersistRejectedAsync(upload.Id, sourceIndex, sourceEntry, suspiciousValue, cancellationToken).ConfigureAwait(false);
        }

        var sellerName = SourceTextCleaner.Clean(sourceEntry.SellerName);
        if (sellerName is null)
        {
            return await PersistRejectedAsync(upload.Id, sourceIndex, sourceEntry, "SellerName is required.", cancellationToken).ConfigureAwait(false);
        }

        var name = SourceTextCleaner.Clean(sourceEntry.Name);
        if (name is null)
        {
            return await PersistRejectedAsync(upload.Id, sourceIndex, sourceEntry, "Name is required.", cancellationToken).ConfigureAwait(false);
        }

        var sourceProductId = SourceTextCleaner.CleanSourceId(sourceEntry.Id);
        if (sourceProductId is null)
        {
            return await PersistRejectedAsync(upload.Id, sourceIndex, sourceEntry, "Id must be a GUID in D format.", cancellationToken).ConfigureAwait(false);
        }

        var brand = SourceTextCleaner.Clean(sourceEntry.Brand);
        var category = SourceTextCleaner.CleanCategory(sourceEntry.Category);

        var candidate = new ConsolidationCandidate(
            upload.Id,
            sourceEntry,
            sourceProductId,
            sellerName,
            name,
            brand,
            category,
            CreateFingerprint(sourceEntry));

        return await _consolidationItemStore.ConsolidateAsync(candidate, cancellationToken).ConfigureAwait(false);
    }

    private Task<ParsedDocument> GetDocumentAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        var task = _parsedDocuments.GetOrAdd(uploadId, _ => ReadDocumentAsync(uploadId, cancellationToken));
        return task.WaitAsync(cancellationToken);
    }

    private async Task<ParsedDocument> ReadDocumentAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        await using var source = await _stagingFileStore.OpenReadAsync(uploadId, cancellationToken).ConfigureAwait(false);
        try
        {
            return new ParsedDocument(await _parser.ParseAsync(source, cancellationToken).ConfigureAwait(false), null);
        }
        catch (SourceDocumentFormatException exception)
        {
            return new ParsedDocument(Array.Empty<SourceProductEntry>(), exception.Message);
        }
    }

    private async Task<UploadItemProcessingResult> PersistRejectedAsync(
        Guid uploadId,
        int sourceIndex,
        SourceProductEntry? source,
        string reason,
        CancellationToken cancellationToken)
    {
        var result = new UploadItemProcessingResult(
            UploadItemStatus.Rejected,
            reason,
            SourceProductId: SourceTextCleaner.CleanSourceId(source?.Id),
            RawSellerName: source?.SellerName,
            RawName: source?.Name,
            RawBrand: source?.Brand,
            RawCategory: source?.Category,
            CleanedSellerName: SourceTextCleaner.Clean(source?.SellerName),
            CleanedName: SourceTextCleaner.Clean(source?.Name),
            CleanedBrand: SourceTextCleaner.Clean(source?.Brand),
            CleanedCategory: SourceTextCleaner.CleanCategory(source?.Category));

        await SaveOutcomeAsync(uploadId, sourceIndex, result, cancellationToken).ConfigureAwait(false);
        return result with { OutcomePersisted = true };
    }

    private async Task SaveOutcomeAsync(Guid uploadId, int sourceIndex, UploadItemProcessingResult result, CancellationToken cancellationToken)
    {
        await _uploadItemStore.SaveProgressAsync(new UploadItemRecord(
            uploadId,
            sourceIndex,
            result.SourceProductId,
            result.RawSellerName,
            result.RawName,
            result.RawBrand,
            result.RawCategory,
            result.CleanedSellerName,
            result.CleanedName,
            result.CleanedBrand,
            result.CleanedCategory,
            result.Status,
            result.ActionTaken,
            result.MatchedProductId), cancellationToken).ConfigureAwait(false);
    }

    private static string? FindSuspiciousValue(SourceProductEntry source)
    {
        return SqlControlSequencePolicy.GetRejectionReason("Id", source.Id)
            ?? SqlControlSequencePolicy.GetRejectionReason("SellerName", source.SellerName)
            ?? SqlControlSequencePolicy.GetRejectionReason("Name", source.Name)
            ?? SqlControlSequencePolicy.GetRejectionReason("Brand", source.Brand)
            ?? SqlControlSequencePolicy.GetRejectionReason("Category", source.Category);
    }

    private static string CreateFingerprint(SourceProductEntry source)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { source.Id, source.SellerName, source.Name, source.Brand, source.Category });
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private sealed record ParsedDocument(IReadOnlyList<SourceProductEntry> Entries, string? Error);
}
