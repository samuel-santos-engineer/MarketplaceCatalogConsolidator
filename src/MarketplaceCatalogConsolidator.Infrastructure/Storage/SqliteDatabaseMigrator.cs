using System.Globalization;
using MarketplaceCatalogConsolidator.Application.Ports;
using MarketplaceCatalogConsolidator.Domain;
using Microsoft.Data.Sqlite;

namespace MarketplaceCatalogConsolidator.Infrastructure.Storage;

public sealed class SqliteDatabaseMigrator : IDatabaseMigrator
{
    private const string InitialMigrationId = "202609290001_InitialCatalogAndUploadSchema";
    private const string ReportFinalizationMigrationId = "202609290002_ReportFinalization";

    public async Task MigrateAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        var connectionFactory = new SqliteConnectionFactory(databasePath);
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null,
            "CREATE TABLE IF NOT EXISTS SchemaMigration (MigrationId TEXT NOT NULL PRIMARY KEY, AppliedAtUtc TEXT NOT NULL);",
            cancellationToken).ConfigureAwait(false);

        await using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "SELECT COUNT(*) FROM SchemaMigration WHERE MigrationId = $migrationId;";
        checkCommand.Parameters.AddWithValue("$migrationId", InitialMigrationId);
        var alreadyApplied = Convert.ToInt64(await checkCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
        if (!alreadyApplied)
        {
            await using var transaction = connection.BeginTransaction();
            await AddProductIdentityColumnsAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await RebuildSellerProductAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await CreateUploadSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await VerifyForeignKeysAsync(connection, transaction, cancellationToken).ConfigureAwait(false);

            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.Transaction = transaction;
            migrationCommand.CommandText = "INSERT INTO SchemaMigration (MigrationId, AppliedAtUtc) VALUES ($migrationId, $appliedAtUtc);";
            migrationCommand.Parameters.AddWithValue("$migrationId", InitialMigrationId);
            migrationCommand.Parameters.AddWithValue("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await migrationCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await ApplyReportFinalizationMigrationAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyReportFinalizationMigrationAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "SELECT COUNT(*) FROM SchemaMigration WHERE MigrationId = $migrationId;";
        checkCommand.Parameters.AddWithValue("$migrationId", ReportFinalizationMigrationId);
        var alreadyApplied = Convert.ToInt64(await checkCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0;
        if (alreadyApplied)
        {
            return;
        }

        await using var transaction = connection.BeginTransaction();
        await ExecuteAsync(connection, transaction,
            "ALTER TABLE Upload ADD COLUMN IntendedTerminalStatus TEXT NULL; " +
            "ALTER TABLE Upload ADD COLUMN ConsolidationFinishedAtUtc TEXT NULL; " +
            "ALTER TABLE Upload ADD COLUMN ReportGeneratedAtUtc TEXT NULL; " +
            "ALTER TABLE Upload ADD COLUMN ReportGenerationFailureCode TEXT NULL; " +
            "ALTER TABLE Upload ADD COLUMN ReportGenerationFailureMessage TEXT NULL; " +
            "UPDATE Upload SET IntendedTerminalStatus = Status, Status = 'ReportPending', ConsolidationFinishedAtUtc = COALESCE(CompletedAtUtc, StartedAtUtc), CompletedAtUtc = NULL " +
            "WHERE Status IN ('Completed', 'CompletedWithRejections', 'Failed') AND ReportSha256 IS NULL;",
            cancellationToken).ConfigureAwait(false);

        await using var migrationCommand = connection.CreateCommand();
        migrationCommand.Transaction = transaction;
        migrationCommand.CommandText = "INSERT INTO SchemaMigration (MigrationId, AppliedAtUtc) VALUES ($migrationId, $appliedAtUtc);";
        migrationCommand.Parameters.AddWithValue("$migrationId", ReportFinalizationMigrationId);
        migrationCommand.Parameters.AddWithValue("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await migrationCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddProductIdentityColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction,
            "ALTER TABLE Product ADD COLUMN NormalizedName TEXT NOT NULL DEFAULT ''; " +
            "ALTER TABLE Product ADD COLUMN NormalizedBrand TEXT NULL; " +
            "ALTER TABLE Product ADD COLUMN NormalizedCategory TEXT NULL;",
            cancellationToken).ConfigureAwait(false);

        await using var selectCommand = connection.CreateCommand();
        selectCommand.Transaction = transaction;
        selectCommand.CommandText = "SELECT Id, Name, Brand, Category FROM Product;";
        await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var products = new List<(long Id, string Name, string? Brand, string? Category)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            products.Add((reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        await using var updateCommand = connection.CreateCommand();
        updateCommand.Transaction = transaction;
        updateCommand.CommandText = "UPDATE Product SET NormalizedName = $name, NormalizedBrand = $brand, NormalizedCategory = $category WHERE Id = $id;";
        var nameParameter = updateCommand.Parameters.Add("$name", SqliteType.Text);
        var brandParameter = updateCommand.Parameters.Add("$brand", SqliteType.Text);
        var categoryParameter = updateCommand.Parameters.Add("$category", SqliteType.Text);
        var idParameter = updateCommand.Parameters.Add("$id", SqliteType.Integer);
        foreach (var product in products)
        {
            nameParameter.Value = TextNormalization.NormalizeForComparison(product.Name);
            brandParameter.Value = product.Brand is null ? DBNull.Value : TextNormalization.NormalizeForComparison(product.Brand);
            categoryParameter.Value = product.Category is null ? DBNull.Value : TextNormalization.NormalizeForComparison(product.Category);
            idParameter.Value = product.Id;
            await updateCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await ExecuteAsync(connection, transaction,
            "CREATE INDEX IX_Product_NormalizedIdentity ON Product (NormalizedBrand, NormalizedName, NormalizedCategory);",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task RebuildSellerProductAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction,
            "ALTER TABLE SellerProduct RENAME TO SellerProduct_Legacy; " +
            "CREATE TABLE SellerProduct (" +
            "Id INTEGER PRIMARY KEY AUTOINCREMENT, " +
            "SellerName TEXT NOT NULL, " +
            "ProductId INTEGER NOT NULL REFERENCES Product(Id), " +
            "SellerProductId TEXT NOT NULL, " +
            "SourceFingerprint TEXT NOT NULL, " +
            "CreatedAtUtc TEXT NOT NULL, " +
            "UNIQUE (SellerName, SellerProductId)); " +
            "INSERT INTO SellerProduct (Id, SellerName, ProductId, SellerProductId, SourceFingerprint, CreatedAtUtc) " +
            "SELECT Id, SellerName, ProductId, CAST(SellerProductId AS TEXT), 'legacy:' || CAST(Id AS TEXT), '1970-01-01T00:00:00.0000000+00:00' " +
            "FROM SellerProduct_Legacy; " +
            "DROP TABLE SellerProduct_Legacy;",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task CreateUploadSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction,
            "CREATE INDEX IX_SellerProduct_ProductId ON SellerProduct (ProductId); " +
            "CREATE TABLE Upload (" +
            "Id TEXT NOT NULL PRIMARY KEY, " +
            "IdempotencyKey TEXT NOT NULL UNIQUE, " +
            "FileName TEXT NOT NULL, " +
            "FileHash TEXT NOT NULL, " +
            "StagedFilePath TEXT NOT NULL, " +
            "ReportFilePath TEXT NOT NULL, " +
            "Status TEXT NOT NULL, " +
            "StartedAtUtc TEXT NOT NULL, " +
            "CompletedAtUtc TEXT NULL, " +
            "ReceivedCount INTEGER NOT NULL DEFAULT 0, " +
            "ApprovedCount INTEGER NOT NULL DEFAULT 0, " +
            "CleanedCount INTEGER NOT NULL DEFAULT 0, " +
            "RejectedCount INTEGER NOT NULL DEFAULT 0, " +
            "TraceId TEXT NOT NULL, " +
            "FailureCode TEXT NULL, " +
            "FailureMessage TEXT NULL, " +
            "ReportSha256 TEXT NULL); " +
            "CREATE INDEX IX_Upload_StartedAtUtc ON Upload (StartedAtUtc DESC); " +
            "CREATE INDEX IX_Upload_Status_StartedAtUtc ON Upload (Status, StartedAtUtc); " +
            "CREATE TABLE UploadItem (" +
            "UploadId TEXT NOT NULL REFERENCES Upload(Id) ON DELETE CASCADE, " +
            "SourceIndex INTEGER NOT NULL, " +
            "SourceProductId TEXT NULL, " +
            "RawSellerName TEXT NULL, " +
            "RawName TEXT NULL, " +
            "RawBrand TEXT NULL, " +
            "RawCategory TEXT NULL, " +
            "CleanedSellerName TEXT NULL, " +
            "CleanedName TEXT NULL, " +
            "CleanedBrand TEXT NULL, " +
            "CleanedCategory TEXT NULL, " +
            "Status TEXT NOT NULL, " +
            "ActionTaken TEXT NOT NULL, " +
            "MatchedProductId INTEGER NULL REFERENCES Product(Id), " +
            "PRIMARY KEY (UploadId, SourceIndex)); " +
            "CREATE INDEX IX_UploadItem_Status ON UploadItem (UploadId, Status);",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyForeignKeysAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The catalog migration produced a foreign-key violation.");
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
