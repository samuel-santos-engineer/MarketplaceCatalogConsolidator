using MarketplaceCatalogConsolidator.Application.Ports;
using MarketplaceCatalogConsolidator.Domain;
using Microsoft.Data.Sqlite;

namespace MarketplaceCatalogConsolidator.Infrastructure.Storage;

public sealed class SqliteProductCatalog(SqliteConnectionFactory connectionFactory) : IProductCatalog
{
    private readonly SqliteConnectionFactory _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public async Task<CatalogProduct?> FindByIdentityAsync(ProductIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Name, Brand, Category, NormalizedName, NormalizedBrand, NormalizedCategory FROM Product WHERE NormalizedBrand = $brand AND NormalizedName = $name AND NormalizedCategory = $category ORDER BY Id LIMIT 1;";
        command.Parameters.AddWithValue("$brand", identity.NormalizedBrand);
        command.Parameters.AddWithValue("$name", identity.NormalizedName);
        command.Parameters.AddWithValue("$category", identity.NormalizedCategory);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new CatalogProduct(
            reader.GetInt64(0),
            reader.GetString(1),
            ReadNullableText(reader, 2),
            ReadNullableText(reader, 3),
            reader.GetString(4),
            ReadNullableText(reader, 5),
            ReadNullableText(reader, 6));
    }

    public async Task<long> CreateAsync(CatalogProduct product, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(product);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Product (Name, Brand, Category, NormalizedName, NormalizedBrand, NormalizedCategory) VALUES ($name, $brand, $category, $normalizedName, $normalizedBrand, $normalizedCategory) RETURNING Id;";
        command.Parameters.AddWithValue("$name", product.Name);
        command.Parameters.AddWithValue("$brand", (object?)product.Brand ?? DBNull.Value);
        command.Parameters.AddWithValue("$category", (object?)product.Category ?? DBNull.Value);
        command.Parameters.AddWithValue("$normalizedName", TextNormalization.NormalizeProductNameForComparison(product.Name));
        command.Parameters.AddWithValue("$normalizedBrand", (object?)product.NormalizedBrand ?? DBNull.Value);
        command.Parameters.AddWithValue("$normalizedCategory", (object?)product.NormalizedCategory ?? DBNull.Value);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? ReadNullableText(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
