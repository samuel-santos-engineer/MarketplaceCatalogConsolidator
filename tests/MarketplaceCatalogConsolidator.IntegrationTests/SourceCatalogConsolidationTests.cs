using System.Text;
using System.Text.Json;
using MarketplaceCatalogConsolidator.Application.Parsing;
using MarketplaceCatalogConsolidator.Application.Ports;
using MarketplaceCatalogConsolidator.Application.Workflow;
using MarketplaceCatalogConsolidator.Domain;
using MarketplaceCatalogConsolidator.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace MarketplaceCatalogConsolidator.IntegrationTests;

public sealed class SourceCatalogConsolidationTests
{
    [Fact]
    public async Task WhitespaceCleanupLinksExistingProductAndPersistsHumanReadableOutcome()
    {
        using var fixture = new ConsolidationFixture();
        var canonicalId = await fixture.EnsureProductAsync("Smartphone Galaxy S23", "Samsung", "Electronics");
        var result = await fixture.ProcessAsync(Source("MegaStore", "Smartphone  Galaxy S23", "Samsung", "Electronics"));

        Assert.Equal(UploadItemStatus.Cleaned, result.Status);
        Assert.Equal(canonicalId, result.MatchedProductId);
        Assert.Contains("Name: \"Smartphone  Galaxy S23\" -> \"Smartphone Galaxy S23\"", result.ActionTaken, StringComparison.Ordinal);
        Assert.Contains($"existing product {canonicalId}", result.ActionTaken, StringComparison.Ordinal);
        Assert.True(result.OutcomePersisted);
        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM Product WHERE NormalizedName = $name;", ("$name", "smartphone galaxy s23")));
        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM SellerProduct WHERE SellerName = $seller;", ("$seller", "MegaStore")));
        var saved = Assert.Single(await fixture.ItemStore.GetByUploadIdAsync(fixture.LastUploadId));
        Assert.Equal("Smartphone  Galaxy S23", saved.RawName);
        Assert.Equal("Smartphone Galaxy S23", saved.CleanedName);
        Assert.Equal(canonicalId, saved.MatchedProductId);
    }

    [Fact]
    public async Task AccentInsensitiveNameLinksCanonicalCameraWithoutCreatingDuplicate()
    {
        using var fixture = new ConsolidationFixture();
        var canonicalId = await fixture.EnsureProductAsync("Camera Canon EOS R6", "Canon", "Electronics");
        var initialCount = await fixture.CountAsync("SELECT COUNT(*) FROM Product;");

        var result = await fixture.ProcessAsync(Source("CameraShop", "Câmera Canon EOS R6", "Canon", "Electronics"));

        Assert.Equal(UploadItemStatus.Cleaned, result.Status);
        Assert.Equal(canonicalId, result.MatchedProductId);
        Assert.Contains("Name: \"Câmera Canon EOS R6\" -> \"Camera Canon EOS R6\"", result.ActionTaken, StringComparison.Ordinal);
        Assert.Equal(initialCount, await fixture.CountAsync("SELECT COUNT(*) FROM Product;"));
    }

