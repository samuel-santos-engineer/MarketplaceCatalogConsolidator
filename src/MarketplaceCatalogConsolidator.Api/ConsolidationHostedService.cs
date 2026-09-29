using MarketplaceCatalogConsolidator.Application.Workflow;

internal sealed class ConsolidationHostedService(ConsolidationPollingWorker worker) : BackgroundService
{
    private readonly ConsolidationPollingWorker _worker = worker ?? throw new ArgumentNullException(nameof(worker));

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => _worker.RunAsync(stoppingToken);
}
