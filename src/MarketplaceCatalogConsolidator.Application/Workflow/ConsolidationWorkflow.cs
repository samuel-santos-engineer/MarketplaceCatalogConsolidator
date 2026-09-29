using MarketplaceCatalogConsolidator.Application.Ports;
using MarketplaceCatalogConsolidator.Domain;

namespace MarketplaceCatalogConsolidator.Application.Workflow;

public sealed class ConsolidationWorkflow(
    IUploadStore uploadStore,
    IUploadItemStore itemStore,
    IUploadItemProcessor itemProcessor,
    IWorkflowLock workflowLock,
    UploadReportFinalizationService reportFinalizationService)
{
    private readonly IUploadStore _uploadStore = uploadStore ?? throw new ArgumentNullException(nameof(uploadStore));
    private readonly IUploadItemStore _itemStore = itemStore ?? throw new ArgumentNullException(nameof(itemStore));
    private readonly IUploadItemProcessor _itemProcessor = itemProcessor ?? throw new ArgumentNullException(nameof(itemProcessor));
    private readonly IWorkflowLock _workflowLock = workflowLock ?? throw new ArgumentNullException(nameof(workflowLock));
    private readonly UploadReportFinalizationService _reportFinalizationService = reportFinalizationService ?? throw new ArgumentNullException(nameof(reportFinalizationService));

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        if (!_itemProcessor.IsAvailable)
        {
            return false;
        }

        await using var lease = await _workflowLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var pendingReports = await _uploadStore.GetReportPendingAsync(cancellationToken).ConfigureAwait(false);
        if (pendingReports.Count > 0)
        {
            return await TryFinalizeReportAsync(pendingReports[0].Id, cancellationToken).ConfigureAwait(false);
        }

        var upload = await _uploadStore.ClaimNextQueuedAsync(cancellationToken).ConfigureAwait(false);
        if (upload is null)
        {
            return false;
        }

        try
        {
            return await ProcessClaimedUploadAsync(upload, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _uploadStore.RequeueProcessingAsync(upload.Id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            var persistedItems = await _itemStore.GetByUploadIdAsync(upload.Id, CancellationToken.None).ConfigureAwait(false);
            var counts = CountOutcomes(persistedItems, upload.ReceivedCount);
            await _uploadStore.BeginReportFinalizationAsync(
                upload.Id,
                UploadStatus.Failed,
                counts.Approved,
                counts.Cleaned,
                counts.Rejected,
                DateTimeOffset.UtcNow,
                "workflow_failed",
                "Upload processing could not be completed.",
                CancellationToken.None).ConfigureAwait(false);
            return await TryFinalizeReportAsync(upload.Id, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<bool> ProcessClaimedUploadAsync(UploadRecord upload, CancellationToken cancellationToken)
    {
        var persisted = await _itemStore.GetByUploadIdAsync(upload.Id, cancellationToken).ConfigureAwait(false);
        var completed = persisted
            .Where(item => item.SourceIndex >= 0 && item.SourceIndex < upload.ReceivedCount)
            .ToDictionary(item => item.SourceIndex);

        for (var sourceIndex = 0; sourceIndex < upload.ReceivedCount; sourceIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (completed.ContainsKey(sourceIndex))
            {
                continue;
            }

            UploadItemProcessingResult result;
            try
            {
                result = await _itemProcessor.ProcessAsync(upload, sourceIndex, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (ItemProcessingException)
            {
                result = new UploadItemProcessingResult(UploadItemStatus.Rejected, "Item rejected during processing.");
            }

            if (string.IsNullOrWhiteSpace(result.ActionTaken))
            {
                throw new InvalidOperationException("The item processor returned an empty action.");
            }

            var item = new UploadItemRecord(
                upload.Id,
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
                result.MatchedProductId);

            if (!result.OutcomePersisted)
            {
                await _itemStore.SaveProgressAsync(item, cancellationToken).ConfigureAwait(false);
            }

            completed.Add(sourceIndex, item);
        }

        var summary = CountOutcomes(completed.Values, upload.ReceivedCount);
        var finalStatus = summary.Rejected > 0 ? UploadStatus.CompletedWithRejections : UploadStatus.Completed;
        var updated = await _uploadStore.BeginReportFinalizationAsync(
            upload.Id,
            finalStatus,
            summary.Approved,
            summary.Cleaned,
            summary.Rejected,
            DateTimeOffset.UtcNow,
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        if (!updated)
        {
            throw new InvalidOperationException("The claimed upload could not be moved to a terminal state.");
        }

        return await TryFinalizeReportAsync(upload.Id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryFinalizeReportAsync(Guid uploadId, CancellationToken cancellationToken)
    {
        try
        {
            return await _reportFinalizationService.FinalizeUnderGateAsync(uploadId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            await _uploadStore.RecordReportGenerationFailureAsync(uploadId, CancellationToken.None).ConfigureAwait(false);
            return false;
        }
    }

    private static OutcomeCounts CountOutcomes(IEnumerable<UploadItemRecord> items, int receivedCount)
    {
        var counts = new OutcomeCounts();
        foreach (var item in items)
        {
            if (item.SourceIndex < 0 || item.SourceIndex >= receivedCount)
            {
                continue;
            }

            switch (item.Status)
            {
                case UploadItemStatus.Approved:
                    counts.Approved++;
                    break;
                case UploadItemStatus.Cleaned:
                    counts.Cleaned++;
                    break;
                case UploadItemStatus.Rejected:
                    counts.Rejected++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(item.Status), item.Status, "Unknown item status.");
            }
        }

        return counts;
    }

    private sealed class OutcomeCounts
    {
        public int Approved { get; set; }
        public int Cleaned { get; set; }
        public int Rejected { get; set; }
    }
}
