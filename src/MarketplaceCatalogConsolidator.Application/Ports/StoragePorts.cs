namespace MarketplaceCatalogConsolidator.Application.Ports;

public interface IStoragePaths
{
    string RootDirectory { get; }
    string StarterDatabasePath { get; }
    string WorkingDatabasePath { get; }
    string UploadDirectory { get; }
    string ReportDirectory { get; }
}

public interface IWorkingDatabaseBootstrapper
{
    Task<string> EnsureWorkingDatabaseAsync(CancellationToken cancellationToken = default);
}

public interface IDatabaseMigrator
{
    Task MigrateAsync(string databasePath, CancellationToken cancellationToken = default);
}

public interface IStagingFileStore
{
    Task<string> StageAsync(Guid uploadId, Stream content, CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(Guid uploadId, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid uploadId, CancellationToken cancellationToken = default);
}

public interface IReportFileStore
{
    string GetReportPath(Guid uploadId);
    Task<bool> ExistsAsync(Guid uploadId, CancellationToken cancellationToken = default);
    Task<Stream?> OpenReadIfExistsAsync(Guid uploadId, CancellationToken cancellationToken = default);
    Task WriteAtomicallyAsync(Guid uploadId, Stream content, CancellationToken cancellationToken = default);
    Task<Stream> OpenReadAsync(Guid uploadId, CancellationToken cancellationToken = default);
    Task CleanupTemporaryFilesAsync(CancellationToken cancellationToken = default);
}

public interface IWorkflowLock
{
    Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken = default);
    Task<IAsyncDisposable> AcquireMaintenanceAsync(CancellationToken cancellationToken = default) => AcquireAsync(cancellationToken);
}

public sealed class MaintenanceUnavailableException() : Exception("Storage maintenance has not completed.");
