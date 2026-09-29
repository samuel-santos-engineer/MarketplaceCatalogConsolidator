using MarketplaceCatalogConsolidator.Application.Ports;
using MarketplaceCatalogConsolidator.Domain;
using Microsoft.Data.Sqlite;

namespace MarketplaceCatalogConsolidator.Infrastructure.Storage;

public sealed class SqlitePublicReadStore(SqliteConnectionFactory factory) : IPublicReadStore
{
    public Task<Page<UploadRecord>> ListUploadsAsync(UploadStatus? status, int page, int pageSize, CancellationToken cancellationToken) =>
        new SqliteUploadStore(factory).ListFilteredAsync(status, page, pageSize, cancellationToken);

    public Task<Page<UploadItemRecord>> ListItemsAsync(Guid uploadId, int page, int pageSize, CancellationToken cancellationToken) =>
        new SqliteUploadItemStore(factory).ListAsync(uploadId, page, pageSize, cancellationToken);

    public async Task<Page<CatalogResult>> QueryCatalogAsync(string? category, string? brand, string? name, string? sellerName, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        connection.CreateFunction<string?, string?>("public_normalize", value => value is null ? null : TextNormalization.NormalizeForComparison(value));
        await using var transaction = connection.BeginTransaction(deferred: true);
        const string predicate = " WHERE ($category IS NULL OR public_normalize(p.Category) = $category) AND ($brand IS NULL OR public_normalize(p.Brand) = $brand) AND ($name IS NULL OR instr(public_normalize(p.Name), $name) > 0) AND ($seller IS NULL OR EXISTS (SELECT 1 FROM SellerProduct s WHERE s.ProductId = p.Id AND public_normalize(s.SellerName) = $seller))";
        var seller = Normalize(sellerName);
        await using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT COUNT(*) FROM Product p" + predicate;
        AddFilters(count, category, brand, name, seller);
        var total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        var products = new List<CatalogResult>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT p.Id, p.Name, p.Brand, p.Category FROM Product p" + predicate + " ORDER BY p.Id LIMIT $limit OFFSET $offset;";
            AddFilters(command, category, brand, name, seller);
            command.Parameters.AddWithValue("$limit", pageSize);
            command.Parameters.AddWithValue("$offset", (long)(page - 1) * pageSize);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                products.Add(new CatalogResult(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), []));
            }
        }
        for (var index = 0; index < products.Count; index++)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT SellerName, SellerProductId FROM SellerProduct WHERE ProductId = $id AND ($seller IS NULL OR public_normalize(SellerName) = $seller) ORDER BY Id;";
            command.Parameters.AddWithValue("$id", products[index].Id);
            command.Parameters.AddWithValue("$seller", (object?)seller ?? DBNull.Value);
            var offers = new List<PublicSellerOffer>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                offers.Add(new PublicSellerOffer(reader.GetString(0), reader.GetString(1)));
            }
            products[index] = products[index] with { SellerOffers = offers };
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new Page<CatalogResult>(products, page, pageSize, total);
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : TextNormalization.NormalizeForComparison(value);

    private static void AddFilters(SqliteCommand command, string? category, string? brand, string? name, string? seller)
    {
        command.Parameters.AddWithValue("$category", (object?)Normalize(category) ?? DBNull.Value);
        command.Parameters.AddWithValue("$brand", (object?)Normalize(brand) ?? DBNull.Value);
        command.Parameters.AddWithValue("$name", (object?)Normalize(name) ?? DBNull.Value);
        command.Parameters.AddWithValue("$seller", (object?)seller ?? DBNull.Value);
    }
}
