using System.Globalization;
using MarketplaceCatalogConsolidator.Application.Ports;
using MarketplaceCatalogConsolidator.Domain;
using Microsoft.Data.Sqlite;

namespace MarketplaceCatalogConsolidator.Infrastructure.Storage;

public sealed class SqliteSellerOfferStore(SqliteConnectionFactory connectionFactory) : ISellerOfferStore
{
    private readonly SqliteConnectionFactory _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public async Task<SellerOffer?> FindBySellerSourceIdAsync(string sellerName, string sellerProductId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sellerProductId);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, SellerName, ProductId, SellerProductId, SourceFingerprint, CreatedAtUtc FROM SellerProduct WHERE SellerName = $sellerName AND SellerProductId = $sellerProductId LIMIT 1;";
        command.Parameters.AddWithValue("$sellerName", sellerName);
        command.Parameters.AddWithValue("$sellerProductId", sellerProductId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new SellerOffer(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.GetString(4),
            DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    public async Task<long> AddAsync(SellerOffer offer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(offer);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = offer.Id is null
            ? "INSERT INTO SellerProduct (SellerName, ProductId, SellerProductId, SourceFingerprint, CreatedAtUtc) VALUES ($sellerName, $productId, $sellerProductId, $fingerprint, $createdAtUtc) RETURNING Id;"
            : "INSERT INTO SellerProduct (Id, SellerName, ProductId, SellerProductId, SourceFingerprint, CreatedAtUtc) VALUES ($id, $sellerName, $productId, $sellerProductId, $fingerprint, $createdAtUtc) RETURNING Id;";
        if (offer.Id is not null)
        {
            command.Parameters.AddWithValue("$id", offer.Id.Value);
        }

        command.Parameters.AddWithValue("$sellerName", offer.SellerName);
        command.Parameters.AddWithValue("$productId", offer.ProductId);
        command.Parameters.AddWithValue("$sellerProductId", offer.SellerProductId);
        command.Parameters.AddWithValue("$fingerprint", offer.SourceFingerprint);
        command.Parameters.AddWithValue("$createdAtUtc", offer.CreatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }
}
