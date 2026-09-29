using MarketplaceCatalogConsolidator.Application.Ports;
using System.Text.RegularExpressions;

namespace MarketplaceCatalogConsolidator.Infrastructure.Storage;

public sealed class FileSystemReportFileStore(IStoragePaths paths) : IReportFileStore
{
    private readonly IStoragePaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public string GetReportPath(Guid uploadId) => Path.Combine(_paths.ReportDirectory, $"{uploadId:D}.json");

    public Task<bool> ExistsAsync(Guid uploadId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(GetReportPath(uploadId)));
    }

    public Task<Stream?> OpenReadIfExistsAsync(Guid uploadId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetReportPath(uploadId);
        if (!File.Exists(path))
        {
            return Task.FromResult<Stream?>(null);
        }

        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult<Stream?>(stream);
    }

    public Task WriteAtomicallyAsync(Guid uploadId, Stream content, CancellationToken cancellationToken = default)
    {
        var filePath = GetReportPath(uploadId);
        return AtomicFileWriter.WriteCreateOnlyAsync(filePath, content, cancellationToken);
    }

    public Task<Stream> OpenReadAsync(Guid uploadId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var filePath = GetReportPath(uploadId);
        Stream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public Task CleanupTemporaryFilesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_paths.ReportDirectory))
        {
            return Task.CompletedTask;
        }

        foreach (var path in Directory.EnumerateFiles(_paths.ReportDirectory, ".*.json.*.tmp", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(path);
            if (!IsReportTemporaryFileName(fileName))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (FileNotFoundException)
            {
            }
        }

        return Task.CompletedTask;
    }

    private static bool IsReportTemporaryFileName(string fileName)
    {
        var parts = fileName.Split('.');
        return parts.Length == 5
            && parts[0].Length == 0
            && Guid.TryParseExact(parts[1], "D", out _)
            && parts[2] == "json"
            && Guid.TryParseExact(parts[3], "N", out _)
            && parts[4] == "tmp";
    }
}
