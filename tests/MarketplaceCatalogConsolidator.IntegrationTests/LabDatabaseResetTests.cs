using System.Net;
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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarketplaceCatalogConsolidator.IntegrationTests;

public sealed class LabDatabaseResetTests
{
    private const string Route = "/api/v2/reset-database";
    private const string Key = "test-lab-api-key";

    [Theory]
    [InlineData(null, "RESET_DATABASE", null, 401, "unauthorized")]
    [InlineData("wrong", "RESET_DATABASE", null, 401, "unauthorized")]
    [InlineData(Key, null, null, 400, "reset_not_confirmed")]
    [InlineData(Key, "reset_database", null, 400, "reset_not_confirmed")]
    [InlineData(Key, " RESET_DATABASE ", null, 400, "reset_not_confirmed")]
    [InlineData(Key, "RESET_DATABASE", "{}", 400, "invalid_request_body")]
    public async Task InvalidRequestsDoNotMutateState(string? key, string? confirmation, string? body, int status, string code)
    {
        using var fixture = new Fixture();
        var accepted = await fixture.UploadAsync();
        var before = await File.ReadAllBytesAsync(fixture.Paths.WorkingDatabasePath);
        using var request = ResetRequest(key, confirmation, body);
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.True(Guid.TryParse(json.RootElement.GetProperty("traceId").GetString(), out _));
        Assert.NotNull(await fixture.Uploads.FindByIdAsync(accepted));
        Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Paths.WorkingDatabasePath));
        Assert.Single(Directory.GetFiles(fixture.Paths.UploadDirectory));
    }

    [Fact]
    public async Task ChunkedNonEmptyBodyIsRejectedWithoutMutation()
    {
        using var fixture = new Fixture();
        using var request = ResetRequest();
        request.Content = new StreamContent(new NonSeekableBody());
        request.Headers.TransferEncodingChunked = true;
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal("invalid_request_body", json.RootElement.GetProperty("code").GetString());
        Assert.False(fixture.Factory.Services.GetRequiredService<ILabDatabaseResetStorage>().HasPendingReset);
    }

    [Fact]
    public async Task ResetRestoresBaselineClearsArtifactsAndAcceptsNewUpload()
    {
        using var fixture = new Fixture();
        var starterBytes = await File.ReadAllBytesAsync(fixture.Paths.StarterDatabasePath);
        var first = await fixture.UploadAsync();
        Assert.True(await fixture.Workflow.ProcessNextAsync());
        Assert.Equal(UploadStatus.Completed, (await fixture.Uploads.FindByIdAsync(first))!.Status);
        Assert.Single(Directory.GetFiles(fixture.Paths.ReportDirectory));
        var key = Guid.NewGuid();
        var queued = await fixture.UploadAsync(key);
        Assert.Single(Directory.GetFiles(fixture.Paths.UploadDirectory));

        await fixture.AssertResetAsync();

        Assert.Null(await fixture.Uploads.FindByIdAsync(first));
        Assert.Null(await fixture.Uploads.FindByIdAsync(queued));
        Assert.Null(await fixture.Uploads.FindByIdempotencyKeyAsync(key));
        Assert.Empty(Directory.GetFiles(fixture.Paths.UploadDirectory));
        Assert.Empty(Directory.GetFiles(fixture.Paths.ReportDirectory));
        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM UploadItem;"));
        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM Upload;"));
        Assert.Equal(3, await fixture.CountAsync("SELECT COUNT(*) FROM SchemaMigration;"));
        using var ready = await fixture.Client.GetAsync("/api/v1/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(starterBytes, await File.ReadAllBytesAsync(fixture.Paths.StarterDatabasePath));

        var fresh = await fixture.UploadAsync(key);
        Assert.True(await fixture.Workflow.ProcessNextAsync());
        Assert.Equal(UploadStatus.Completed, (await fixture.Uploads.FindByIdAsync(fresh))!.Status);
        Assert.Equal(1, await fixture.CountAsync("SELECT COUNT(*) FROM SellerProduct;"));
        await fixture.AssertResetAsync();
        Assert.Equal(starterBytes, await File.ReadAllBytesAsync(fixture.Paths.StarterDatabasePath));
    }

    [Fact]
    public async Task LiveOpenApiHasOnlyVersionTwoPostAndRequiredHeaders()
    {
        using var fixture = new Fixture();
        using var response = await fixture.Client.GetAsync("/openapi/v1.json");
        using var json = await JsonAsync(response);
        var paths = json.RootElement.GetProperty("paths");
        Assert.Single(paths.EnumerateObject(), path => path.Name.Contains("reset", StringComparison.OrdinalIgnoreCase));
        var route = paths.GetProperty(Route);
        Assert.Single(route.EnumerateObject());
        var operation = route.GetProperty("post");
        Assert.Contains("Lab-only", operation.GetProperty("description").GetString());
        foreach (var header in new[] { "X-Api-Key", "X-Reset-Confirmation" })
        {
            var parameter = Assert.Single(operation.GetProperty("parameters").EnumerateArray(), item => item.GetProperty("name").GetString() == header);
            Assert.True(parameter.GetProperty("required").GetBoolean());
            Assert.Equal("header", parameter.GetProperty("in").GetString());
        }
        Assert.False(operation.TryGetProperty("requestBody", out _));
        foreach (var status in new[] { "200", "400", "401", "413", "429", "500", "503" })
            Assert.True(operation.GetProperty("responses").TryGetProperty(status, out _));
        Assert.DoesNotContain(Key, json.RootElement.GetRawText());
        foreach (var invalidRoute in new[] { "/reset-database", "/api/v1/reset-database" })
        {
            using var invalid = await fixture.Client.PostAsync(invalidRoute, null);
            Assert.Equal(HttpStatusCode.NotFound, invalid.StatusCode);
        }
        using var get = await fixture.Client.GetAsync(Route);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
    }

    [Fact]
    public async Task UploadWaitsForRealOverlappingResetAndSurvivesInCleanState()
    {
        var pause = new ResetPause();
        using var fixture = new Fixture(pause: pause);
        using var resetRequest = ResetRequest();
        var resetting = fixture.Client.SendAsync(resetRequest);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var uploading = fixture.UploadAsync();
        await Task.Delay(100);
        Assert.False(uploading.IsCompleted);
        pause.Release.TrySetResult();
        using var reset = await resetting;
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        var id = await uploading;
        Assert.NotNull(await fixture.Uploads.FindByIdAsync(id));
        Assert.Single(Directory.GetFiles(fixture.Paths.UploadDirectory));
        Assert.True(await fixture.Workflow.ProcessNextAsync());
        Assert.Equal(UploadStatus.Completed, (await fixture.Uploads.FindByIdAsync(id))!.Status);
    }

    [Fact]
    public async Task ConcurrentResetsSerializeAndBothRestoreBaseline()
    {
        var pause = new ResetPause();
        using var fixture = new Fixture(pause: pause);
        using var firstRequest = ResetRequest();
        var first = fixture.Client.SendAsync(firstRequest);
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var secondRequest = ResetRequest();
        var second = fixture.Client.SendAsync(secondRequest);
        await Task.Delay(100);
        Assert.False(second.IsCompleted);
        pause.Release.TrySetResult();
        using var firstResponse = await first;
        using var secondResponse = await second;
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(975, await fixture.CountAsync("SELECT COUNT(*) FROM Product;"));
        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM SellerProduct;"));
    }

    [Fact]
    public async Task ResetWaitsForWorkerToPublishBeforeRemovingItsArtifacts()
    {
        var pause = new WorkerPause();
        using var fixture = new Fixture(workerPause: pause);
        await fixture.UploadAsync();
        var processing = fixture.Workflow.ProcessNextAsync();
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var request = ResetRequest();
        var reset = fixture.Client.SendAsync(request);
        await Task.Delay(100);
        Assert.False(reset.IsCompleted);
        pause.Release.TrySetResult();
        Assert.True(await processing);
        using var response = await reset;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(Directory.GetFiles(fixture.Paths.ReportDirectory));
        Assert.Equal(0, await fixture.CountAsync("SELECT COUNT(*) FROM Upload;"));
    }

    [Fact]
    public async Task FailedMigrationFencesReadinessAndUploadUntilResetRecovers()
    {
        var migration = new SwitchableMigrator();
        using var fixture = new Fixture(migrator: migration, runWorker: true);
        migration.Fail = true;
        using var request = ResetRequest();
        using var failed = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        using var json = await JsonAsync(failed);
        Assert.Equal("reset_failed", json.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("sensitive", json.RootElement.GetRawText());
        await Task.Delay(600); // Allow the real hosted worker to encounter the reset fence.
        using var ready = await fixture.Client.GetAsync("/api/v1/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        using var upload = await fixture.PostUploadAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, upload.StatusCode);
        migration.Fail = false;
        await fixture.AssertResetAsync();
        using var restoredReady = await fixture.Client.GetAsync("/api/v1/ready");
        Assert.Equal(HttpStatusCode.OK, restoredReady.StatusCode);
    }

    [Fact]
    public async Task StartupRecoversPersistedInterruptedReset()
    {
        var root = Path.Combine(Path.GetTempPath(), $"marketplace-reset-recovery-{Guid.NewGuid():N}");
        try
        {
            using (var first = new Fixture(root: root))
            {
                await first.UploadAsync();
                await File.WriteAllTextAsync(Path.Combine(first.Paths.RootDirectory, ".lab-reset-pending"), "pending");
            }
            using var recovered = new Fixture(root: root);
            Assert.Equal(975, await recovered.CountAsync("SELECT COUNT(*) FROM Product;"));
            Assert.Equal(0, await recovered.CountAsync("SELECT COUNT(*) FROM Upload;"));
            Assert.False(recovered.Factory.Services.GetRequiredService<ILabDatabaseResetStorage>().HasPendingReset);
            Assert.Empty(Directory.GetFiles(recovered.Paths.UploadDirectory));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ResetUsesSameStrictMutationRateLimitAsUploads()
    {
        using var fixture = new Fixture();
        for (var index = 0; index < 5; index++)
        {
            using var request = ResetRequest(confirmation: "wrong");
            using var response = await fixture.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using var sixth = ResetRequest();
        using var limited = await fixture.Client.SendAsync(sixth);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    private static HttpRequestMessage ResetRequest(string? key = Key, string? confirmation = "RESET_DATABASE", string? body = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route);
        if (key is not null) request.Headers.TryAddWithoutValidation("X-Api-Key", key);
        if (confirmation is not null) request.Headers.TryAddWithoutValidation("X-Reset-Confirmation", confirmation);
        if (body is not null) request.Content = new StringContent(body);
        return request;
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) => JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly bool _ownsRoot;
        public Fixture(ResetPause? pause = null, WorkerPause? workerPause = null, SwitchableMigrator? migrator = null, string? root = null, bool runWorker = false)
        {
            _ownsRoot = root is null;
            _root = root ?? Path.Combine(Path.GetTempPath(), $"marketplace-reset-tests-{Guid.NewGuid():N}");
            Factory = new Factory(_root, pause, workerPause, migrator, runWorker);
            Client = Factory.CreateClient();
            Paths = Factory.Services.GetRequiredService<IStoragePaths>();
            Uploads = Factory.Services.GetRequiredService<IUploadStore>();
            Workflow = Factory.Services.GetRequiredService<ConsolidationWorkflow>();
        }
        public Factory Factory { get; }
        public HttpClient Client { get; }
        public IStoragePaths Paths { get; }
        public IUploadStore Uploads { get; }
        public ConsolidationWorkflow Workflow { get; }

        public async Task<HttpResponseMessage> PostUploadAsync(Guid? key = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/uploads");
            request.Headers.Add("X-Api-Key", Key);
            request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString("D"));
            var content = new MultipartFormDataContent();
            var source = new[] { new { Id = Guid.NewGuid().ToString("D"), SellerName = "ResetSeller", Name = "Reset Test Item", Brand = "Lab Brand", Category = "Lab Category" } };
            content.Add(new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(source)), "file", "lab.json");
            request.Content = content;
            return await Client.SendAsync(request);
        }
        public async Task<Guid> UploadAsync(Guid? key = null)
        {
            using var response = await PostUploadAsync(key);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            using var json = await JsonAsync(response);
            return json.RootElement.GetProperty("uploadId").GetGuid();
        }
        public async Task AssertResetAsync()
        {
            using var request = ResetRequest();
            using var response = await Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
            using var json = await JsonAsync(response);
            Assert.Equal(975, json.RootElement.GetProperty("productCount").GetInt64());
            Assert.Equal(0, json.RootElement.GetProperty("sellerProductCount").GetInt64());
            Assert.True(json.RootElement.GetProperty("resetAtUtc").TryGetDateTimeOffset(out _));
            Assert.Equal(3, json.RootElement.EnumerateObject().Count());
        }
        public async Task<long> CountAsync(string sql)
        {
            await using var connection = await Factory.Services.GetRequiredService<SqliteConnectionFactory>().OpenConnectionAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }
        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
            if (_ownsRoot && Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed class Factory(string root, ResetPause? pause, WorkerPause? workerPause, SwitchableMigrator? migrator, bool runWorker) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureLogging(logging => logging.ClearProviders().AddConsole());
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKey"] = Key,
                ["Catalog:StorageRoot"] = Path.Combine(root, "data"),
                ["Catalog:StarterDatabasePath"] = Path.Combine(AppContext.BaseDirectory, "artifacts", "catalog.db")
            }));
            builder.ConfigureTestServices(services =>
            {
                if (!runWorker) services.RemoveAll<IHostedService>();
                if (migrator is not null)
                {
                    services.RemoveAll<IDatabaseMigrator>();
                    services.AddSingleton<IDatabaseMigrator>(migrator);
                }
                if (pause is not null)
                {
                    services.RemoveAll<ILabDatabaseResetStorage>();
                    services.AddSingleton<SqliteLabDatabaseResetStorage>();
                    services.AddSingleton<ILabDatabaseResetStorage>(provider => new PausedResetStorage(provider.GetRequiredService<SqliteLabDatabaseResetStorage>(), pause));
                }
                if (workerPause is not null)
                {
                    services.RemoveAll<IUploadItemProcessor>();
                    services.AddSingleton<SourceCatalogItemProcessor>();
                    services.AddSingleton<IUploadItemProcessor>(provider => new PausedProcessor(provider.GetRequiredService<SourceCatalogItemProcessor>(), workerPause));
                }
            });
        }
    }
    private sealed class ResetPause
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class WorkerPause
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class PausedResetStorage(ILabDatabaseResetStorage inner, ResetPause pause) : ILabDatabaseResetStorage
    {
        public bool HasPendingReset => inner.HasPendingReset;
        public async Task<LabDatabaseResetResult> ResetAsync(CancellationToken cancellationToken = default)
        {
            pause.Entered.TrySetResult();
            await pause.Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return await inner.ResetAsync(cancellationToken);
        }
    }
    private sealed class PausedProcessor(IUploadItemProcessor inner, WorkerPause pause) : IUploadItemProcessor
    {
        public bool IsAvailable => true;
        public async Task<UploadItemProcessingResult> ProcessAsync(UploadRecord upload, int sourceIndex, CancellationToken cancellationToken = default)
        {
            pause.Entered.TrySetResult();
            await pause.Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return await inner.ProcessAsync(upload, sourceIndex, cancellationToken);
        }
    }
    private sealed class SwitchableMigrator : IDatabaseMigrator
    {
        public bool Fail { get; set; }
        public Task MigrateAsync(string databasePath, CancellationToken cancellationToken = default) =>
            Fail ? throw new IOException("sensitive migration detail") : new SqliteDatabaseMigrator().MigrateAsync(databasePath, cancellationToken);
    }
    private sealed class NonSeekableBody : MemoryStream
    {
        public NonSeekableBody() : base(Encoding.UTF8.GetBytes("{}")) { }
        public override bool CanSeek => false;
    }
}
