using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using MarketplaceCatalogConsolidator.Api.Operations;
using MarketplaceCatalogConsolidator.Application.Ports;
using MarketplaceCatalogConsolidator.Domain;
using MarketplaceCatalogConsolidator.Infrastructure.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarketplaceCatalogConsolidator.IntegrationTests;

public sealed class OperationalHardeningTests
{
    private const string ApiKey = "test-only-api-key-marker";
    private const string SecretHeader = "test-only-secret-header-marker";

    [Fact]
    public async Task UploadLimitRejectsWithoutNewArtifactsAndIgnoresForwardedHeaders()
    {
        using var fixture = new Fixture();
        for (var index = 0; index < ApiHardening.UploadPermits; index++)
        {
            using var request = UploadRequest(Guid.NewGuid());
            request.Headers.Add("X-Forwarded-For", $"192.0.2.{index + 1}");
            using var response = await fixture.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        using var rejectedRequest = UploadRequest(Guid.NewGuid());
        rejectedRequest.Headers.Add("X-Forwarded-For", "198.51.100.99");
        using var rejected = await fixture.Client.SendAsync(rejectedRequest);
        await AssertErrorAsync(rejected, HttpStatusCode.TooManyRequests, "rate_limit_exceeded");
        Assert.NotNull(rejected.Headers.RetryAfter);
        Assert.Equal(ApiHardening.UploadPermits, (await fixture.Uploads.ListAsync(1, 100)).TotalCount);
        Assert.Equal(ApiHardening.UploadPermits, Directory.GetFiles(fixture.Paths.UploadDirectory, "*.json").Length);
        using var read = await fixture.Client.GetAsync("/api/v1/uploads");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    [Fact]
    public async Task ReadsShareTheirOwnPolicyWithoutConsumingUploadPermits()
    {
        using var fixture = new Fixture();
        for (var index = 0; index < ApiHardening.ReadPermits; index++)
        {
            using var read = await fixture.Client.GetAsync(index % 2 == 0 ? "/api/v1/health" : "/api/v1/uploads");
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }
        using var rejected = await fixture.Client.GetAsync("/api/v1/catalog");
        await AssertErrorAsync(rejected, HttpStatusCode.TooManyRequests, "rate_limit_exceeded");
        using var request = UploadRequest(Guid.NewGuid());
        using var accepted = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
    }

    [Fact]
    public async Task GlobalAndReadinessErrorsNeverReflectSecretsInResponsesOrLogs()
    {
        using var fixture = new Fixture(failQueries: true);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/catalog");
        request.Headers.Add("X-Api-Key", ApiKey);
        request.Headers.Add("Authorization", "Bearer " + SecretHeader);
        request.Headers.Add("X-Secret", SecretHeader);
        using var response = await fixture.Client.SendAsync(request);
        await AssertErrorAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        using var readiness = await fixture.Client.GetAsync("/api/v1/ready");
        await AssertErrorAsync(readiness, HttpStatusCode.ServiceUnavailable, "not_ready");
        Assert.Contains(fixture.Logs.Entries, entry => entry.Contains("TraceId", StringComparison.Ordinal) && entry.Contains("InvalidOperationException", StringComparison.Ordinal));
        var logs = string.Join("\n", fixture.Logs.Entries);
        Assert.DoesNotContain(ApiKey, logs);
        Assert.DoesNotContain(SecretHeader, logs);
        Assert.DoesNotContain("sensitive-staged-path", logs);
        Assert.DoesNotContain("sensitive-raw-payload", logs);
        using var unauthorizedRequest = UploadRequest(Guid.NewGuid());
        unauthorizedRequest.Headers.Remove("X-Api-Key");
        unauthorizedRequest.Headers.Add("X-Api-Key", SecretHeader);
        using var unauthorized = await fixture.Client.SendAsync(unauthorizedRequest);
        await AssertErrorAsync(unauthorized, HttpStatusCode.Unauthorized, "unauthorized");
        Assert.DoesNotContain(SecretHeader, string.Join("\n", fixture.Logs.Entries));
    }

    [Fact]
    public async Task SecurityHeadersAllowPublicSwaggerAndOpenApi()
    {
        using var fixture = new Fixture();
        foreach (var route in new[] { "/swagger/", "/openapi/v1.json", "/api/v1/health" })
        {
            using var response = await fixture.Client.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
            Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
            Assert.False(response.Headers.Contains("Server"));
        }
        using var https = await fixture.Client.GetAsync("https://localhost/api/v1/health");
        Assert.Equal("max-age=31536000", https.Headers.GetValues("Strict-Transport-Security").Single());
        using var api = JsonDocument.Parse(await fixture.Client.GetStringAsync("/openapi/v1.json"));
        foreach (var path in api.RootElement.GetProperty("paths").EnumerateObject())
            foreach (var operation in path.Value.EnumerateObject())
                Assert.True(operation.Value.GetProperty("responses").TryGetProperty("429", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TotalBodyLimitRejectsDeclaredAndUnknownLengthsWithoutArtifacts(bool unknownLength)
    {
        using var fixture = new Fixture();
        var bytes = Encoding.UTF8.GetBytes(new string(' ', ApiHardening.MaximumRequestBytes + 1));
        using var request = UploadRequest(Guid.NewGuid());
        request.Content?.Dispose();
        request.Content = unknownLength ? new StreamContent(new NonSeekableStream(bytes)) : new ByteArrayContent(bytes);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("multipart/form-data");
        request.Content.Headers.ContentType.Parameters.Add(new System.Net.Http.Headers.NameValueHeaderValue("boundary", "boundary"));
        using var response = await fixture.Client.SendAsync(request);
        await AssertErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
        Assert.Empty((await fixture.Uploads.ListAsync(1, 10)).Items);
        Assert.False(Directory.Exists(fixture.Paths.UploadDirectory) && Directory.GetFiles(fixture.Paths.UploadDirectory).Length > 0);
    }

    [Fact]
    public async Task LargestPermittedFileStillAcceptsAndIdempotencyRemainsIntact()
    {
        using var fixture = new Fixture();
        var key = Guid.NewGuid();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = UploadRequest(key, "[" + new string(' ', 499_997) + "]");
            using var response = await fixture.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        Assert.Single((await fixture.Uploads.ListAsync(1, 10)).Items);
        Assert.Single(Directory.GetFiles(fixture.Paths.UploadDirectory, "*.json"));
    }

    [Theory]
    [InlineData("Production", null, false, false)]
    [InlineData("Production", "development-only-not-a-secret", false, false)]
    [InlineData("Production", null, true, false)]
    [InlineData("Development", null, true, true)]
    [InlineData("Testing", "test-only-api-key-marker", false, true)]
    public void StartupConfigurationRejectsMissingKeysAndProductionPlaceholder(string environment, string? key, bool placeholder, bool valid)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Security:ApiKey"] = key,
            ["Development:UsePlaceholderApiKey"] = placeholder.ToString()
        }).Build();
        var paths = new FileSystemStoragePaths(new CatalogStorageOptions(Path.Combine(Path.GetTempPath(), "separate-test-root"), Path.Combine(AppContext.BaseDirectory, "artifacts", "catalog.db")));
        var hostEnvironment = new TestEnvironment { EnvironmentName = environment };
        if (valid) ApiHardening.ValidateRuntimeConfiguration(configuration, hostEnvironment, paths);
        else
        {
            var exception = Assert.Throws<InvalidOperationException>(() => ApiHardening.ValidateRuntimeConfiguration(configuration, hostEnvironment, paths));
            if (key is not null) Assert.DoesNotContain(key, exception.Message);
        }
        if (valid && placeholder) Assert.Equal(ApiHardening.DevelopmentPlaceholder, configuration["Security:ApiKey"]);
    }

    private static HttpRequestMessage UploadRequest(Guid key, string content = "[]")
    {
        var multipart = new MultipartFormDataContent();
        multipart.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "file", "input.json");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/uploads") { Content = multipart };
        request.Headers.Add("X-Api-Key", ApiKey);
        request.Headers.Add("Idempotency-Key", key.ToString());
        return request;
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        using var error = JsonDocument.Parse(text);
        Assert.Equal(code, error.RootElement.GetProperty("code").GetString());
        Assert.True(Guid.TryParse(error.RootElement.GetProperty("traceId").GetString(), out _));
        Assert.DoesNotContain(ApiKey, text);
        Assert.DoesNotContain(SecretHeader, text);
        Assert.DoesNotContain("sensitive-staged-path", text);
        Assert.DoesNotContain("sensitive-raw-payload", text);
        Assert.DoesNotContain("StackTrace", text);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"marketplace-hardening-tests-{Guid.NewGuid():N}");
        public Fixture(bool failQueries = false)
        {
            Factory = new Factory(_root, Logs, failQueries);
            Client = Factory.CreateClient();
            Paths = Factory.Services.GetRequiredService<IStoragePaths>();
            Uploads = Factory.Services.GetRequiredService<IUploadStore>();
        }
        public CaptureLoggerProvider Logs { get; } = new();
        public Factory Factory { get; }
        public HttpClient Client { get; }
        public IStoragePaths Paths { get; }
        public IUploadStore Uploads { get; }
        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed class Factory(string root, CaptureLoggerProvider logs, bool failQueries) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureLogging(logging => logging.ClearProviders().AddProvider(logs));
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKey"] = ApiKey,
                ["Catalog:StorageRoot"] = Path.Combine(root, "data"),
                ["Catalog:StarterDatabasePath"] = Path.Combine(AppContext.BaseDirectory, "artifacts", "catalog.db")
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUploadItemProcessor>();
                services.AddSingleton<IUploadItemProcessor, IdleProcessor>();
                if (failQueries)
                {
                    services.RemoveAll<IPublicReadStore>();
                    services.AddSingleton<IPublicReadStore, ThrowingReads>();
                    services.RemoveAll<IReadinessCheck>();
                    services.AddSingleton<IReadinessCheck, ThrowingReadiness>();
                }
            });
        }
    }

    private sealed class CaptureLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(Entries);
        public void Dispose() { }
        private sealed class CaptureLogger(ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var fields = state is IEnumerable<KeyValuePair<string, object?>> values ? string.Join(";", values.Select(value => $"{value.Key}={value.Value}")) : string.Empty;
                entries.Enqueue(formatter(state, exception) + fields + exception);
            }
        }
    }
    private sealed class IdleProcessor : IUploadItemProcessor
    {
        public bool IsAvailable => false;
        public Task<UploadItemProcessingResult> ProcessAsync(UploadRecord upload, int sourceIndex, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
    private static Exception SensitiveException() => new InvalidOperationException($"{ApiKey}; {SecretHeader}; sensitive-staged-path; sensitive-raw-payload");
    private sealed class ThrowingReadiness : IReadinessCheck
    {
        public Task CheckAsync(CancellationToken cancellationToken) => throw SensitiveException();
    }
    private sealed class ThrowingReads : IPublicReadStore
    {
        public Task<Page<UploadRecord>> ListUploadsAsync(UploadStatus? status, int page, int pageSize, CancellationToken cancellationToken) => throw SensitiveException();
        public Task<Page<UploadItemRecord>> ListItemsAsync(Guid uploadId, int page, int pageSize, CancellationToken cancellationToken) => throw SensitiveException();
        public Task<Page<CatalogResult>> QueryCatalogAsync(string? category, string? brand, string? name, string? sellerName, int page, int pageSize, CancellationToken cancellationToken) => throw SensitiveException();
    }
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }
}
