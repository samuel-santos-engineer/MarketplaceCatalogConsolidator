using MarketplaceCatalogConsolidator.Application.Ports;

namespace MarketplaceCatalogConsolidator.Application.Workflow;

public sealed class LabDatabaseResetService(IWorkflowLock workflowLock, ILabDatabaseResetStorage storage) : ILabDatabaseResetService
{
    public async Task<LabDatabaseResetResult> ResetAsync(CancellationToken cancellationToken = default)
    {
        await using var lease = await workflowLock.AcquireMaintenanceAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // Once mutation starts, finish initialization even if the requesting client disconnects.
            return await storage.ResetAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            throw new LabDatabaseResetException();
        }
    }
}
