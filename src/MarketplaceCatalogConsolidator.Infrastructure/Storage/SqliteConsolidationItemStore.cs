using System.Globalization;
using MarketplaceCatalogConsolidator.Application.Ports;
using MarketplaceCatalogConsolidator.Application.Workflow;
using MarketplaceCatalogConsolidator.Domain;
using Microsoft.Data.Sqlite;

namespace MarketplaceCatalogConsolidator.Infrastructure.Storage;

public sealed class SqliteConsolidationItemStore(SqliteConnectionFactory connectionFactory) : IConsolidationItemStore
{
    private readonly SqliteConnectionFactory _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public async Task<UploadItemProcessingResult> ConsolidateAsync(ConsolidationCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction();

        var existingOffer = await FindSellerOfferAsync(connection, transaction, candidate, cancellationToken).ConfigureAwait(false);
        if (existingOffer is not null)
        {
            var duplicateAction = string.Equals(existingOffer.Value.SourceFingerprint, candidate.SourceFingerprint, StringComparison.Ordinal)
                ? $"Duplicate source entry for seller {candidate.SellerName} and source ID {candidate.SourceProductId}."
                : $"Source-ID conflict for seller {candidate.SellerName} and source ID {candidate.SourceProductId}.";
            var duplicateResult = CreateResult(candidate, UploadItemStatus.Rejected, duplicateAction, existingOffer.Value.ProductId, candidate.Name, candidate.Brand, candidate.Category, persisted: true);
            await SaveOutcomeAsync(connection, transaction, candidate, duplicateResult, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return duplicateResult;
        }

        var canonicalBrand = CreateIdentityValue(candidate.Brand);
        var canonicalCategory = CreateIdentityValue(candidate.Category);
        var normalizedName = TextNormalization.NormalizeForComparison(candidate.Name);
        long productId;
        var outcomeName = candidate.Name;
        var createdProduct = false;

        if (canonicalBrand is not null && canonicalCategory is not null && normalizedName.Length > 0)
        {
            var match = await FindProductAsync(
                connection,
                transaction,
                canonicalBrand.NormalizedValue,
                normalizedName,
                canonicalCategory.NormalizedValue,
                cancellationToken).ConfigureAwait(false);
            if (match is not null)
            {
                productId = match.Value.Id;
                outcomeName = SourceTextCleaner.Clean(match.Value.Name) ?? candidate.Name;
            }
            else
            {
                productId = await CreateProductAsync(connection, transaction, candidate.Name, canonicalBrand, canonicalCategory, normalizedName, cancellationToken).ConfigureAwait(false);
                createdProduct = true;
            }
        }
        else
        {
            productId = await CreateProductAsync(connection, transaction, candidate.Name, canonicalBrand, canonicalCategory, normalizedName, cancellationToken).ConfigureAwait(false);
            createdProduct = true;
        }

        await AddSellerOfferAsync(connection, transaction, candidate, productId, cancellationToken).ConfigureAwait(false);
        var transformations = GetTransformations(candidate, outcomeName, canonicalBrand?.DisplayValue, canonicalCategory?.DisplayValue);
        var status = transformations.Count == 0 ? UploadItemStatus.Approved : UploadItemStatus.Cleaned;
        var action = BuildAction(transformations, createdProduct, candidate.SellerName, productId);
        var result = CreateResult(candidate, status, action, productId, outcomeName, canonicalBrand?.DisplayValue, canonicalCategory?.DisplayValue, persisted: true);
        await SaveOutcomeAsync(connection, transaction, candidate, result, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task<(string SourceFingerprint, long ProductId)?> FindSellerOfferAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConsolidationCandidate candidate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT SourceFingerprint, ProductId FROM SellerProduct WHERE SellerName = $sellerName AND SellerProductId = $sellerProductId LIMIT 1;";
        command.Parameters.AddWithValue("$sellerName", candidate.SellerName);
        command.Parameters.AddWithValue("$sellerProductId", candidate.SourceProductId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetString(0), reader.GetInt64(1))
            : null;
    }

    private static CanonicalValue? CreateIdentityValue(string? value) =>
        value is null ? null : new CanonicalValue(value, TextNormalization.NormalizeForComparison(value));

    private static async Task<(long Id, string Name)?> FindProductAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string normalizedBrand,
        string normalizedName,
        string normalizedCategory,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Id, Name FROM Product WHERE NormalizedBrand = $brand AND NormalizedName = $name AND NormalizedCategory = $category LIMIT 1;";
        command.Parameters.AddWithValue("$brand", normalizedBrand);
        command.Parameters.AddWithValue("$name", normalizedName);
        command.Parameters.AddWithValue("$category", normalizedCategory);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (reader.GetInt64(0), reader.GetString(1))
            : null;
    }

    private static async Task<long> CreateProductAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string name,
        CanonicalValue? brand,
        CanonicalValue? category,
        string normalizedName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO Product (Name, Brand, Category, NormalizedName, NormalizedBrand, NormalizedCategory) VALUES ($name, $brand, $category, $normalizedName, $normalizedBrand, $normalizedCategory) RETURNING Id;";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$brand", (object?)brand?.DisplayValue ?? DBNull.Value);
        command.Parameters.AddWithValue("$category", (object?)category?.DisplayValue ?? DBNull.Value);
        command.Parameters.AddWithValue("$normalizedName", normalizedName);
        command.Parameters.AddWithValue("$normalizedBrand", (object?)brand?.NormalizedValue ?? DBNull.Value);
        command.Parameters.AddWithValue("$normalizedCategory", (object?)category?.NormalizedValue ?? DBNull.Value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task AddSellerOfferAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConsolidationCandidate candidate,
        long productId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO SellerProduct (SellerName, ProductId, SellerProductId, SourceFingerprint, CreatedAtUtc) VALUES ($sellerName, $productId, $sellerProductId, $fingerprint, $createdAtUtc);";
        command.Parameters.AddWithValue("$sellerName", candidate.SellerName);
        command.Parameters.AddWithValue("$productId", productId);
        command.Parameters.AddWithValue("$sellerProductId", candidate.SourceProductId);
        command.Parameters.AddWithValue("$fingerprint", candidate.SourceFingerprint);
        command.Parameters.AddWithValue("$createdAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task SaveOutcomeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConsolidationCandidate candidate,
        UploadItemProcessingResult result,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO UploadItem (UploadId, SourceIndex, SourceProductId, RawSellerName, RawName, RawBrand, RawCategory, CleanedSellerName, CleanedName, CleanedBrand, CleanedCategory, Status, ActionTaken, MatchedProductId) VALUES ($uploadId, $sourceIndex, $sourceProductId, $rawSellerName, $rawName, $rawBrand, $rawCategory, $cleanedSellerName, $cleanedName, $cleanedBrand, $cleanedCategory, $status, $actionTaken, $matchedProductId) ON CONFLICT (UploadId, SourceIndex) DO UPDATE SET SourceProductId = excluded.SourceProductId, RawSellerName = excluded.RawSellerName, RawName = excluded.RawName, RawBrand = excluded.RawBrand, RawCategory = excluded.RawCategory, CleanedSellerName = excluded.CleanedSellerName, CleanedName = excluded.CleanedName, CleanedBrand = excluded.CleanedBrand, CleanedCategory = excluded.CleanedCategory, Status = excluded.Status, ActionTaken = excluded.ActionTaken, MatchedProductId = excluded.MatchedProductId;";
        command.Parameters.AddWithValue("$uploadId", candidate.UploadId.ToString("D"));
        command.Parameters.AddWithValue("$sourceIndex", candidate.Source.SourceIndex);
        command.Parameters.AddWithValue("$sourceProductId", (object?)result.SourceProductId ?? DBNull.Value);
        command.Parameters.AddWithValue("$rawSellerName", (object?)result.RawSellerName ?? DBNull.Value);
        command.Parameters.AddWithValue("$rawName", (object?)result.RawName ?? DBNull.Value);
        command.Parameters.AddWithValue("$rawBrand", (object?)result.RawBrand ?? DBNull.Value);
        command.Parameters.AddWithValue("$rawCategory", (object?)result.RawCategory ?? DBNull.Value);
        command.Parameters.AddWithValue("$cleanedSellerName", (object?)result.CleanedSellerName ?? DBNull.Value);
        command.Parameters.AddWithValue("$cleanedName", (object?)result.CleanedName ?? DBNull.Value);
        command.Parameters.AddWithValue("$cleanedBrand", (object?)result.CleanedBrand ?? DBNull.Value);
        command.Parameters.AddWithValue("$cleanedCategory", (object?)result.CleanedCategory ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", result.Status.ToString());
        command.Parameters.AddWithValue("$actionTaken", result.ActionTaken);
        command.Parameters.AddWithValue("$matchedProductId", (object?)result.MatchedProductId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static UploadItemProcessingResult CreateResult(
        ConsolidationCandidate candidate,
        UploadItemStatus status,
        string action,
        long? productId,
        string? name,
        string? brand,
        string? category,
        bool persisted) => new(
        status,
        action,
        productId,
        candidate.SourceProductId,
        candidate.Source.SellerName,
        candidate.Source.Name,
        candidate.Source.Brand,
        candidate.Source.Category,
        candidate.SellerName,
        name,
        brand,
        category,
        persisted);

    private static List<string> GetTransformations(ConsolidationCandidate candidate, string name, string? brand, string? category)
    {
        var changes = new List<string>();
        AddChange(changes, "Id", candidate.Source.Id, candidate.SourceProductId);
        AddChange(changes, "SellerName", candidate.Source.SellerName, candidate.SellerName);
        AddChange(changes, "Name", candidate.Source.Name, name);
        AddChange(changes, "Brand", candidate.Source.Brand, brand);
        AddChange(changes, "Category", candidate.Source.Category, category);
        return changes;
    }

    private static void AddChange(ICollection<string> changes, string field, string? before, string? after)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            changes.Add($"{field}: {after ?? "NULL"}");
        }
    }

    private static string BuildAction(IReadOnlyCollection<string> changes, bool createdProduct, string sellerName, long productId)
    {
        var action = createdProduct
            ? $"Created Product {productId} and linked seller {sellerName}."
            : $"Linked seller {sellerName} to existing product {productId}.";
        return changes.Count == 0 ? action : $"{string.Join("; ", changes)} {action}";
    }

    private sealed record CanonicalValue(string DisplayValue, string NormalizedValue);
}
