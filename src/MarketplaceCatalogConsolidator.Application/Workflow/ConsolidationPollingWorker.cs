namespace MarketplaceCatalogConsolidator.Application.Workflow;

public sealed class ConsolidationPollingWorker(ConsolidationWorkflow workflow, TimeSpan idleInterval)
{
    private readonly ConsolidationWorkflow _workflow = workflow ?? throw new ArgumentNullException(nameof(workflow));
    private readonly TimeSpan _idleInterval = idleInterval > TimeSpan.Zero
        ? idleInterval
        : throw new ArgumentOutOfRangeException(nameof(idleInterval));

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_idleInterval);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processed = await _workflow.ProcessNextAsync(cancellationToken).ConfigureAwait(false);
            if (!processed)
            {
                await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
