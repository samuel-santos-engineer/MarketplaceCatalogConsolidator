using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using MarketplaceCatalogConsolidator.Application.Reports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace MarketplaceCatalogConsolidator.IntegrationTests;

public sealed class SuppliedFixtureHttpEndToEndTests
{
    private const string ApiKey = "test-supplied-fixture-api-key";
    private const int ExpectedItemCount = 269;

    [Fact]
    public async Task SuppliedFixtureThroughHttpDoesNotAddDuplicateCanonicalIdentitiesAndPersistsExactlyReportedSellerLinks()
    {
        var projectRoot = FindProjectRoot();
        var artifactDirectory = Path.Combine(projectRoot, "artifacts");
        var starterPath = Path.Combine(artifactDirectory, "catalog.db");
        var inputPath = Path.Combine(artifactDirectory, "ProductEntry.json");
        var starterHashBefore = await HashFileAsync(starterPath);
        var artifactSnapshotBefore = await SnapshotDirectoryAsync(artifactDirectory);

        await using var fixture = new Fixture(starterPath);
        var baselineIdentities = await ReadIdentityCountsAsync(fixture.WorkingDatabasePath);
        Assert.Equal(975, await CountAsync(fixture.WorkingDatabasePath, "SELECT COUNT(*) FROM Product;"));
        Assert.Equal(0, await CountAsync(fixture.WorkingDatabasePath, "SELECT COUNT(*) FROM SellerProduct;"));

        var inputBytes = await File.ReadAllBytesAsync(inputPath);
        using var multipart = new MultipartFormDataContent();
        using var file = new ByteArrayContent(inputBytes);
        file.Headers.ContentType = new("application/json");
        multipart.Add(file, "file", Path.GetFileName(inputPath));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/uploads") { Content = multipart };
        request.Headers.Add("X-Api-Key", ApiKey);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));

        using var accepted = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using var acceptedJson = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync());
        var uploadId = acceptedJson.RootElement.GetProperty("uploadId").GetGuid();

        var finalStatus = await PollUntilReportAvailableAsync(fixture.Client, uploadId);
        using var reportResponse = await fixture.Client.GetAsync($"/api/v1/uploads/{uploadId:D}/report");
        Assert.Equal(HttpStatusCode.OK, reportResponse.StatusCode);
        var reportBytes = await reportResponse.Content.ReadAsByteArrayAsync();
        var report = JsonSerializer.Deserialize<UploadReportDocument>(reportBytes);
        Assert.NotNull(report);

        Assert.Equal(uploadId.ToString("D"), report.UploadId);
        Assert.Equal(ExpectedItemCount, report.Items.Count);
        AssertSummary(report.Summary);
        var statusUpload = finalStatus.GetProperty("upload");
        var statusSummary = statusUpload.GetProperty("summary");
        Assert.Equal(uploadId, statusUpload.GetProperty("uploadId").GetGuid());
        Assert.Equal(report.Status, statusUpload.GetProperty("status").GetString());
        Assert.True(statusUpload.GetProperty("reportAvailable").GetBoolean());
        Assert.Equal(report.Summary.Received, statusSummary.GetProperty("received").GetInt32());
        Assert.Equal(report.Summary.Approved, statusSummary.GetProperty("approved").GetInt32());
        Assert.Equal(report.Summary.Cleaned, statusSummary.GetProperty("cleaned").GetInt32());
        Assert.Equal(report.Summary.Rejected, statusSummary.GetProperty("rejected").GetInt32());

        var durableUpload = await ReadUploadAsync(fixture.WorkingDatabasePath, uploadId);
        Assert.Equal(report.Status, durableUpload.Status);
        Assert.Equal(report.Summary.Received, durableUpload.Received);
        Assert.Equal(report.Summary.Approved, durableUpload.Approved);
        Assert.Equal(report.Summary.Cleaned, durableUpload.Cleaned);
        Assert.Equal(report.Summary.Rejected, durableUpload.Rejected);
        Assert.True(durableUpload.ReportAvailable);

        // Historical duplicate identities may exist; this import must not increase any
        // complete identity beyond its prior count, or one row when the identity is new.
        var postImportIdentities = await ReadIdentityCountsAsync(fixture.WorkingDatabasePath);
        foreach (var (identity, postImportCount) in postImportIdentities)
        {
            baselineIdentities.TryGetValue(identity, out var baselineCount);
            Assert.True(postImportCount <= Math.Max(baselineCount, 1),
                $"Identity '{identity}' increased from {baselineCount} to {postImportCount} rows.");
        }

        var acceptedItems = report.Items.Where(item => item.Status is "Approved" or "Cleaned").ToArray();
        var expectedLinks = acceptedItems
            .GroupBy(item => new SellerLinkKey(
                Assert.IsType<string>(item.CleanedSellerName),
                Assert.IsType<string>(item.Id)))
            .ToDictionary(
                group => group.Key,
                group => Assert.Single(group.Select(item => Assert.IsType<long>(item.CanonicalProductId)).Distinct()));
        var actualLinks = await ReadSellerLinksAsync(fixture.WorkingDatabasePath);
        Assert.Equal(expectedLinks.Count, actualLinks.Count);
        foreach (var (key, expectedProductId) in expectedLinks)
        {
            Assert.True(actualLinks.TryGetValue(key, out var actualProductId), $"Missing seller link '{key}'.");
            Assert.Equal(expectedProductId, actualProductId);
        }

        foreach (var rejected in report.Items.Where(item => item.Status == "Rejected"))
        {
            if (rejected.CleanedSellerName is not null && rejected.Id is not null
                && !expectedLinks.ContainsKey(new SellerLinkKey(rejected.CleanedSellerName, rejected.Id)))
            {
                Assert.DoesNotContain(new SellerLinkKey(rejected.CleanedSellerName, rejected.Id), actualLinks.Keys);
            }
        }

        AssertPathUnderRoot(fixture.WorkingDatabasePath, fixture.StorageRoot);
        AssertPathUnderRoot(durableUpload.StagedFilePath, fixture.StorageRoot);
        AssertPathUnderRoot(durableUpload.ReportFilePath, fixture.StorageRoot);
        Assert.All(Directory.EnumerateFiles(fixture.StorageRoot, "*", SearchOption.AllDirectories),
            path => AssertPathUnderRoot(path, fixture.StorageRoot));
        Assert.Equal(starterHashBefore, await HashFileAsync(starterPath));
        Assert.Equal(artifactSnapshotBefore, await SnapshotDirectoryAsync(artifactDirectory));
    }

    private static void AssertSummary(UploadReportSummary summary)
    {
        Assert.Equal(ExpectedItemCount, summary.Received);
        Assert.Equal(summary.Received, summary.Approved + summary.Cleaned + summary.Rejected);
    }

    private static async Task<JsonElement> PollUntilReportAvailableAsync(HttpClient client, Guid uploadId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (true)
        {
            using var response = await client.GetAsync($"/api/v1/uploads/{uploadId:D}/status", timeout.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var upload = document.RootElement.GetProperty("upload");
            var terminal = upload.GetProperty("status").GetString() is "Completed" or "CompletedWithRejections" or "Failed";
            if (terminal && upload.GetProperty("reportAvailable").GetBoolean())
            {
                return document.RootElement.Clone();
            }

            Assert.True(await timer.WaitForNextTickAsync(timeout.Token), "Status polling timer ended before completion.");
        }
    }

    private static async Task<Dictionary<string, long>> ReadIdentityCountsAsync(string databasePath)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT NormalizedBrand, NormalizedName, NormalizedCategory, COUNT(*)
            FROM Product
            WHERE NormalizedBrand IS NOT NULL AND NormalizedName IS NOT NULL AND NormalizedCategory IS NOT NULL
            GROUP BY NormalizedBrand, NormalizedName, NormalizedCategory;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(string.Join('\u001f', reader.GetString(0), reader.GetString(1), reader.GetString(2)), reader.GetInt64(3));
        }

        return result;
    }

    private static async Task<Dictionary<SellerLinkKey, long>> ReadSellerLinksAsync(string databasePath)
    {
        var result = new Dictionary<SellerLinkKey, long>();
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SellerName, SellerProductId, ProductId FROM SellerProduct;";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new SellerLinkKey(reader.GetString(0), reader.GetString(1)), reader.GetInt64(2));
        }

        return result;
    }

    private static async Task<DurableUpload> ReadUploadAsync(string databasePath, Guid uploadId)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Status, ReceivedCount, ApprovedCount, CleanedCount, RejectedCount,
                   StagedFilePath, ReportFilePath, CompletedAtUtc, ReportGeneratedAtUtc, ReportSha256
            FROM Upload WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", uploadId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new DurableUpload(
            reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
            reader.GetString(5), reader.GetString(6),
            !reader.IsDBNull(7) && !reader.IsDBNull(8) && !reader.IsDBNull(9));
    }

    private static async Task<long> CountAsync(string databasePath, string sql)
    {
        await using var connection = await OpenAsync(databasePath);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<SqliteConnection> OpenAsync(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<string> HashFileAsync(string path) =>
        Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));

    private static async Task<Dictionary<string, string>> SnapshotDirectoryAsync(string directory)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            result.Add(Path.GetRelativePath(directory, path), await HashFileAsync(path));
        }

        return result;
    }

    private static string FindProjectRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MarketplaceCatalogConsolidator.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("The solution root could not be located.");
    }

    private static void AssertPathUnderRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        Assert.False(Path.IsPathRooted(relative));
        Assert.NotEqual("..", relative);
        Assert.False(relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _temporaryRoot = Path.Combine(Path.GetTempPath(), $"marketplace-http-e2e-{Guid.NewGuid():N}");
        private readonly WebApplicationFactory<Program> _factory;

        public Fixture(string starterPath)
        {
            StorageRoot = Path.Combine(_temporaryRoot, "data");
            Directory.CreateDirectory(_temporaryRoot);
            _factory = new Factory(StorageRoot, starterPath);
            Client = _factory.CreateClient();
        }

        public HttpClient Client { get; }
        public string StorageRoot { get; }
        public string WorkingDatabasePath => Path.Combine(StorageRoot, "catalog.db");

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _factory.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_temporaryRoot))
            {
                Directory.Delete(_temporaryRoot, recursive: true);
            }
        }
    }

    private sealed class Factory(string storageRoot, string starterPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKey"] = ApiKey,
                ["Catalog:StorageRoot"] = storageRoot,
                ["Catalog:StarterDatabasePath"] = starterPath
            }));
        }
    }

    private sealed record DurableUpload(string Status, int Received, int Approved, int Cleaned, int Rejected,
        string StagedFilePath, string ReportFilePath, bool ReportAvailable);

    private sealed record SellerLinkKey(string SellerName, string SellerProductId);
}
