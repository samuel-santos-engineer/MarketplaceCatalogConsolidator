using MarketplaceCatalogConsolidator.Application.Ports;

namespace MarketplaceCatalogConsolidator.Infrastructure.Storage;

public sealed class FileSystemWorkflowLock(IStoragePaths paths) : IWorkflowLock
{
    private readonly string _lockPath = Path.Combine(
        (paths ?? throw new ArgumentNullException(nameof(paths))).RootDirectory,
        ".consolidation.lock");

    public Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default) => AcquireCoreAsync(false, cancellationToken);

    public Task<IAsyncDisposable> AcquireMaintenanceAsync(CancellationToken cancellationToken = default) => AcquireCoreAsync(true, cancellationToken);

    private async Task<IAsyncDisposable> AcquireCoreAsync(bool maintenance, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!);
        var retryDelay = TimeSpan.FromMilliseconds(50);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var lease = new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
                if (!maintenance && File.Exists(Path.Combine(Path.GetDirectoryName(_lockPath)!, ".lab-reset-pending")))
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                    throw new MaintenanceUnavailableException();
                }
                return lease;
            }
            catch (IOException)
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromMilliseconds(Math.Min(retryDelay.TotalMilliseconds * 2, 500));
            }
        }
    }
}
