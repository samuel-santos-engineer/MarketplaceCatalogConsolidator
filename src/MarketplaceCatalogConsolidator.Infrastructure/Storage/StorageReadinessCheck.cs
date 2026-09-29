using MarketplaceCatalogConsolidator.Application.Ports;
using Microsoft.Data.Sqlite;

namespace MarketplaceCatalogConsolidator.Infrastructure.Storage;

public sealed class StorageReadinessCheck(IStoragePaths paths) : IReadinessCheck
{
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.WorkingDatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 1)
        {
            throw new InvalidOperationException("Foreign key support is unavailable.");
        }
        foreach (var directory in new[] { paths.UploadDirectory, paths.ReportDirectory })
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".readiness-{Guid.NewGuid():N}.tmp");
            await using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            await stream.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
    }
}
