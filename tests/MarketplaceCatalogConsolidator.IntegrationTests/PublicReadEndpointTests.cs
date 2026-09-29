using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MarketplaceCatalogConsolidator.Application.Ports;
using MarketplaceCatalogConsolidator.Application.Workflow;
using MarketplaceCatalogConsolidator.Domain;
using MarketplaceCatalogConsolidator.Infrastructure.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace MarketplaceCatalogConsolidator.IntegrationTests;

public sealed class PublicReadEndpointTests
{
    [Fact]
    public async Task UploadListOrdersTiesFiltersAndHidesSensitiveData()
    {
        using var fixture = new Fixture();
        var first = fixture.Upload(Guid.Parse("00000000-0000-4000-8000-000000000001"));
        var second = fixture.Upload(Guid.Parse("00000000-0000-4000-8000-000000000002"));
        var newest = fixture.Upload(Guid.NewGuid()) with { StartedAtUtc = first.StartedAtUtc.AddDays(1), Status = UploadStatus.Failed };
        foreach (var upload in new[] { second, newest, first }) await fixture.Uploads.CreateAsync(upload);
        using var list = await fixture.GetJsonAsync("/api/v1/uploads?pageSize=1");
        Assert.Equal(newest.Id.ToString(), list.RootElement.GetProperty("items")[0].GetProperty("uploadId").GetString());
        Assert.Equal(3, list.RootElement.GetProperty("totalCount").GetInt32());
        using var filtered = await fixture.GetJsonAsync("/api/v1/uploads?status=queued&pageSize=1");
        Assert.Equal(second.Id.ToString(), filtered.RootElement.GetProperty("items")[0].GetProperty("uploadId").GetString());
        using var next = await fixture.GetJsonAsync("/api/v1/uploads?status=Queued&pageSize=1&page=2");
        Assert.Equal(first.Id.ToString(), next.RootElement.GetProperty("items")[0].GetProperty("uploadId").GetString());
        foreach (var forbidden in new[] { "idempotencyKey", "stagedFilePath", "reportFilePath", "failureMessage", "fileHash", "ApiKey" })
            Assert.DoesNotContain(forbidden, list.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("page=abc")]
    [InlineData("page=2147483648")]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=")]
    public async Task InvalidPaginationUsesStandardEnvelope(string query)
    {
        using var fixture = new Fixture();
        var upload = fixture.Upload(Guid.NewGuid());
        await fixture.Uploads.CreateAsync(upload);
        foreach (var route in new[] { "/api/v1/uploads", "/api/v1/catalog", $"/api/v1/uploads/{upload.Id}/status" })
            await AssertErrorAsync(fixture.Client, route + "?" + query, HttpStatusCode.BadRequest, "invalid_pagination");
    }

    [Fact]
    public async Task UploadOrderingPreservesSubMillisecondPrecisionAndUtcInstants()
    {
        using var fixture = new Fixture();
        var older = fixture.Upload(Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff"));
        var newer = fixture.Upload(Guid.Parse("00000000-0000-4000-8000-000000000001")) with { StartedAtUtc = older.StartedAtUtc.AddTicks(1).ToOffset(TimeSpan.FromHours(-3)) };
        await fixture.Uploads.CreateAsync(older);
        await fixture.Uploads.CreateAsync(newer);
        using var result = await fixture.GetJsonAsync("/api/v1/uploads");
        Assert.Equal(newer.Id.ToString(), result.RootElement.GetProperty("items")[0].GetProperty("uploadId").GetString());
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("1")]
    public async Task InvalidUploadStatusIsRejected(string status)
    {
        using var fixture = new Fixture();
        await AssertErrorAsync(fixture.Client, "/api/v1/uploads?status=" + status, HttpStatusCode.BadRequest, "invalid_status");
    }

    [Fact]
    public async Task StatusReturnsPersistedOrderedItemsAndSafeSummary()
    {
        using var fixture = new Fixture();
        var upload = fixture.Upload(Guid.NewGuid()) with { ReceivedCount = 3, ApprovedCount = 1, CleanedCount = 1, RejectedCount = 1, FailureCode = "internal_detail", FailureMessage = fixture.Paths.RootDirectory };
        await fixture.Uploads.CreateAsync(upload);
        var items = fixture.Factory.Services.GetRequiredService<IUploadItemStore>();
        foreach (var index in new[] { 2, 0, 1 })
            await items.SaveProgressAsync(new UploadItemRecord(upload.Id, index, "source-id", " Raw Seller ", " Raw Name ", "Brand", "Category", "Seller", "Name", "Brand", "Category", UploadItemStatus.Cleaned, "Cleaned name", 2));
        using var status = await fixture.GetJsonAsync($"/api/v1/uploads/{upload.Id}/status?pageSize=2");
        Assert.Equal(upload.TraceId.ToString(), status.RootElement.GetProperty("traceId").GetString());
        Assert.Equal(3, status.RootElement.GetProperty("upload").GetProperty("summary").GetProperty("received").GetInt32());
        Assert.Equal(1, status.RootElement.GetProperty("upload").GetProperty("summary").GetProperty("rejected").GetInt32());
        var page = status.RootElement.GetProperty("items");
        Assert.Equal(3, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(new[] { 0, 1 }, page.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("sourceIndex").GetInt32()));
        Assert.Equal(" Raw Name ", page.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal("Name", page.GetProperty("items")[0].GetProperty("cleanedName").GetString());
        Assert.Equal(2, page.GetProperty("items")[0].GetProperty("canonicalProductId").GetInt32());
        Assert.DoesNotContain(fixture.Paths.RootDirectory, status.RootElement.GetRawText());
        using var next = await fixture.GetJsonAsync($"/api/v1/uploads/{upload.Id}/status?pageSize=2&page=2");
        Assert.Equal(2, next.RootElement.GetProperty("items").GetProperty("items")[0].GetProperty("sourceIndex").GetInt32());
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("00000000-0000-4000-8000-000000000001")]
    [InlineData("..%2F..%2Fstaged.json")]
    public async Task UnknownAndInvalidIdsNeverServeFiles(string id)
    {
        using var fixture = new Fixture();
        foreach (var suffix in new[] { "status", "report" })
            await AssertErrorAsync(fixture.Client, $"/api/v1/uploads/{id}/{suffix}", HttpStatusCode.NotFound, "upload_not_found");
    }

    [Fact]
    public async Task ReportReturnsExactBytesAndRejectsUnavailableOrCorruptMetadata()
    {
        using var fixture = new Fixture();
        var upload = fixture.Upload(Guid.NewGuid());
        await fixture.Uploads.CreateAsync(upload);
        await AssertErrorAsync(fixture.Client, $"/api/v1/uploads/{upload.Id}/report", HttpStatusCode.Conflict, "report_not_available");
        await fixture.Uploads.UpdateAsync(upload with { Status = UploadStatus.Processing });
        Assert.True(await fixture.Uploads.BeginReportFinalizationAsync(upload.Id, UploadStatus.Completed, 0, 0, 0, upload.StartedAtUtc, null, null));
        await AssertErrorAsync(fixture.Client, $"/api/v1/uploads/{upload.Id}/report", HttpStatusCode.Conflict, "report_not_available");
        Assert.True(await fixture.Factory.Services.GetRequiredService<UploadReportFinalizationService>().FinalizeAsync(upload.Id));
        upload = (await fixture.Uploads.FindByIdAsync(upload.Id))!;
        var bytes = await File.ReadAllBytesAsync(upload.ReportFilePath);
        using var response = await fixture.Client.GetAsync($"/api/v1/uploads/{upload.Id}/report");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
        using var status = await fixture.GetJsonAsync($"/api/v1/uploads/{upload.Id}/status");
        Assert.True(status.RootElement.GetProperty("upload").GetProperty("reportAvailable").GetBoolean());
        await fixture.Uploads.UpdateAsync(upload with { ReportFilePath = fixture.Paths.WorkingDatabasePath });
        await AssertErrorAsync(fixture.Client, $"/api/v1/uploads/{upload.Id}/report", HttpStatusCode.InternalServerError, "report_unavailable");
        await fixture.Uploads.UpdateAsync(upload with { ReportFilePath = Path.Combine(fixture.Paths.UploadDirectory, $"{upload.Id}.json") });
        await AssertErrorAsync(fixture.Client, $"/api/v1/uploads/{upload.Id}/report", HttpStatusCode.InternalServerError, "report_unavailable");
        await fixture.Uploads.UpdateAsync(upload with { ReportSha256 = new string('0', 64) });
        await AssertErrorAsync(fixture.Client, $"/api/v1/uploads/{upload.Id}/report", HttpStatusCode.InternalServerError, "report_unavailable");
        await fixture.Uploads.UpdateAsync(upload);
        File.Delete(upload.ReportFilePath);
        await AssertErrorAsync(fixture.Client, $"/api/v1/uploads/{upload.Id}/report", HttpStatusCode.InternalServerError, "report_unavailable");
        Assert.False(File.Exists(upload.ReportFilePath));
    }

    [Fact]
    public async Task CatalogFiltersPaginationAndSellerOffersAreStable()
    {
        using var fixture = new Fixture();
        var catalog = fixture.Factory.Services.GetRequiredService<IProductCatalog>();
        var first = await catalog.CreateAsync(new CatalogProduct(0, "Test   Camera", "TEST Brand", "TEST Category", "test camera", "test brand", "test category"));
        var second = await catalog.CreateAsync(new CatalogProduct(0, "Test Camera Two", "TEST Brand", "TEST Category", "test camera two", "test brand", "test category"));
        var offers = fixture.Factory.Services.GetRequiredService<ISellerOfferStore>();
        foreach (var id in new[] { first, second })
            foreach (var seller in new[] { "Test Seller", "Other Seller" })
                await offers.AddAsync(new SellerOffer(null, seller, id, Guid.NewGuid().ToString(), "fingerprint", DateTimeOffset.Parse("2026-01-01T00:00:00Z")));
        foreach (var query in new[] { "category=%20test%20%20category%20", "brand=test%20brand", "name=test%20camera", "sellerName=test%20seller", "category=test%20category&brand=test%20brand&name=camera&sellerName=test%20seller" })
        {
            using var result = await fixture.GetJsonAsync("/api/v1/catalog?" + query);
            Assert.Equal(2, result.RootElement.GetProperty("totalCount").GetInt32());
            Assert.Equal(new[] { first, second }, result.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt64()));
            foreach (var item in result.RootElement.GetProperty("items").EnumerateArray())
            {
                var sellerOffers = item.GetProperty("sellerOffers");
                Assert.Equal(query.Contains("sellerName", StringComparison.Ordinal) ? 1 : 2, sellerOffers.GetArrayLength());
                if (query.Contains("sellerName", StringComparison.Ordinal)) Assert.Equal("Test Seller", sellerOffers[0].GetProperty("sellerName").GetString());
            }
        }
        using var page = await fixture.GetJsonAsync("/api/v1/catalog?brand=test%20brand&pageSize=1&page=2");
        Assert.Equal(second, page.RootElement.GetProperty("items")[0].GetProperty("id").GetInt64());
        using var injection = await fixture.GetJsonAsync("/api/v1/catalog?name=%27%20OR%201%3D1%20--");
        Assert.Equal(0, injection.RootElement.GetProperty("totalCount").GetInt32());
        using var literalWildcard = await fixture.GetJsonAsync("/api/v1/catalog?name=%25");
        Assert.Equal(0, literalWildcard.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"uploadId\":\"00000000-0000-4000-8000-000000000001\",\"status\":\"Completed\"}")]
    public async Task HashMatchingButInvalidReportsFailSafely(string content)
    {
        using var fixture = new Fixture();
        var bytes = Encoding.UTF8.GetBytes(content);
        var upload = fixture.Upload(Guid.NewGuid());
        upload = upload with { Status = UploadStatus.Completed, CompletedAtUtc = upload.StartedAtUtc, ReportGeneratedAtUtc = upload.StartedAtUtc, ReportSha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
        await fixture.Uploads.CreateAsync(upload);
        await fixture.Factory.Services.GetRequiredService<IReportFileStore>().WriteAtomicallyAsync(upload.Id, new MemoryStream(bytes));
        await AssertErrorAsync(fixture.Client, $"/api/v1/uploads/{upload.Id}/report", HttpStatusCode.InternalServerError, "report_unavailable");
        Assert.Equal(bytes, await File.ReadAllBytesAsync(upload.ReportFilePath));
    }

    [Fact]
    public async Task HealthIsIndependentAndReadinessChecksRealAndInjectedDependencies()
    {
        using var fixture = new Fixture();
        using var ready = await fixture.GetJsonAsync("/api/v1/ready");
        Assert.Equal("ready", ready.RootElement.GetProperty("status").GetString());
        Assert.Empty(Directory.GetFiles(fixture.Paths.ReportDirectory));
        Assert.Empty(Directory.GetFiles(fixture.Paths.UploadDirectory));
        using var broken = new Fixture(failReadiness: true);
        using var health = await broken.GetJsonAsync("/api/v1/health");
        Assert.Equal("healthy", health.RootElement.GetProperty("status").GetString());
        await AssertErrorAsync(broken.Client, "/api/v1/ready", HttpStatusCode.ServiceUnavailable, "not_ready");
        File.Move(fixture.Paths.WorkingDatabasePath, fixture.Paths.WorkingDatabasePath + ".offline");
        using var alive = await fixture.GetJsonAsync("/api/v1/health");
        await AssertErrorAsync(fixture.Client, "/api/v1/ready", HttpStatusCode.ServiceUnavailable, "not_ready");
        Assert.False(File.Exists(fixture.Paths.WorkingDatabasePath));
    }

    [Fact]
    public async Task PublicOpenApiDocumentsReadRoutesAndAcceptedLocationIsLive()
    {
        using var fixture = new Fixture();
        using var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("[]")), "file", "input.json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/uploads") { Content = body };
        request.Headers.Add("X-Api-Key", "test-read-api-key");
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var accepted = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        using var status = await fixture.GetJsonAsync(accepted.Headers.Location!.OriginalString);
        using var unauthorized = await fixture.Client.PostAsync("/api/v1/uploads", new StringContent("[]"));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        using var api = await fixture.GetJsonAsync("/openapi/v1.json");
        var paths = api.RootElement.GetProperty("paths");
        foreach (var route in new[] { "/api/v1/uploads", "/api/v1/uploads/{uploadId}/status", "/api/v1/uploads/{uploadId}/report", "/api/v1/catalog", "/api/v1/health", "/api/v1/ready" })
        {
            var get = paths.GetProperty(route).GetProperty("get");
            Assert.False(get.TryGetProperty("requestBody", out _));
            Assert.True(get.GetProperty("responses").TryGetProperty("200", out _));
            if (get.TryGetProperty("parameters", out var parameters))
                Assert.DoesNotContain(parameters.EnumerateArray(), parameter => parameter.GetProperty("name").GetString() == "X-Api-Key");
        }
        var catalogParameters = paths.GetProperty("/api/v1/catalog").GetProperty("get").GetProperty("parameters");
        foreach (var parameter in new[] { "category", "brand", "name", "sellerName", "page", "pageSize" })
            Assert.Contains(catalogParameters.EnumerateArray(), item => item.GetProperty("name").GetString() == parameter);
        foreach (var (route, codes) in new[]
        {
            ("/api/v1/uploads", new[] { "400" }),
            ("/api/v1/uploads/{uploadId}/status", new[] { "400", "404" }),
            ("/api/v1/uploads/{uploadId}/report", new[] { "404", "409", "500" }),
            ("/api/v1/catalog", new[] { "400" }),
            ("/api/v1/ready", new[] { "503" })
        })
            foreach (var code in codes) Assert.True(paths.GetProperty(route).GetProperty("get").GetProperty("responses").TryGetProperty(code, out _));
        using var swagger = await fixture.Client.GetAsync("/swagger/");
        Assert.Equal(HttpStatusCode.OK, swagger.StatusCode);
    }

    private static async Task AssertErrorAsync(HttpClient client, string route, HttpStatusCode expected, string code)
    {
        using var response = await client.GetAsync(route);
        Assert.Equal(expected, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.True(Guid.TryParse(json.RootElement.GetProperty("traceId").GetString(), out _));
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("message").GetString()));
        Assert.DoesNotContain("C:\\", json.RootElement.GetRawText());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"marketplace-public-read-tests-{Guid.NewGuid():N}");
        public Fixture(bool failReadiness = false)
        {
            Factory = new Factory(_root, failReadiness);
            Client = Factory.CreateClient();
            Paths = Factory.Services.GetRequiredService<IStoragePaths>();
            Uploads = Factory.Services.GetRequiredService<IUploadStore>();
        }
        public Factory Factory { get; }
        public HttpClient Client { get; }
        public IStoragePaths Paths { get; }
        public IUploadStore Uploads { get; }
        public UploadRecord Upload(Guid id) => new(id, Guid.NewGuid(), "input.json", "hash", Path.Combine(Paths.UploadDirectory, $"{id}.json"), Path.Combine(Paths.ReportDirectory, $"{id}.json"), UploadStatus.Queued,
            DateTimeOffset.Parse("2026-01-01T00:00:00Z"), null, 0, 0, 0, 0, Guid.NewGuid(), null, null, null);
        public async Task<JsonDocument> GetJsonAsync(string route)
        {
            using var response = await Client.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }
        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed class Factory(string root, bool failReadiness) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureLogging(logging => logging.ClearProviders().AddConsole());
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKey"] = "test-read-api-key",
                ["Catalog:StorageRoot"] = Path.Combine(root, "data"),
                ["Catalog:StarterDatabasePath"] = Path.Combine(AppContext.BaseDirectory, "catalog.db")
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUploadItemProcessor>();
                services.AddSingleton<IUploadItemProcessor, IdleProcessor>();
                if (failReadiness)
                {
                    services.RemoveAll<IReadinessCheck>();
                    services.AddSingleton<IReadinessCheck, FailedReadiness>();
                }
            });
        }
    }
    private sealed class IdleProcessor : IUploadItemProcessor
    {
        public bool IsAvailable => false;
        public Task<UploadItemProcessingResult> ProcessAsync(UploadRecord upload, int sourceIndex, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
    private sealed class FailedReadiness : IReadinessCheck
    {
        public Task CheckAsync(CancellationToken cancellationToken) => throw new IOException("Sensitive dependency detail");
    }
}
