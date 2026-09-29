using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceCatalogConsolidator.Application.Ports;
using MarketplaceCatalogConsolidator.Application.Reports;
using MarketplaceCatalogConsolidator.Application.Workflow;
using MarketplaceCatalogConsolidator.Domain;
using MarketplaceCatalogConsolidator.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace MarketplaceCatalogConsolidator.IntegrationTests;

public sealed class UploadReportFinalizationTests
{
    [Fact]
    public async Task CompletedWithRejectionsReportContainsMetadataSummaryAndStableItemOrder()
    {
        using var fixture = new ReportFixture();
        var upload = await fixture.CreateUploadAsync("../../unsafe-name.json", UploadStatus.ReportPending, UploadStatus.CompletedWithRejections, 3, 1, 1, 1);
        await fixture.SaveItemAsync(upload.Id, 2, UploadItemStatus.Rejected, "Rejected with reason.");
        await fixture.SaveItemAsync(upload.Id, 0, UploadItemStatus.Approved, "Approved and linked.");
        await fixture.SaveItemAsync(upload.Id, 1, UploadItemStatus.Cleaned, "Name: before -> after.");

        Assert.True(await fixture.Finalizer.FinalizeAsync(upload.Id));

        var finalUpload = (await fixture.UploadStore.FindByIdAsync(upload.Id))!;
        Assert.Equal(UploadStatus.CompletedWithRejections, finalUpload.Status);
        Assert.Equal(3, finalUpload.ReceivedCount);
        Assert.Equal(1, finalUpload.ApprovedCount);
        Assert.Equal(1, finalUpload.CleanedCount);
        Assert.Equal(1, finalUpload.RejectedCount);
        Assert.NotNull(finalUpload.ConsolidationFinishedAtUtc);
        Assert.NotNull(finalUpload.ReportGeneratedAtUtc);
        Assert.NotNull(finalUpload.CompletedAtUtc);
        Assert.Equal(Path.GetFullPath(fixture.ReportStore.GetReportPath(upload.Id)), Path.GetFullPath(finalUpload.ReportFilePath));
        Assert.StartsWith(Path.GetFullPath(fixture.Paths.ReportDirectory), Path.GetFullPath(finalUpload.ReportFilePath), PathComparison());

        var bytes = await File.ReadAllBytesAsync(finalUpload.ReportFilePath);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), finalUpload.ReportSha256);
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        Assert.Equal(upload.Id.ToString("D"), root.GetProperty("uploadId").GetString());
        Assert.Equal("../../unsafe-name.json", root.GetProperty("fileName").GetString());
        Assert.Equal("file-hash", root.GetProperty("fileHash").GetString());
        Assert.Equal(upload.TraceId.ToString("D"), root.GetProperty("traceId").GetString());
        Assert.Equal("CompletedWithRejections", root.GetProperty("status").GetString());
        Assert.Equal(3, root.GetProperty("summary").GetProperty("received").GetInt32());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("approved").GetInt32());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("cleaned").GetInt32());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("rejected").GetInt32());
        Assert.Equal(new[] { 0, 1, 2 }, root.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("sourceIndex").GetInt32()));
        Assert.Equal("Approved", root.GetProperty("items")[0].GetProperty("status").GetString());
        Assert.Equal("Cleaned", root.GetProperty("items")[1].GetProperty("status").GetString());
        Assert.Equal("Rejected", root.GetProperty("items")[2].GetProperty("status").GetString());
        Assert.Equal("Name: before -> after.", root.GetProperty("items")[1].GetProperty("actionTaken").GetString());
        AssertUtcIsoTimestamp(root.GetProperty("startedAtUtc").GetString());
        AssertUtcIsoTimestamp(root.GetProperty("consolidationFinishedAtUtc").GetString());
        AssertUtcIsoTimestamp(root.GetProperty("reportGeneratedAtUtc").GetString());
        AssertUtcIsoTimestamp(root.GetProperty("terminalAtUtc").GetString());
    }

    [Fact]
    public async Task FailedTerminalOutcomePublishesFailureReportBeforeFailedState()
    {
        using var fixture = new ReportFixture();
        var upload = await fixture.CreateUploadAsync("input.json", UploadStatus.ReportPending, UploadStatus.Failed, 1, 0, 0, 1, "workflow_failed", "Upload processing could not be completed.");
        await fixture.SaveItemAsync(upload.Id, 0, UploadItemStatus.Rejected, "Item rejected during processing.");

        Assert.True(await fixture.Finalizer.FinalizeAsync(upload.Id));

        var failed = (await fixture.UploadStore.FindByIdAsync(upload.Id))!;
        Assert.Equal(UploadStatus.Failed, failed.Status);
        Assert.True(File.Exists(failed.ReportFilePath));
        using var report = JsonDocument.Parse(await File.ReadAllBytesAsync(failed.ReportFilePath));
        Assert.Equal("Failed", report.RootElement.GetProperty("status").GetString());
        Assert.Equal("workflow_failed", report.RootElement.GetProperty("failureCode").GetString());
        Assert.Equal("Upload processing could not be completed.", report.RootElement.GetProperty("failureMessage").GetString());
        Assert.DoesNotContain("stack", report.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReportWriteFailureLeavesUploadPendingAndRetryCanFinish()
    {
        using var fixture = new ReportFixture();
        var upload = await fixture.CreateUploadAsync("input.json", UploadStatus.ReportPending, UploadStatus.Completed, 1, 1, 0, 0);
        await fixture.SaveItemAsync(upload.Id, 0, UploadItemStatus.Approved, "Approved.");
        var failingFinalizer = new UploadReportFinalizationService(fixture.UploadStore, fixture.ItemStore, new FaultingReportStore(fixture.ReportStore, FaultMode.FailBeforePublication));

        await Assert.ThrowsAsync<IOException>(() => failingFinalizer.FinalizeAsync(upload.Id));
        var stillPending = (await fixture.UploadStore.FindByIdAsync(upload.Id))!;
        Assert.Equal(UploadStatus.ReportPending, stillPending.Status);
        Assert.Null(stillPending.CompletedAtUtc);
        Assert.Null(stillPending.ReportSha256);
        Assert.False(File.Exists(fixture.ReportStore.GetReportPath(upload.Id)));
        Assert.True(File.Exists(upload.StagedFilePath));
        Assert.True(await fixture.UploadStore.RecordReportGenerationFailureAsync(upload.Id));
        Assert.Equal("report_generation_failed", (await fixture.UploadStore.FindByIdAsync(upload.Id))!.ReportGenerationFailureCode);

        Assert.True(await fixture.Finalizer.FinalizeAsync(upload.Id));
        Assert.Equal(UploadStatus.Completed, (await fixture.UploadStore.FindByIdAsync(upload.Id))!.Status);
        Assert.False(File.Exists(upload.StagedFilePath));
    }

    [Fact]
    public async Task PublishedReportBeforeDatabaseFinalizationIsRecoveredWithoutReplacingBytes()
    {
        using var fixture = new ReportFixture();
        var upload = await fixture.CreateUploadAsync("input.json", UploadStatus.ReportPending, UploadStatus.CompletedWithRejections, 1, 0, 0, 1);
        await fixture.SaveItemAsync(upload.Id, 0, UploadItemStatus.Rejected, "Rejected.");
        var interruptedFinalizer = new UploadReportFinalizationService(fixture.UploadStore, fixture.ItemStore, new FaultingReportStore(fixture.ReportStore, FaultMode.PublishThenInterrupt));

        await Assert.ThrowsAsync<SimulatedProcessInterruptionException>(() => interruptedFinalizer.FinalizeAsync(upload.Id));
        var pending = (await fixture.UploadStore.FindByIdAsync(upload.Id))!;
        Assert.Equal(UploadStatus.ReportPending, pending.Status);
        Assert.Null(pending.CompletedAtUtc);
        var publishedBytes = await File.ReadAllBytesAsync(fixture.ReportStore.GetReportPath(upload.Id));
        var publishedHash = SHA256.HashData(publishedBytes);

        var recovery = fixture.CreateRecoveryService();
        await recovery.RecoverAsync();

        var recovered = (await fixture.UploadStore.FindByIdAsync(upload.Id))!;
        Assert.Equal(UploadStatus.CompletedWithRejections, recovered.Status);
        Assert.Equal(Convert.ToHexString(publishedHash).ToLowerInvariant(), recovered.ReportSha256);
        Assert.Equal(publishedBytes, await File.ReadAllBytesAsync(recovered.ReportFilePath));
    }

    [Fact]
    public async Task RepeatedFinalizationReusesMatchingImmutableReportBytesAndHash()
    {
        using var fixture = new ReportFixture();
        var upload = await fixture.CreateUploadAsync("input.json", UploadStatus.ReportPending, UploadStatus.Completed, 1, 1, 0, 0);
        await fixture.SaveItemAsync(upload.Id, 0, UploadItemStatus.Approved, "Approved.");

        await fixture.Finalizer.FinalizeAsync(upload.Id);
        var completed = (await fixture.UploadStore.FindByIdAsync(upload.Id))!;
        var bytes = await File.ReadAllBytesAsync(completed.ReportFilePath);
        var hash = completed.ReportSha256;
        await fixture.Finalizer.FinalizeAsync(upload.Id);

        var repeated = (await fixture.UploadStore.FindByIdAsync(upload.Id))!;
        Assert.Equal(UploadStatus.Completed, repeated.Status);
        Assert.Equal(hash, repeated.ReportSha256);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(repeated.ReportFilePath));
        Assert.Single(Directory.GetFiles(fixture.Paths.ReportDirectory, "*.json", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task StartupCleansOnlyReportTemporaryFilesAndNeverExposesThemAsFinalReports()
    {
        using var fixture = new ReportFixture();
        var upload = await fixture.CreateUploadAsync("input.json", UploadStatus.ReportPending, UploadStatus.Completed, 0, 0, 0, 0);
        Directory.CreateDirectory(fixture.Paths.ReportDirectory);
        var expectedTemporary = Path.Combine(fixture.Paths.ReportDirectory, $".{upload.Id:D}.json.{Guid.NewGuid():N}.tmp");
        var unrelated = Path.Combine(fixture.Paths.ReportDirectory, ".unrelated.tmp");
        await File.WriteAllTextAsync(expectedTemporary, "partial report");
        await File.WriteAllTextAsync(unrelated, "unrelated file");

        await fixture.CreateRecoveryService().RecoverAsync();

        Assert.False(File.Exists(expectedTemporary));
        Assert.True(File.Exists(unrelated));
        Assert.Equal(UploadStatus.Completed, (await fixture.UploadStore.FindByIdAsync(upload.Id))!.Status);
        Assert.True(File.Exists(fixture.ReportStore.GetReportPath(upload.Id)));
        Assert.True(await fixture.Finalizer.FinalizeAsync(upload.Id));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task MigrationReopensLegacyTerminalUploadWithoutReportHash()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"marketplace-legacy-report-migration-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var databasePath = Path.Combine(temporaryRoot, "legacy.db");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "catalog.db"), databasePath);
            var connectionFactory = new SqliteConnectionFactory(databasePath);
            var completedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10);
            await using (var connection = await connectionFactory.OpenConnectionAsync())
            {
                await using var setup = connection.CreateCommand();
                setup.CommandText = "CREATE TABLE SchemaMigration (MigrationId TEXT NOT NULL PRIMARY KEY, AppliedAtUtc TEXT NOT NULL); " +
                    "INSERT INTO SchemaMigration (MigrationId, AppliedAtUtc) VALUES ('202609290001_InitialCatalogAndUploadSchema', $appliedAtUtc); " +
                    "CREATE TABLE Upload (Id TEXT NOT NULL PRIMARY KEY, IdempotencyKey TEXT NOT NULL UNIQUE, FileName TEXT NOT NULL, FileHash TEXT NOT NULL, StagedFilePath TEXT NOT NULL, ReportFilePath TEXT NOT NULL, Status TEXT NOT NULL, StartedAtUtc TEXT NOT NULL, CompletedAtUtc TEXT NULL, ReceivedCount INTEGER NOT NULL DEFAULT 0, ApprovedCount INTEGER NOT NULL DEFAULT 0, CleanedCount INTEGER NOT NULL DEFAULT 0, RejectedCount INTEGER NOT NULL DEFAULT 0, TraceId TEXT NOT NULL, FailureCode TEXT NULL, FailureMessage TEXT NULL, ReportSha256 TEXT NULL); " +
                    "INSERT INTO Upload (Id, IdempotencyKey, FileName, FileHash, StagedFilePath, ReportFilePath, Status, StartedAtUtc, CompletedAtUtc, ReceivedCount, ApprovedCount, CleanedCount, RejectedCount, TraceId) VALUES ($id, $idempotencyKey, 'legacy.json', 'old-hash', 'old-stage', 'old-report', 'Completed', $startedAtUtc, $completedAtUtc, 2, 1, 1, 0, $traceId);";
                setup.Parameters.AddWithValue("$appliedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
                setup.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                setup.Parameters.AddWithValue("$idempotencyKey", Guid.NewGuid().ToString("D"));
                setup.Parameters.AddWithValue("$startedAtUtc", completedAtUtc.AddMinutes(-1).ToString("O"));
                setup.Parameters.AddWithValue("$completedAtUtc", completedAtUtc.ToString("O"));
                setup.Parameters.AddWithValue("$traceId", Guid.NewGuid().ToString("D"));
                await setup.ExecuteNonQueryAsync();
            }

            await new SqliteDatabaseMigrator().MigrateAsync(databasePath);

            await using var migrated = await connectionFactory.OpenConnectionAsync();
            await using var select = migrated.CreateCommand();
            select.CommandText = "SELECT Status, IntendedTerminalStatus, ConsolidationFinishedAtUtc, CompletedAtUtc, ReportSha256 FROM Upload WHERE FileName = $fileName;";
            select.Parameters.AddWithValue("$fileName", "legacy.json");
            await using var reader = await select.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(UploadStatus.ReportPending.ToString(), reader.GetString(0));
            Assert.Equal(UploadStatus.Completed.ToString(), reader.GetString(1));
            Assert.Equal(completedAtUtc.ToString("O"), reader.GetString(2));
            Assert.True(reader.IsDBNull(3));
            Assert.True(reader.IsDBNull(4));
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    private static StringComparison PathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static void AssertUtcIsoTimestamp(string? timestamp)
    {
        Assert.NotNull(timestamp);
        Assert.True(DateTimeOffset.TryParse(timestamp, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed));
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.Equal(parsed.ToString("O", System.Globalization.CultureInfo.InvariantCulture), timestamp);
    }

    private enum FaultMode
    {
        FailBeforePublication,
        PublishThenInterrupt
    }

    private sealed class FaultingReportStore(IReportFileStore inner, FaultMode mode) : IReportFileStore
    {
        private int _writeCount;

        public string GetReportPath(Guid uploadId) => inner.GetReportPath(uploadId);
        public Task<bool> ExistsAsync(Guid uploadId, CancellationToken cancellationToken = default) => inner.ExistsAsync(uploadId, cancellationToken);
        public Task<Stream?> OpenReadIfExistsAsync(Guid uploadId, CancellationToken cancellationToken = default) => inner.OpenReadIfExistsAsync(uploadId, cancellationToken);
        public Task<Stream> OpenReadAsync(Guid uploadId, CancellationToken cancellationToken = default) => inner.OpenReadAsync(uploadId, cancellationToken);
        public Task CleanupTemporaryFilesAsync(CancellationToken cancellationToken = default) => inner.CleanupTemporaryFilesAsync(cancellationToken);

        public async Task WriteAtomicallyAsync(Guid uploadId, Stream content, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writeCount) > 1)
            {
                await inner.WriteAtomicallyAsync(uploadId, content, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (mode == FaultMode.FailBeforePublication)
            {
                throw new IOException("Injected write failure before publication.");
            }

            await inner.WriteAtomicallyAsync(uploadId, content, cancellationToken).ConfigureAwait(false);
            throw new SimulatedProcessInterruptionException();
        }
    }

    private sealed class SimulatedProcessInterruptionException : Exception;

    private sealed class ReportFixture : IDisposable
    {
        private readonly string _temporaryRoot = Path.Combine(Path.GetTempPath(), $"marketplace-report-tests-{Guid.NewGuid():N}");
        private readonly SqliteConnectionFactory _connectionFactory;

        public ReportFixture()
        {
            Directory.CreateDirectory(_temporaryRoot);
            Paths = new FileSystemStoragePaths(new CatalogStorageOptions(
                Path.Combine(_temporaryRoot, "data"),
                Path.Combine(AppContext.BaseDirectory, "catalog.db")));
            var databasePath = new SqliteWorkingDatabaseBootstrapper(Paths).EnsureWorkingDatabaseAsync().GetAwaiter().GetResult();
            new SqliteDatabaseMigrator().MigrateAsync(databasePath).GetAwaiter().GetResult();
            _connectionFactory = new SqliteConnectionFactory(databasePath);
            UploadStore = new SqliteUploadStore(_connectionFactory);
            ItemStore = new SqliteUploadItemStore(_connectionFactory);
            ReportStore = new FileSystemReportFileStore(Paths);
            StagingStore = new FileSystemStagingFileStore(Paths);
            Finalizer = new UploadReportFinalizationService(UploadStore, ItemStore, ReportStore, StagingStore);
        }

        public FileSystemStoragePaths Paths { get; }
        public SqliteUploadStore UploadStore { get; }
        public SqliteUploadItemStore ItemStore { get; }
        public FileSystemReportFileStore ReportStore { get; }
        public FileSystemStagingFileStore StagingStore { get; }
        public UploadReportFinalizationService Finalizer { get; }

        public async Task<UploadRecord> CreateUploadAsync(
            string fileName,
            UploadStatus status,
            UploadStatus intendedStatus,
            int received,
            int approved,
            int cleaned,
            int rejected,
            string? failureCode = null,
            string? failureMessage = null)
        {
            var uploadId = Guid.NewGuid();
            var startedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
            var finishedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await using var stagedContent = new MemoryStream(Encoding.UTF8.GetBytes("[]"));
            var stagedFilePath = await StagingStore.StageAsync(uploadId, stagedContent);
            var upload = new UploadRecord(
                uploadId,
                Guid.NewGuid(),
                fileName,
                "file-hash",
                stagedFilePath,
                ReportStore.GetReportPath(uploadId),
                status,
                startedAt,
                null,
                received,
                approved,
                cleaned,
                rejected,
                Guid.NewGuid(),
                failureCode,
                failureMessage,
                null,
                intendedStatus,
                finishedAt);
            await UploadStore.CreateAsync(upload);
            return upload;
        }

        public Task SaveItemAsync(Guid uploadId, int sourceIndex, UploadItemStatus status, string action) =>
            ItemStore.SaveProgressAsync(new UploadItemRecord(
                uploadId,
                sourceIndex,
                $"source-{sourceIndex}",
                $"seller-{sourceIndex}",
                $"name-{sourceIndex}",
                $"brand-{sourceIndex}",
                $"category-{sourceIndex}",
                $"clean-seller-{sourceIndex}",
                $"clean-name-{sourceIndex}",
                $"clean-brand-{sourceIndex}",
                $"clean-category-{sourceIndex}",
                status,
                action,
                100 + sourceIndex));

        public StartupRecoveryService CreateRecoveryService() =>
            new(UploadStore, new FileSystemWorkflowLock(Paths), ReportStore, Finalizer);

        public void Dispose()
        {
            if (Directory.Exists(_temporaryRoot))
            {
                Directory.Delete(_temporaryRoot, recursive: true);
            }
        }
    }
}
