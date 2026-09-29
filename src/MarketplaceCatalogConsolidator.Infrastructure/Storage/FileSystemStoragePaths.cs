using MarketplaceCatalogConsolidator.Application.Ports;

namespace MarketplaceCatalogConsolidator.Infrastructure.Storage;

public sealed class FileSystemStoragePaths : IStoragePaths
{
    public FileSystemStoragePaths(CatalogStorageOptions? options = null)
    {
        options ??= CatalogStorageOptions.FromEnvironment();
        RootDirectory = Path.GetFullPath(options.StorageRoot ?? GetDefaultStorageRoot());
        StarterDatabasePath = Path.GetFullPath(options.StarterDatabasePath ?? Path.Combine(AppContext.BaseDirectory, "catalog.db"));
        WorkingDatabasePath = Path.Combine(RootDirectory, "catalog.db");
        UploadDirectory = Path.Combine(RootDirectory, "uploads");
        ReportDirectory = Path.Combine(RootDirectory, "reports");
    }

    public string RootDirectory { get; }
    public string StarterDatabasePath { get; }
    public string WorkingDatabasePath { get; }
    public string UploadDirectory { get; }
    public string ReportDirectory { get; }

    private static string GetDefaultStorageRoot()
    {
        if (OperatingSystem.IsLinux())
        {
            return "/home/data";
        }

        var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localApplicationData, "MarketplaceCatalogConsolidator", "data");
    }
}
