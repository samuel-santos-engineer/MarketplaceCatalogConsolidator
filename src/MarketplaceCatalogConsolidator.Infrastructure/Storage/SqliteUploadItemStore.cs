using MarketplaceCatalogConsolidator.Application.Ports;
using MarketplaceCatalogConsolidator.Domain;
using Microsoft.Data.Sqlite;

namespace MarketplaceCatalogConsolidator.Infrastructure.Storage;

public sealed class SqliteUploadItemStore(SqliteConnectionFactory connectionFactory) : IUploadItemStore
{
    private readonly SqliteConnectionFactory _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public async Task SaveProgressAsync(UploadItemRecord item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO UploadItem (UploadId, SourceIndex, SourceProductId, RawSellerName, RawName, RawBrand, RawCategory, CleanedSellerName, CleanedName, CleanedBrand, CleanedCategory, Status, ActionTaken, MatchedProductId) VALUES ($uploadId, $sourceIndex, $sourceProductId, $rawSellerName, $rawName, $rawBrand, $rawCategory, $cleanedSellerName, $cleanedName, $cleanedBrand, $cleanedCategory, $status, $actionTaken, $matchedProductId) ON CONFLICT (UploadId, SourceIndex) DO UPDATE SET SourceProductId = excluded.SourceProductId, RawSellerName = excluded.RawSellerName, RawName = excluded.RawName, RawBrand = excluded.RawBrand, RawCategory = excluded.RawCategory, CleanedSellerName = excluded.CleanedSellerName, CleanedName = excluded.CleanedName, CleanedBrand = excluded.CleanedBrand, CleanedCategory = excluded.CleanedCategory, Status = excluded.Status, ActionTaken = excluded.ActionTaken, MatchedProductId = excluded.MatchedProductId;";
        AddItemParameters(command, item);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UploadItemRecord>> GetByUploadIdAsync(Guid uploadId, CancellationToken cancellationToken = default)
        => (await ListAsync(uploadId, 1, int.MaxValue, cancellationToken).ConfigureAwait(false)).Items;

    public async Task<Page<UploadItemRecord>> ListAsync(Guid uploadId, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        await using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT COUNT(*) FROM UploadItem WHERE UploadId = $id;";
        count.Parameters.AddWithValue("$id", uploadId.ToString("D"));
        var total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT UploadId, SourceIndex, SourceProductId, RawSellerName, RawName, RawBrand, RawCategory, CleanedSellerName, CleanedName, CleanedBrand, CleanedCategory, Status, ActionTaken, MatchedProductId FROM UploadItem WHERE UploadId = $uploadId ORDER BY SourceIndex LIMIT $limit OFFSET $offset;";
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", (long)(page - 1) * pageSize);
        command.Parameters.AddWithValue("$uploadId", uploadId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var items = new List<UploadItemRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(new UploadItemRecord(
                Guid.Parse(reader.GetString(0)),
                reader.GetInt32(1),
                ReadNullableText(reader, 2),
                ReadNullableText(reader, 3),
                ReadNullableText(reader, 4),
                ReadNullableText(reader, 5),
                ReadNullableText(reader, 6),
                ReadNullableText(reader, 7),
                ReadNullableText(reader, 8),
                ReadNullableText(reader, 9),
                ReadNullableText(reader, 10),
                Enum.Parse<UploadItemStatus>(reader.GetString(11), ignoreCase: false),
                reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetInt64(13)));
        }

        return new Page<UploadItemRecord>(items, page, pageSize, total);
    }

    private static void AddItemParameters(SqliteCommand command, UploadItemRecord item)
    {
        command.Parameters.AddWithValue("$uploadId", item.UploadId.ToString("D"));
        command.Parameters.AddWithValue("$sourceIndex", item.SourceIndex);
        command.Parameters.AddWithValue("$sourceProductId", (object?)item.SourceProductId ?? DBNull.Value);
        command.Parameters.AddWithValue("$rawSellerName", (object?)item.RawSellerName ?? DBNull.Value);
        command.Parameters.AddWithValue("$rawName", (object?)item.RawName ?? DBNull.Value);
        command.Parameters.AddWithValue("$rawBrand", (object?)item.RawBrand ?? DBNull.Value);
        command.Parameters.AddWithValue("$rawCategory", (object?)item.RawCategory ?? DBNull.Value);
        command.Parameters.AddWithValue("$cleanedSellerName", (object?)item.CleanedSellerName ?? DBNull.Value);
        command.Parameters.AddWithValue("$cleanedName", (object?)item.CleanedName ?? DBNull.Value);
        command.Parameters.AddWithValue("$cleanedBrand", (object?)item.CleanedBrand ?? DBNull.Value);
        command.Parameters.AddWithValue("$cleanedCategory", (object?)item.CleanedCategory ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", item.Status.ToString());
        command.Parameters.AddWithValue("$actionTaken", item.ActionTaken);
        command.Parameters.AddWithValue("$matchedProductId", (object?)item.MatchedProductId ?? DBNull.Value);
    }

    private static string? ReadNullableText(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