    [Fact]
    public async Task PhotoAliasResolvesToPhotographyAndMatchesCanonicalProduct()
    {
        using var fixture = new ConsolidationFixture();
        var canonicalId = await fixture.EnsureProductAsync("EOS R6 Camera Kit", "Canon", "Photography");

        var result = await fixture.ProcessAsync(Source("PhotoSeller", "EOS R6 Camera Kit", "Canon", "Photo"));

        Assert.Equal(UploadItemStatus.Cleaned, result.Status);
        Assert.Equal(canonicalId, result.MatchedProductId);
        Assert.Equal("Photography", result.CleanedCategory);
        Assert.Contains("Category: \"Photo\" -> \"Photography\"", result.ActionTaken, StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM Product WHERE NormalizedName = $name;", ("$name", "eos r6 camera kit")));
    }

    [Fact]
    public async Task CrossSellerDuplicateCreatesOnlySellerLinkAndLeavesCanonicalDisplayUntouched()
    {
        using var fixture = new ConsolidationFixture();
        var canonicalId = await fixture.EnsureProductAsync("Smartphone Galaxy S23", "Samsung", "Electronics");
        var originalDisplay = await fixture.ReadProductNameAsync(canonicalId);
        var beforeCount = await fixture.CountAsync("SELECT COUNT(*) FROM Product;");

        var result = await fixture.ProcessAsync(Source("SecondSeller", "Smartphone Galaxy S23", "Samsung", "Electronics"));

        Assert.Equal(UploadItemStatus.Approved, result.Status);
        Assert.Equal(canonicalId, result.MatchedProductId);
        Assert.Contains($"Linked seller SecondSeller to existing product {canonicalId}", result.ActionTaken, StringComparison.Ordinal);
        Assert.Equal(beforeCount, await fixture.CountAsync("SELECT COUNT(*) FROM Product;"));
        Assert.Equal(originalDisplay, await fixture.ReadProductNameAsync(canonicalId));
        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM SellerProduct WHERE SellerName = $seller AND ProductId = $productId;", ("$seller", "SecondSeller"), ("$productId", canonicalId)));
    }

    [Fact]
    public async Task StrictMatchFailureCreatesProductAndOffer()
    {
        using var fixture = new ConsolidationFixture();
        var name = $"Novel Catalog Item {Guid.NewGuid():N}";
        var result = await fixture.ProcessAsync(Source("NewSeller", name, "Canon", "Photography"));

        Assert.Equal(UploadItemStatus.Approved, result.Status);
        Assert.NotNull(result.MatchedProductId);
        Assert.Contains($"Created Product {result.MatchedProductId}", result.ActionTaken, StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM SellerProduct WHERE SellerName = $seller AND ProductId = $productId;", ("$seller", "NewSeller"), ("$productId", result.MatchedProductId!.Value)));
        Assert.Equal(name, await fixture.ReadProductNameAsync(result.MatchedProductId.Value));
    }

    [Fact]
    public async Task UnknownBrandAndCategoryBecomeNullAndDoNotMatchIncompleteIdentity()
    {
        using var fixture = new ConsolidationFixture();
        var name = $"Incomplete Identity Item {Guid.NewGuid():N}";
        var result = await fixture.ProcessAsync(Source("UnknownSeller", name, "Unknown Brand", "Unknown Category"));

        Assert.Equal(UploadItemStatus.Cleaned, result.Status);
        Assert.Contains("Brand: \"Unknown Brand\" -> NULL", result.ActionTaken, StringComparison.Ordinal);
        Assert.Contains("Category: \"Unknown Category\" -> NULL", result.ActionTaken, StringComparison.Ordinal);
        Assert.Null(result.CleanedBrand);
        Assert.Null(result.CleanedCategory);
        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM Product WHERE Name = $name AND Brand IS NULL AND Category IS NULL;", ("$name", name)));
        Assert.Null(await fixture.FindByIdentityAsync("unknown brand", "unknown category", name));
    }

    [Fact]
    public async Task SuspiciousSqlControlValueIsRejectedWithoutExecutingSql()
    {
        using var fixture = new ConsolidationFixture();
        var beforeProducts = await fixture.CountAsync("SELECT COUNT(*) FROM Product;");
        var beforeSellers = await fixture.CountAsync("SELECT COUNT(*) FROM SellerProduct;");
        var result = await fixture.ProcessAsync(Source("SecureSeller", "Safe Item", "TestBrand'; SELECT 1; --", "Electronics"));

        Assert.Equal(UploadItemStatus.Rejected, result.Status);
        Assert.Equal("Brand contains a suspicious SQL control sequence", result.ActionTaken);
        Assert.Equal(beforeProducts, await fixture.CountAsync("SELECT COUNT(*) FROM Product;"));
        Assert.Equal(beforeSellers, await fixture.CountAsync("SELECT COUNT(*) FROM SellerProduct;"));
        var item = Assert.Single(await fixture.ItemStore.GetByUploadIdAsync(fixture.LastUploadId));
        Assert.Equal(UploadItemStatus.Rejected, item.Status);
        Assert.Contains("TestBrand'; SELECT 1; --", item.RawBrand, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrdinaryApostrophesAndPunctuationAreAccepted()
    {
        using var fixture = new ConsolidationFixture();
        var productName = "O'Brian's Camera: Compact!";
        var result = await fixture.ProcessAsync(Source("Photo-Shop", productName, "Canon", "Photography"));

        Assert.Equal(UploadItemStatus.Approved, result.Status);
        Assert.Contains("Created Product", result.ActionTaken, StringComparison.Ordinal);
        Assert.Equal(productName, await fixture.ReadProductNameAsync(result.MatchedProductId!.Value));
    }

    [Fact]
    public async Task SameSellerSourceIdDuplicateAndChangedContentAreRejectedExplicitly()
    {
        using var fixture = new ConsolidationFixture();
        var sourceId = Guid.NewGuid().ToString("D");
        var first = await fixture.ProcessEntriesAsync(new[]
        {
            Source("RepeatSeller", "Repeatable Item", "Canon", "Photography", sourceId),
            Source("RepeatSeller", "Repeatable Item", "Canon", "Photography", sourceId)
        });

        Assert.Equal(UploadItemStatus.Approved, first[0].Status);
        Assert.Equal(UploadItemStatus.Rejected, first[1].Status);
        Assert.Contains("Duplicate source entry", first[1].ActionTaken, StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM SellerProduct WHERE SellerName = $seller AND SellerProductId = $id;", ("$seller", "RepeatSeller"), ("$id", sourceId)));

        var conflict = await fixture.ProcessAsync(Source("RepeatSeller", "Changed Repeatable Item", "Canon", "Photography", sourceId));
        Assert.Equal(UploadItemStatus.Rejected, conflict.Status);
        Assert.Contains("Source-ID conflict", conflict.ActionTaken, StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM SellerProduct WHERE SellerName = $seller AND SellerProductId = $id;", ("$seller", "RepeatSeller"), ("$id", sourceId)));
    }

    [Fact]
    public async Task MalformedDocumentAndInvalidEntryBecomePersistedRejectedOutcomes()
    {
        using var fixture = new ConsolidationFixture();
        var malformed = await fixture.ProcessDocumentAsync("not-json", receivedCount: 1);
        Assert.Equal(UploadItemStatus.Rejected, malformed[0].Status);
        Assert.Contains("not valid UTF-8 JSON", malformed[0].ActionTaken, StringComparison.Ordinal);

        var invalidEntry = await fixture.ProcessDocumentAsync("[null]", receivedCount: 1);
        Assert.Equal(UploadItemStatus.Rejected, invalidEntry[0].Status);
        Assert.Contains("source array entry", invalidEntry[0].ActionTaken, StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM UploadItem WHERE UploadId = $uploadId;", ("$uploadId", fixture.LastUploadId.ToString("D"))));
    }

    [Fact]
    public async Task BlankRequiredFieldsAndMalformedGuidAreRejectedPrecisely()
    {
        using var fixture = new ConsolidationFixture();
        var entries = await fixture.ProcessDocumentAsync("[" +
            "{\"Id\":\"not-a-guid\",\"SellerName\":\"Seller\",\"Name\":\"Item\",\"Brand\":null,\"Category\":null}," +
            "{\"Id\":\"" + Guid.NewGuid().ToString("D") + "\",\"SellerName\":\"  \",\"Name\":\"Item\",\"Brand\":null,\"Category\":null}," +
            "{\"Id\":\"" + Guid.NewGuid().ToString("D") + "\",\"SellerName\":\"Seller\",\"Name\":\"  \",\"Brand\":null,\"Category\":null}]",
            receivedCount: 3);

        Assert.Equal(new[] { UploadItemStatus.Rejected, UploadItemStatus.Rejected, UploadItemStatus.Rejected }, entries.Select(entry => entry.Status));
        Assert.Equal("Id must be a GUID in D format.", entries[0].ActionTaken);
        Assert.Equal("SellerName is required.", entries[1].ActionTaken);
        Assert.Equal("Name is required.", entries[2].ActionTaken);
    }

    [Fact]
    public async Task WorkflowRecoverySkipsAtomicallyCommittedProductionOutcome()
    {
        using var fixture = new ConsolidationFixture();
        var canonicalProductId = await fixture.EnsureProductAsync("Workflow Catalog Phone", "Samsung", "Electronics");
        var sourceId = Guid.NewGuid().ToString("D");
        var upload = await fixture.CreateQueuedUploadAsync(JsonSerializer.Serialize(new[]
        {
            Source("WorkflowSeller", "Workflow Catalog Phone", "Samsung", "Electronics", sourceId)
        }));
        var workflowLock = new FileSystemWorkflowLock(fixture.Paths);
        var reportStore = new FileSystemReportFileStore(fixture.Paths);
        var workflow = new ConsolidationWorkflow(fixture.UploadStore, fixture.ItemStore, fixture.Processor, workflowLock, new UploadReportFinalizationService(fixture.UploadStore, fixture.ItemStore, reportStore));

        Assert.True(await workflow.ProcessNextAsync());
        var initialOutcome = Assert.Single(await fixture.ItemStore.GetByUploadIdAsync(upload.Id));
        Assert.Equal(UploadItemStatus.Approved, initialOutcome.Status);
        Assert.Equal(canonicalProductId, initialOutcome.MatchedProductId);
        Assert.Equal(UploadStatus.Completed, (await fixture.UploadStore.FindByIdAsync(upload.Id))!.Status);

        var finalOutcome = Assert.Single(await fixture.ItemStore.GetByUploadIdAsync(upload.Id));
        Assert.Equal(initialOutcome, finalOutcome);
        var finalUpload = await fixture.UploadStore.FindByIdAsync(upload.Id);
        Assert.NotNull(finalUpload);
        Assert.Equal(UploadStatus.Completed, finalUpload.Status);
        Assert.Equal(1, finalUpload.ApprovedCount);
        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM SellerProduct WHERE SellerName = $seller AND SellerProductId = $sourceId;", ("$seller", "WorkflowSeller"), ("$sourceId", sourceId)));
        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM Product WHERE Id = $id;", ("$id", canonicalProductId)));
    }

    [Fact]
    public async Task CatalogAndSellerWritesRollbackWhenItemOutcomeCannotCommit()
    {
        using var fixture = new ConsolidationFixture();
        var name = $"Atomic Failure Item {Guid.NewGuid():N}";
        var productCount = await fixture.CountAsync("SELECT COUNT(*) FROM Product;");
        await fixture.CreateUploadItemFailureTriggerAsync();

        await Assert.ThrowsAsync<SqliteException>(() => fixture.ProcessAsync(Source("AtomicSeller", name, "Canon", "Photography")));

        Assert.Equal(productCount, await fixture.CountAsync("SELECT COUNT(*) FROM Product;"));
        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM SellerProduct WHERE SellerName = $seller;", ("$seller", "AtomicSeller")));
        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM Product WHERE Name = $name;", ("$name", name)));
    }

    private static object Source(string seller, string name, string? brand, string? category, string? id = null) => new
    {
        Id = id ?? Guid.NewGuid().ToString("D"),
        SellerName = seller,
        Name = name,
        Brand = brand,
        Category = category
    };

    private sealed class ConsolidationFixture : IDisposable
    {
        private readonly string _temporaryDirectory = Path.Combine(Path.GetTempPath(), $"marketplace-consolidation-tests-{Guid.NewGuid():N}");
        private readonly SqliteConnectionFactory _connectionFactory;

        public ConsolidationFixture()
        {
            Directory.CreateDirectory(_temporaryDirectory);
            var starterPath = Path.Combine(AppContext.BaseDirectory, "catalog.db");
            Paths = new FileSystemStoragePaths(new CatalogStorageOptions(Path.Combine(_temporaryDirectory, "data"), starterPath));
            var workingPath = new SqliteWorkingDatabaseBootstrapper(Paths).EnsureWorkingDatabaseAsync().GetAwaiter().GetResult();
            new SqliteDatabaseMigrator().MigrateAsync(workingPath).GetAwaiter().GetResult();
            _connectionFactory = new SqliteConnectionFactory(workingPath);
            UploadStore = new SqliteUploadStore(_connectionFactory);
            ItemStore = new SqliteUploadItemStore(_connectionFactory);
            StagingStore = new FileSystemStagingFileStore(Paths);
            Processor = new SourceCatalogItemProcessor(StagingStore, ItemStore, new SqliteConsolidationItemStore(_connectionFactory), new SourceDocumentParser());
        }

        public FileSystemStoragePaths Paths { get; }
        public SqliteUploadStore UploadStore { get; }
        public SqliteUploadItemStore ItemStore { get; }
        public FileSystemStagingFileStore StagingStore { get; }
        public SourceCatalogItemProcessor Processor { get; }
        public Guid LastUploadId { get; private set; }

        public async Task<long> EnsureProductAsync(string name, string brand, string category)
        {
            var normalizedBrand = TextNormalization.NormalizeForComparison(brand);
            var normalizedName = TextNormalization.NormalizeForComparison(name);
            var normalizedCategory = TextNormalization.NormalizeForComparison(category);
            var existing = await ReadNullableInt64Async(
                "SELECT Id FROM Product WHERE NormalizedBrand = $brand AND NormalizedName = $name AND NormalizedCategory = $category LIMIT 1;",
                ("$brand", normalizedBrand),
                ("$name", normalizedName),
                ("$category", normalizedCategory));
            if (existing is not null)
            {
                return existing.Value;
            }

            var productCatalog = new SqliteProductCatalog(_connectionFactory);
            return await productCatalog.CreateAsync(new CatalogProduct(0, name, brand, category, normalizedName, normalizedBrand, normalizedCategory));
        }

        public async Task<UploadItemProcessingResult> ProcessAsync(object source) =>
            (await ProcessEntriesAsync(new[] { source }))[0];

        public async Task<IReadOnlyList<UploadItemProcessingResult>> ProcessEntriesAsync(IReadOnlyList<object> sources, int? receivedCount = null)
        {
            var json = JsonSerializer.Serialize(sources);
            return await ProcessDocumentAsync(json, receivedCount ?? sources.Count);
        }

        public async Task<IReadOnlyList<UploadItemProcessingResult>> ProcessDocumentAsync(string json, int receivedCount)
        {
            var upload = await CreateUploadAsync(json, receivedCount, UploadStatus.Processing);
            var results = new List<UploadItemProcessingResult>(receivedCount);
            for (var index = 0; index < receivedCount; index++)
            {
                results.Add(await Processor.ProcessAsync(upload, index));
            }

            return results;
        }

        public Task<UploadRecord> CreateQueuedUploadAsync(string json) =>
            CreateUploadAsync(json, 1, UploadStatus.Queued);

        private async Task<UploadRecord> CreateUploadAsync(string json, int receivedCount, UploadStatus status)
        {
            var uploadId = Guid.NewGuid();
            LastUploadId = uploadId;
            await using var content = new MemoryStream(Encoding.UTF8.GetBytes(json));
            var stagedPath = await StagingStore.StageAsync(uploadId, content);
            var upload = new UploadRecord(
                uploadId,
                Guid.NewGuid(),
                "source.json",
                "test-hash",
                stagedPath,
                Path.Combine(Paths.ReportDirectory, $"{uploadId:D}.json"),
                status,
                DateTimeOffset.UtcNow,
                null,
                receivedCount,
                0,
                0,
                0,
                Guid.NewGuid(),
                null,
                null,
                null);
            await UploadStore.CreateAsync(upload);
            return upload;
        }

        public async Task CreateUploadItemFailureTriggerAsync()
        {
            await using var connection = await _connectionFactory.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER FailUploadItemInsert BEFORE INSERT ON UploadItem BEGIN SELECT RAISE(ABORT, 'test outcome persistence failure'); END;";
            await command.ExecuteNonQueryAsync();
        }

        public async Task<long> CountAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = await _connectionFactory.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task<string?> ReadProductNameAsync(long productId)
        {
            await using var connection = await _connectionFactory.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Name FROM Product WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", productId);
            return (string?)await command.ExecuteScalarAsync();
        }

        public async Task<CatalogProduct?> FindByIdentityAsync(string brand, string category, string name)
        {
            await using var connection = await _connectionFactory.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id FROM Product WHERE NormalizedBrand = $brand AND NormalizedCategory = $category AND NormalizedName = $name LIMIT 1;";
            command.Parameters.AddWithValue("$brand", brand);
            command.Parameters.AddWithValue("$category", category);
            command.Parameters.AddWithValue("$name", name);
            var id = await command.ExecuteScalarAsync();
            if (id is null or DBNull)
            {
                return null;
            }

            var productId = Convert.ToInt64(id, System.Globalization.CultureInfo.InvariantCulture);
            await using var readCommand = connection.CreateCommand();
            readCommand.CommandText = "SELECT Id, Name, Brand, Category, NormalizedName, NormalizedBrand, NormalizedCategory FROM Product WHERE Id = $id;";
            readCommand.Parameters.AddWithValue("$id", productId);
            await using var reader = await readCommand.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return new CatalogProduct(reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6));
        }

        private async Task<long?> ReadNullableInt64Async(string sql, params (string Name, object Value)[] parameters)
        {
            var result = await CountOrNullAsync(sql, parameters);
            return result;
        }

        private async Task<long?> CountOrNullAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = await _connectionFactory.OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value);
            }

            var result = await command.ExecuteScalarAsync();
            return result is null or DBNull ? null : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
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
