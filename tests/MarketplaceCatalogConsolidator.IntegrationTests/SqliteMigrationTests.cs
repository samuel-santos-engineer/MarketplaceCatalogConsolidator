using System.Security.Cryptography;
using MarketplaceCatalogConsolidator.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace MarketplaceCatalogConsolidator.IntegrationTests;

public sealed class SqliteMigrationTests
{
    private static readonly Guid LegacyGuidSellerProductId = Guid.Parse("a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d");

    [Fact]
    public async Task BootstrapCopiesStarterAndMigrationPreservesDataAndIsIdempotent()
    {
        using var fixture = new TemporaryCatalog();
        var starterHash = SHA256.HashData(await File.ReadAllBytesAsync(fixture.StarterPath));
        var bootstrapper = new SqliteWorkingDatabaseBootstrapper(fixture.Paths);
        var workingPath = await bootstrapper.EnsureWorkingDatabaseAsync();

        Assert.NotEqual(Path.GetFullPath(fixture.StarterPath), Path.GetFullPath(workingPath));
        Assert.Equal(starterHash, SHA256.HashData(await File.ReadAllBytesAsync(workingPath)));

        var factory = new SqliteConnectionFactory(workingPath);
        await using (var legacyConnection = await factory.OpenConnectionAsync())
        {
            await using var insertCommand = legacyConnection.CreateCommand();
            insertCommand.CommandText = "INSERT INTO SellerProduct (SellerName, ProductId, SellerProductId) VALUES ($seller, (SELECT Id FROM Product ORDER BY Id LIMIT 1), $sourceId);";
            insertCommand.Parameters.AddWithValue("$seller", "MigrationTestSeller");
            insertCommand.Parameters.AddWithValue("$sourceId", LegacyGuidSellerProductId.ToString("D"));
            Assert.Equal(1, await insertCommand.ExecuteNonQueryAsync());

            await using var numericInsertCommand = legacyConnection.CreateCommand();
            numericInsertCommand.CommandText = "INSERT INTO SellerProduct (SellerName, ProductId, SellerProductId) VALUES ($seller, (SELECT Id FROM Product ORDER BY Id LIMIT 1), $sourceId);";
            numericInsertCommand.Parameters.AddWithValue("$seller", "LegacyNumericSeller");
            numericInsertCommand.Parameters.AddWithValue("$sourceId", 54321);
            Assert.Equal(1, await numericInsertCommand.ExecuteNonQueryAsync());
        }

        var migrator = new SqliteDatabaseMigrator();
        await migrator.MigrateAsync(workingPath);
        await migrator.MigrateAsync(workingPath);

        await using (var migratedConnection = await factory.OpenConnectionAsync())
        {
            Assert.Equal(975, await ReadInt64Async(migratedConnection, "SELECT COUNT(*) FROM Product;"));
            Assert.Equal(0, await ReadInt64Async(migratedConnection, "SELECT COUNT(*) FROM Product WHERE NormalizedName = '' OR (Brand IS NOT NULL AND NormalizedBrand IS NULL) OR (Category IS NOT NULL AND NormalizedCategory IS NULL);"));
            Assert.Equal(2, await ReadInt64Async(migratedConnection, "SELECT COUNT(*) FROM SellerProduct;"));
            Assert.Equal(3, await ReadInt64Async(migratedConnection, "SELECT COUNT(*) FROM SchemaMigration;"));
            Assert.Equal(1, await ReadInt64Async(migratedConnection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Upload';"));
            Assert.Equal(1, await ReadInt64Async(migratedConnection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'UploadItem';"));
            Assert.Equal(1, await ReadInt64Async(migratedConnection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_Product_NormalizedIdentity';"));
            Assert.Equal(1, await ReadInt64Async(migratedConnection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_SellerProduct_ProductId';"));
            Assert.Equal(3, await ReadInt64Async(migratedConnection, "SELECT COUNT(*) FROM pragma_table_info('Product') WHERE name IN ('NormalizedName', 'NormalizedBrand', 'NormalizedCategory');"));
            Assert.Equal(22, await ReadInt64Async(migratedConnection, "SELECT COUNT(*) FROM pragma_table_info('Upload');"));
            Assert.Equal(14, await ReadInt64Async(migratedConnection, "SELECT COUNT(*) FROM pragma_table_info('UploadItem');"));
            Assert.Equal("TEXT", await ReadTextAsync(migratedConnection, "SELECT type FROM pragma_table_info('SellerProduct') WHERE name = 'SellerProductId';"));
            Assert.Equal(1, await ReadInt64Async(migratedConnection, "SELECT COUNT(*) FROM pragma_table_info('SellerProduct') WHERE name = 'SellerProductId' AND [notnull] = 1;"));
            Assert.Equal(1, await ReadInt64Async(migratedConnection, "SELECT COUNT(*) FROM pragma_index_list('SellerProduct') WHERE [unique] = 1;"));

            await using var sellerIdsCommand = migratedConnection.CreateCommand();
            sellerIdsCommand.CommandText = "SELECT Id, SellerName, ProductId, SellerProductId, typeof(SellerProductId) FROM SellerProduct ORDER BY SellerName;";
            await using var reader = await sellerIdsCommand.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(2, reader.GetInt64(0));
            Assert.Equal("LegacyNumericSeller", reader.GetString(1));
            Assert.Equal(1, reader.GetInt64(2));
            Assert.Equal("54321", reader.GetString(3));
            Assert.Equal("text", reader.GetString(4));
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1, reader.GetInt64(0));
            Assert.Equal("MigrationTestSeller", reader.GetString(1));
            Assert.Equal(1, reader.GetInt64(2));
            Assert.Equal(LegacyGuidSellerProductId.ToString("D"), reader.GetString(3));
            Assert.Equal("text", reader.GetString(4));
        }

        await using (var uniqueConnection = await factory.OpenConnectionAsync())
        {
            await using var duplicateCommand = uniqueConnection.CreateCommand();
            duplicateCommand.CommandText = "INSERT INTO SellerProduct (SellerName, ProductId, SellerProductId, SourceFingerprint, CreatedAtUtc) VALUES ($seller, (SELECT Id FROM Product ORDER BY Id LIMIT 1), $sourceId, $fingerprint, $createdAtUtc);";
            duplicateCommand.Parameters.AddWithValue("$seller", "MigrationTestSeller");
            duplicateCommand.Parameters.AddWithValue("$sourceId", LegacyGuidSellerProductId.ToString("D"));
            duplicateCommand.Parameters.AddWithValue("$fingerprint", "duplicate");
            duplicateCommand.Parameters.AddWithValue("$createdAtUtc", DateTimeOffset.UtcNow.ToString("O"));

            await Assert.ThrowsAsync<SqliteException>(() => duplicateCommand.ExecuteNonQueryAsync());
        }

        Assert.Equal(starterHash, SHA256.HashData(await File.ReadAllBytesAsync(fixture.StarterPath)));
        Assert.NotEqual(starterHash, SHA256.HashData(await File.ReadAllBytesAsync(workingPath)));
    }

    [Fact]
    public async Task NormalApplicationConnectionsEnableAndEnforceForeignKeys()
    {
        using var fixture = new TemporaryCatalog();
        var workingPath = await fixture.CreateMigratedWorkingCopyAsync();
        var factory = new SqliteConnectionFactory(workingPath);
        await using var connection = await factory.OpenConnectionAsync();

        Assert.Equal(1, await ReadInt64Async(connection, "PRAGMA foreign_keys;"));

        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO SellerProduct (SellerName, ProductId, SellerProductId, SourceFingerprint, CreatedAtUtc) VALUES ($seller, $productId, $sourceId, $fingerprint, $createdAtUtc);";
        command.Parameters.AddWithValue("$seller", "OrphanSeller");
        command.Parameters.AddWithValue("$productId", -1);
        command.Parameters.AddWithValue("$sourceId", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$fingerprint", "test-fingerprint");
        command.Parameters.AddWithValue("$createdAtUtc", DateTimeOffset.UtcNow.ToString("O"));

        await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
    }

    [Fact]
    public async Task OptionalInchQuoteMigrationRefreshesLegacyKeyWithoutChangingProductAndIsIdempotent()
    {
        using var fixture = new TemporaryCatalog();
        var workingPath = await fixture.CreateMigratedWorkingCopyAsync();
        var factory = new SqliteConnectionFactory(workingPath);
        await using (var connection = await factory.OpenConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM SchemaMigration WHERE MigrationId = $migration; UPDATE Product SET NormalizedName = $old WHERE Id = 14;";
            command.Parameters.AddWithValue("$migration", "202609290003_OptionalInchQuoteIdentity");
            command.Parameters.AddWithValue("$old", "tablet ipad pro 12.9\"");
            await command.ExecuteNonQueryAsync();
        }

        var migrator = new SqliteDatabaseMigrator();
        await migrator.MigrateAsync(workingPath);
        await migrator.MigrateAsync(workingPath);

        await using var migrated = await factory.OpenConnectionAsync();
        Assert.Equal("tablet ipad pro 12.9", await ReadTextAsync(migrated, "SELECT NormalizedName FROM Product WHERE Id = 14;"));
        Assert.Equal("Tablet iPad Pro 12.9\"", await ReadTextAsync(migrated, "SELECT Name FROM Product WHERE Id = 14;"));
        Assert.Equal(975, await ReadInt64Async(migrated, "SELECT COUNT(*) FROM Product;"));
        Assert.Equal(3, await ReadInt64Async(migrated, "SELECT COUNT(*) FROM SchemaMigration;"));
    }

    private static async Task<long> ReadInt64Async(SqliteConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string?> ReadTextAsync(SqliteConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return (string?)await command.ExecuteScalarAsync();
    }

    private sealed class TemporaryCatalog : IDisposable
    {
        private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"marketplace-catalog-tests-{Guid.NewGuid():N}");

        public TemporaryCatalog()
        {
            Directory.CreateDirectory(_temporaryDirectory);
            StarterPath = Path.Combine(AppContext.BaseDirectory, "artifacts", "catalog.db");
            Paths = new FileSystemStoragePaths(new CatalogStorageOptions(
                Path.Combine(_temporaryDirectory, "data"),
                StarterPath));
        }

        public string StarterPath { get; }
        public FileSystemStoragePaths Paths { get; }

        public async Task<string> CreateMigratedWorkingCopyAsync()
        {
            var workingPath = await new SqliteWorkingDatabaseBootstrapper(Paths).EnsureWorkingDatabaseAsync();
            await new SqliteDatabaseMigrator().MigrateAsync(workingPath);
            return workingPath;
        }

        public void Dispose()
        {
            if (Directory.Exists(_temporaryDirectory))
            {
                Directory.Delete(_temporaryDirectory, recursive: true);
            }
        }
    }
}
