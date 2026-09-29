using MarketplaceCatalogConsolidator.Application.Ports;

namespace MarketplaceCatalogConsolidator.Infrastructure.Storage;

public sealed class FileSystemStagingFileStore(IStoragePaths paths) : IStagingFileStore
{
    private readonly IStoragePaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public async Task<string> StageAsync(Guid uploadId, Stream content, CancellationToken cancellationToken = default)
    {
        var filePath = Path.Combine(_paths.UploadDirectory, $"{uploadId:D}.json");
        await AtomicFileWriter.WriteCreateOnlyAsync(filePath, content, cancellationToken).ConfigureAwait(false);
        return filePath;
    }

    public Task<Stream> OpenReadAsync(Guid uploadId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var filePath = Path.Combine(_paths.UploadDirectory, $"{uploadId:D}.json");
        Stream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(Guid uploadId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var filePath = Path.Combine(_paths.UploadDirectory, $"{uploadId:D}.json");
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        return Task.CompletedTask;
    }
}
