using MarketplaceCatalogConsolidator.Application.Ports;

namespace MarketplaceCatalogConsolidator.Infrastructure.Storage;

public sealed class FileSystemWorkflowLock(IStoragePaths paths) : IWorkflowLock
{
    private readonly string _lockPath = Path.Combine(
        (paths ?? throw new ArgumentNullException(nameof(paths))).RootDirectory,
        ".consolidation.lock");

    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!);
        var retryDelay = TimeSpan.FromMilliseconds(50);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException)
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromMilliseconds(Math.Min(retryDelay.TotalMilliseconds * 2, 500));
            }
        }
    }
}
