using MarketplaceCatalogConsolidator.Application.Ports;

namespace MarketplaceCatalogConsolidator.Application.Workflow;

public sealed class StartupRecoveryService(IUploadStore uploadStore, IWorkflowLock workflowLock, IReportFileStore reportFileStore, UploadReportFinalizationService reportFinalizationService)
{
    private readonly IUploadStore _uploadStore = uploadStore ?? throw new ArgumentNullException(nameof(uploadStore));
    private readonly IWorkflowLock _workflowLock = workflowLock ?? throw new ArgumentNullException(nameof(workflowLock));
    private readonly IReportFileStore _reportFileStore = reportFileStore ?? throw new ArgumentNullException(nameof(reportFileStore));
    private readonly UploadReportFinalizationService _reportFinalizationService = reportFinalizationService ?? throw new ArgumentNullException(nameof(reportFinalizationService));

    public async Task<int> RecoverAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await _workflowLock.AcquireAsync(cancellationToken).ConfigureAwait(false);
        var recovered = await _uploadStore.RecoverInterruptedAsync(cancellationToken).ConfigureAwait(false);
        await _reportFileStore.CleanupTemporaryFilesAsync(cancellationToken).ConfigureAwait(false);
        var pending = await _uploadStore.GetReportPendingAsync(cancellationToken).ConfigureAwait(false);
        foreach (var upload in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _reportFinalizationService.FinalizeUnderGateAsync(upload.Id, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                await _uploadStore.RecordReportGenerationFailureAsync(upload.Id, CancellationToken.None).ConfigureAwait(false);
            }
        }

        return recovered;
    }
}
