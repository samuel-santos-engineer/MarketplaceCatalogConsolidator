using System.Globalization;
using MarketplaceCatalogConsolidator.Application.Ports;

namespace MarketplaceCatalogConsolidator.Infrastructure.Storage;

// The caller must hold the existing data-root workflow gate throughout this operation.
public sealed class SqliteLabDatabaseResetStorage(
    IStoragePaths paths, IWorkingDatabaseBootstrapper bootstrapper, IDatabaseMigrator migrator,
    SqliteConnectionFactory connectionFactory) : ILabDatabaseResetStorage
{
    private string MarkerPath => Path.Combine(paths.RootDirectory, ".lab-reset-pending");
    public bool HasPendingReset => File.Exists(MarkerPath);

    public async Task<LabDatabaseResetResult> ResetAsync(CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(paths.RootDirectory);
        var database = ValidateTarget(paths.WorkingDatabasePath, root);
        var uploads = ValidateTarget(paths.UploadDirectory, root);
        var reports = ValidateTarget(paths.ReportDirectory, root);
        var markerPath = ValidateTarget(MarkerPath, root);
        var starter = Path.GetFullPath(paths.StarterDatabasePath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (new[] { database, database + "-wal", database + "-shm", database + "-journal", markerPath }
                .Any(target => string.Equals(starter, target, comparison))
            || starter.StartsWith(uploads + Path.DirectorySeparatorChar, comparison)
            || starter.StartsWith(reports + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("The reset targets must exclude the starter database.");

        Directory.CreateDirectory(root);
        await using (var marker = new FileStream(markerPath, FileMode.Create, FileAccess.Write, FileShare.Read,
            1, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await marker.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
            marker.Flush(flushToDisk: true);
        }

        // A durable marker fences normal work until every reset invariant has been restored.
        DeleteArtifacts(uploads, root);
        DeleteArtifacts(reports, root);
        foreach (var target in new[] { database, database + "-wal", database + "-shm", database + "-journal" })
        {
            ValidateTarget(target, root);
            File.Delete(target);
        }
        await bootstrapper.EnsureWorkingDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await migrator.MigrateAsync(database, cancellationToken).ConfigureAwait(false);
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Product;";
        var products = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        command.CommandText = "SELECT COUNT(*) FROM SellerProduct;";
        var offers = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (products != 975 || offers != 0)
            throw new InvalidOperationException("The restored catalog does not match the lab baseline.");
        Directory.CreateDirectory(uploads);
        Directory.CreateDirectory(reports);
        File.Delete(MarkerPath);
        return new LabDatabaseResetResult(DateTimeOffset.UtcNow, products, offers);
    }

    private static void DeleteArtifacts(string directory, string root)
    {
        if (!Directory.Exists(directory)) return;
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Reset storage must not be linked.");
        // Upload and report stores have a flat layout. Unexpected directories fail closed.
        if (Directory.EnumerateDirectories(directory).Any())
            throw new InvalidOperationException("Unexpected storage layout.");
        foreach (var file in Directory.EnumerateFiles(directory))
            File.Delete(ValidateTarget(file, root));
    }

    private static string ValidateTarget(string target, string root)
    {
        var fullPath = Path.GetFullPath(target);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!fullPath.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("Reset storage must remain within the working root.");
        if (File.Exists(fullPath) && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Reset storage must not be linked.");
        return fullPath;
    }
}
