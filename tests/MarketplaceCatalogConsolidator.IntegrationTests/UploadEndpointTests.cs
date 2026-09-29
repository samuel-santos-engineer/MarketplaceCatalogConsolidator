using System.Net;
using System.Text;
using System.Text.Json;
using MarketplaceCatalogConsolidator.Application.Ports;
using MarketplaceCatalogConsolidator.Application.Uploads;
using MarketplaceCatalogConsolidator.Domain;
using MarketplaceCatalogConsolidator.Infrastructure.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MarketplaceCatalogConsolidator.IntegrationTests;

public sealed class UploadEndpointTests
{
    private const string ApiKey = "milestone-five-test-api-key";
    private const string ApiUrl = "/api/v1/uploads";
    private const string ValidProductJson = "[{\"Id\":\"a1b2c3d4-e5f6-4a5b-8c9d-0e1f2a3b4c5d\",\"SellerName\":\"MegaStore\",\"Name\":\"Smartphone Galaxy S23\",\"Brand\":\"Samsung\",\"Category\":\"Electronics\"}]";

    [Fact]
    public async Task ValidMultipartCreatesQueuedUploadAndDurableStagedFile()
    {
        using var fixture = new UploadApiFixture();
        var key = Guid.NewGuid();
        using var response = await fixture.PostFileAsync(Encoding.UTF8.GetBytes(ValidProductJson), "ProductEntry.json", key);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal($"/api/v1/uploads/{JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("uploadId").GetString()}/status", response.Headers.Location?.OriginalString);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var uploadId = Guid.Parse(json.RootElement.GetProperty("uploadId").GetString()!);
        Assert.Equal("Queued", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("ProductEntry.json", json.RootElement.GetProperty("fileName").GetString());

        var uploads = await fixture.UploadStore.ListAsync(1, 10);
        var upload = Assert.Single(uploads.Items);
        Assert.Equal(uploadId, upload.Id);
        Assert.Equal(key, upload.IdempotencyKey);
        Assert.Equal(1, upload.ReceivedCount);
        Assert.Equal(UploadStatus.Queued, upload.Status);
        Assert.True(File.Exists(upload.StagedFilePath));
        Assert.Equal(Encoding.UTF8.GetBytes(ValidProductJson), await File.ReadAllBytesAsync(upload.StagedFilePath));
        Assert.Single(Directory.GetFiles(fixture.Paths.UploadDirectory, "*.json", SearchOption.TopDirectoryOnly));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-key")]
    public async Task MissingOrInvalidApiKeyReturnsSameUnauthorizedEnvelopeWithoutArtifacts(string? apiKey)
    {
        using var fixture = new UploadApiFixture();
        using var response = await fixture.PostFileAsync(Encoding.UTF8.GetBytes(ValidProductJson), "input.json", Guid.NewGuid(), apiKey);

        await AssertErrorAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
        await AssertNoAcceptedArtifactsAsync(fixture);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("9d0df985-610c-1e54-8d50-519e972a42a6")]
    [InlineData("bad-key")]
    public async Task MissingOrNonV4IdempotencyKeyReturnsBadRequestWithoutArtifacts(string? key)
    {
        using var fixture = new UploadApiFixture();
        using var response = await fixture.PostFileAsync(Encoding.UTF8.GetBytes(ValidProductJson), "input.json", key);

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_idempotency_key");
        await AssertNoAcceptedArtifactsAsync(fixture);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{}")]
    public async Task InvalidOrNonArrayJsonReturnsBadRequestWithoutArtifacts(string payload)
    {
        using var fixture = new UploadApiFixture();
        using var response = await fixture.PostFileAsync(Encoding.UTF8.GetBytes(payload), "input.json", Guid.NewGuid());

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_json");
        await AssertNoAcceptedArtifactsAsync(fixture);
    }

    [Fact]
    public async Task InvalidUtf8ReturnsBadRequestWithoutArtifacts()
    {
        using var fixture = new UploadApiFixture();
        using var response = await fixture.PostFileAsync(new byte[] { (byte)'[', 0xFF, (byte)']' }, "input.json", Guid.NewGuid());

        await AssertErrorAsync(response, HttpStatusCode.BadRequest, "invalid_json");
        await AssertNoAcceptedArtifactsAsync(fixture);
    }

    [Fact]
    public async Task UnsupportedContentTypeReturns415()
    {
        using var fixture = new UploadApiFixture();
        using var content = new StringContent(ValidProductJson, Encoding.UTF8, "application/json");
        using var request = fixture.CreateRequest(Guid.NewGuid());
        request.Content = content;

        using var response = await fixture.Client.SendAsync(request);

        await AssertErrorAsync(response, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        await AssertNoAcceptedArtifactsAsync(fixture);
    }

    [Fact]
    public async Task MissingEmptyAndMultipleFilesReturnBadRequestWithoutArtifacts()
    {
        using var fixture = new UploadApiFixture();
        using (var missing = await fixture.PostMultipartAsync(Array.Empty<(string Field, byte[] Bytes, string Name)>(), Guid.NewGuid()))
        {
            await AssertErrorAsync(missing, HttpStatusCode.BadRequest, "missing_file");
        }

        using (var empty = await fixture.PostFileAsync([], "empty.json", Guid.NewGuid()))
        {
            await AssertErrorAsync(empty, HttpStatusCode.BadRequest, "empty_file");
        }

        using (var multiple = await fixture.PostMultipartAsync(new[]
        {
            ("file", Encoding.UTF8.GetBytes(ValidProductJson), "one.json"),
            ("file", Encoding.UTF8.GetBytes(ValidProductJson), "two.json")
        }, Guid.NewGuid()))
        {
            await AssertErrorAsync(multiple, HttpStatusCode.BadRequest, "multiple_files");
        }

        await AssertNoAcceptedArtifactsAsync(fixture);
    }

    [Fact]
    public async Task ExactlyFiveHundredThousandBytesIsRejected()
    {
        using var fixture = new UploadApiFixture();
        using var response = await fixture.PostFileAsync(new byte[UploadAcceptanceService.MaximumFileSizeBytes], "large.json", Guid.NewGuid());

        await AssertErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
        await AssertNoAcceptedArtifactsAsync(fixture);
    }

    [Fact]
    public async Task StreamingLimitRejectsOversizeWhenPartLengthIsUnknown()
    {
        using var fixture = new UploadApiFixture();
        using var content = new MultipartFormDataContent();
        using var streamContent = new StreamContent(new NonSeekableReadStream(new byte[UploadAcceptanceService.MaximumFileSizeBytes + 1]));
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        content.Add(streamContent, "file", "large.json");
        using var request = fixture.CreateRequest(Guid.NewGuid());
        request.Content = content;

        using var response = await fixture.Client.SendAsync(request);

        await AssertErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, "payload_too_large");
        await AssertNoAcceptedArtifactsAsync(fixture);
    }

    [Fact]
    public async Task SameKeyAndSameContentReturnsExistingUploadWithoutDuplicateStage()
    {
        using var fixture = new UploadApiFixture();
        var key = Guid.NewGuid();
        var payload = Encoding.UTF8.GetBytes(ValidProductJson);
        using var first = await fixture.PostFileAsync(payload, "first.json", key);
        using var second = await fixture.PostFileAsync(payload, "different-display-name.json", key);
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(firstJson.RootElement.GetProperty("uploadId").GetString(), secondJson.RootElement.GetProperty("uploadId").GetString());
        Assert.Single((await fixture.UploadStore.ListAsync(1, 10)).Items);
        Assert.Single(Directory.GetFiles(fixture.Paths.UploadDirectory, "*.json", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task SameKeyDifferentContentReturnsConflictAndPreservesOriginal()
    {
        using var fixture = new UploadApiFixture();
        var key = Guid.NewGuid();
        using var first = await fixture.PostFileAsync(Encoding.UTF8.GetBytes(ValidProductJson), "first.json", key);
        var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var firstUploadId = firstJson.RootElement.GetProperty("uploadId").GetString();
        firstJson.Dispose();

        using var conflict = await fixture.PostFileAsync(Encoding.UTF8.GetBytes("[]"), "second.json", key);
        await AssertErrorAsync(conflict, HttpStatusCode.Conflict, "idempotency_conflict");
        Assert.Single((await fixture.UploadStore.ListAsync(1, 10)).Items);
        Assert.True(File.Exists((await fixture.UploadStore.FindByIdAsync(Guid.Parse(firstUploadId!)))!.StagedFilePath));
        Assert.Single(Directory.GetFiles(fixture.Paths.UploadDirectory, "*.json", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task ConcurrentSameKeyRequestsConvergeOnOneRecordAndStagedFile()
    {
        using var fixture = new UploadApiFixture();
        var key = Guid.NewGuid();
        var payload = Encoding.UTF8.GetBytes(ValidProductJson);
        using var requestOne = fixture.CreateFileRequest(payload, "one.json", key);
        using var requestTwo = fixture.CreateFileRequest(payload, "two.json", key);

        var responses = await Task.WhenAll(fixture.Client.SendAsync(requestOne), fixture.Client.SendAsync(requestTwo));
        using var responseOne = responses[0];
        using var responseTwo = responses[1];
        Assert.Equal(HttpStatusCode.Accepted, responseOne.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, responseTwo.StatusCode);
        using var resultOne = JsonDocument.Parse(await responseOne.Content.ReadAsStringAsync());
        using var resultTwo = JsonDocument.Parse(await responseTwo.Content.ReadAsStringAsync());

        Assert.Equal(resultOne.RootElement.GetProperty("uploadId").GetString(), resultTwo.RootElement.GetProperty("uploadId").GetString());
        Assert.Single((await fixture.UploadStore.ListAsync(1, 10)).Items);
        Assert.Single(Directory.GetFiles(fixture.Paths.UploadDirectory, "*.json", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task ClientFilenameCannotEscapeStagingRootAndOpenApiIsAccurate()
    {
        using var fixture = new UploadApiFixture();
        using var response = await fixture.PostFileAsync(Encoding.UTF8.GetBytes(ValidProductJson), "..\\..\\outside.json", Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var upload = Assert.Single((await fixture.UploadStore.ListAsync(1, 10)).Items);
        Assert.Equal("outside.json", upload.FileName);
        Assert.StartsWith(Path.GetFullPath(fixture.Paths.UploadDirectory) + Path.DirectorySeparatorChar, Path.GetFullPath(upload.StagedFilePath), PathComparison());

        using var openApiResponse = await fixture.Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, openApiResponse.StatusCode);
        using var openApi = JsonDocument.Parse(await openApiResponse.Content.ReadAsStringAsync());
        var post = openApi.RootElement.GetProperty("paths").GetProperty("/api/v1/uploads").GetProperty("post");
        Assert.True(post.GetProperty("parameters").EnumerateArray().Single(parameter => parameter.GetProperty("name").GetString() == "Idempotency-Key").GetProperty("required").GetBoolean());
        Assert.True(post.GetProperty("parameters").EnumerateArray().Single(parameter => parameter.GetProperty("name").GetString() == "X-Api-Key").GetProperty("required").GetBoolean());
        Assert.True(post.GetProperty("requestBody").GetProperty("required").GetBoolean());
        Assert.True(post.GetProperty("requestBody").GetProperty("content").TryGetProperty("multipart/form-data", out _));
        foreach (var status in new[] { "202", "400", "401", "409", "413", "415" })
        {
            Assert.True(post.GetProperty("responses").TryGetProperty(status, out _));
        }

        using var swaggerResponse = await fixture.Client.GetAsync("/swagger/");
        Assert.Equal(HttpStatusCode.OK, swaggerResponse.StatusCode);
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, error.RootElement.GetProperty("code").GetString());
        Assert.True(Guid.TryParse(error.RootElement.GetProperty("traceId").GetString(), out _));
        Assert.False(string.IsNullOrWhiteSpace(error.RootElement.GetProperty("message").GetString()));
    }

    private static async Task AssertNoAcceptedArtifactsAsync(UploadApiFixture fixture)
    {
        Assert.Empty((await fixture.UploadStore.ListAsync(1, 10)).Items);
        Assert.False(Directory.Exists(fixture.Paths.UploadDirectory) && Directory.GetFiles(fixture.Paths.UploadDirectory).Length > 0);
    }

    private static StringComparison PathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed class UploadApiFixture : IDisposable
    {
        private readonly string _temporaryRoot = Path.Combine(Path.GetTempPath(), $"marketplace-upload-api-tests-{Guid.NewGuid():N}");

        public UploadApiFixture()
        {
            Directory.CreateDirectory(_temporaryRoot);
            Factory = new UploadWebApplicationFactory(_temporaryRoot);
            Client = Factory.CreateClient();
            Paths = Factory.Services.GetRequiredService<IStoragePaths>();
            UploadStore = Factory.Services.GetRequiredService<IUploadStore>();
        }

        public UploadWebApplicationFactory Factory { get; }
        public HttpClient Client { get; }
        public IStoragePaths Paths { get; }
        public IUploadStore UploadStore { get; }

        public HttpRequestMessage CreateRequest(Guid idempotencyKey, string? apiKey = ApiKey)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            request.Headers.Add("Idempotency-Key", idempotencyKey.ToString("D"));
            if (apiKey is not null)
            {
                request.Headers.Add("X-Api-Key", apiKey);
            }

            return request;
        }

        public HttpRequestMessage CreateFileRequest(byte[] payload, string fileName, Guid idempotencyKey, string? apiKey = ApiKey)
        {
            var content = new MultipartFormDataContent();
            var file = new ByteArrayContent(payload);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            content.Add(file, "file", fileName);
            var request = CreateRequest(idempotencyKey, apiKey);
            request.Content = content;
            return request;
        }

        public Task<HttpResponseMessage> PostFileAsync(byte[] payload, string fileName, Guid? idempotencyKey, string? apiKey = ApiKey) =>
            PostFileAsync(payload, fileName, idempotencyKey?.ToString("D"), apiKey);

        public async Task<HttpResponseMessage> PostFileAsync(byte[] payload, string fileName, string? idempotencyKey, string? apiKey = ApiKey)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
            if (idempotencyKey is not null)
            {
                request.Headers.Add("Idempotency-Key", idempotencyKey);
            }

            if (apiKey is not null)
            {
                request.Headers.Add("X-Api-Key", apiKey);
            }

            var content = new MultipartFormDataContent();
            var file = new ByteArrayContent(payload);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            content.Add(file, "file", fileName);
            request.Content = content;
            return await Client.SendAsync(request);
        }

        public async Task<HttpResponseMessage> PostMultipartAsync(IEnumerable<(string Field, byte[] Bytes, string Name)> files, Guid key)
        {
            var request = CreateRequest(key);
            var content = new MultipartFormDataContent();
            foreach (var (field, bytes, name) in files)
            {
                var file = new ByteArrayContent(bytes);
                file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                content.Add(file, field, name);
            }

            request.Content = content;
            return await Client.SendAsync(request);
        }

        public void Dispose()
        {
            Client.Dispose();
            Factory.Dispose();
            if (Directory.Exists(_temporaryRoot))
            {
                Directory.Delete(_temporaryRoot, recursive: true);
            }
        }
    }

    private sealed class UploadWebApplicationFactory(string storageRoot) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:ApiKey"] = ApiKey,
                ["Catalog:StorageRoot"] = Path.Combine(storageRoot, "data"),
                ["Catalog:StarterDatabasePath"] = Path.Combine(AppContext.BaseDirectory, "artifacts", "catalog.db")
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IUploadItemProcessor>();
                services.AddSingleton<IUploadItemProcessor, UnavailableTestProcessor>();
            });
        }
    }

    private sealed class UnavailableTestProcessor : IUploadItemProcessor
    {
        public bool IsAvailable => false;
        public Task<UploadItemProcessingResult> ProcessAsync(UploadRecord upload, int sourceIndex, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("API integration test worker must remain idle.");
    }

    private sealed class NonSeekableReadStream(byte[] content) : Stream
    {
        private readonly MemoryStream _inner = new(content, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
