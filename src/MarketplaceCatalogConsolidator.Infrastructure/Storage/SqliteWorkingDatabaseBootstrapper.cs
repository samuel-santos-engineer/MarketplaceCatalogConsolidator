using MarketplaceCatalogConsolidator.Application.Ports;

namespace MarketplaceCatalogConsolidator.Infrastructure.Storage;

public sealed class SqliteWorkingDatabaseBootstrapper(IStoragePaths paths) : IWorkingDatabaseBootstrapper
{
    private readonly IStoragePaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public async Task<string> EnsureWorkingDatabaseAsync(CancellationToken cancellationToken = default)
    {
        var sourcePath = Path.GetFullPath(_paths.StarterDatabasePath);
        var destinationPath = Path.GetFullPath(_paths.WorkingDatabasePath);
        if (string.Equals(sourcePath, destinationPath, GetPathComparison()))
        {
            throw new InvalidOperationException("The starter database and working database must be different files.");
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The starter catalog database was not found.", sourcePath);
        }

        Directory.CreateDirectory(_paths.RootDirectory);
        if (File.Exists(destinationPath))
        {
            return destinationPath;
        }

        var temporaryPath = Path.Combine(_paths.RootDirectory, $".catalog-bootstrap-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Move(temporaryPath, destinationPath);
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                File.Delete(temporaryPath);
            }

            return destinationPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static StringComparison GetPathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
