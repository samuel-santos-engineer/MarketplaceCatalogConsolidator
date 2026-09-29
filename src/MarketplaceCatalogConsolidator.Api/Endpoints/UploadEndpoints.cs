using System.Buffers;
using System.Globalization;
using MarketplaceCatalogConsolidator.Api.Contracts;
using MarketplaceCatalogConsolidator.Api.Operations;
using MarketplaceCatalogConsolidator.Application.Uploads;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.OpenApi;
using Microsoft.Net.Http.Headers;

namespace MarketplaceCatalogConsolidator.Api.Endpoints;

internal static class UploadEndpoints
{
    public static IEndpointRouteBuilder MapUploadEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/uploads", HandleAsync)
            .RequireRateLimiting(ApiHardening.UploadPolicy)
            .Produces<UploadAcceptedResponse>(StatusCodes.Status202Accepted)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status401Unauthorized)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorResponse>(StatusCodes.Status413PayloadTooLarge)
            .Produces<ApiErrorResponse>(StatusCodes.Status415UnsupportedMediaType)
            .Produces<ApiErrorResponse>(StatusCodes.Status429TooManyRequests)
            .Produces<ApiErrorResponse>(StatusCodes.Status500InternalServerError)
            .Produces<ApiErrorResponse>(StatusCodes.Status503ServiceUnavailable)
            .Produces<ApiErrorResponse>(StatusCodes.Status507InsufficientStorage)
            .WithName("UploadCatalog")
            .WithSummary("Accept a product catalog JSON file")
            .WithDescription("Accepts exactly one UTF-8 JSON array file in multipart form data. Requires Idempotency-Key (UUID v4) and X-Api-Key headers.");

        return endpoints;
    }

    public static Task TransformOpenApiAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(context.Description.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)
            || context.Description.RelativePath != "api/v1/uploads")
        {
            return Task.CompletedTask;
        }
        _ = cancellationToken;
        operation.Parameters = (operation.Parameters ?? []).Select(parameter =>
        {
            var documentedParameter = new OpenApiParameter
            {
                Name = parameter.Name,
                In = parameter.In,
                Required = parameter.Required,
                Deprecated = parameter.Deprecated,
                Style = parameter.Style,
                Explode = parameter.Explode,
                AllowReserved = parameter.AllowReserved,
                Schema = parameter.Schema,
                Examples = parameter.Examples,
                Example = parameter.Example,
                Content = parameter.Content
            };

            if (documentedParameter.Name == "Idempotency-Key")
            {
                documentedParameter.Required = true;
                documentedParameter.Description = "RFC 4122 UUID version 4 used to safely retry this upload.";
            }
            else if (documentedParameter.Name == "X-Api-Key")
            {
                documentedParameter.Required = true;
                documentedParameter.Description = "Upload API key configured by the host. No key is included in this documentation.";
            }

            return (IOpenApiParameter)documentedParameter;
        }).ToList();

        operation.RequestBody = new OpenApiRequestBody
        {
            Description = "Exactly one UTF-8 JSON array file in the multipart field named file.",
            Required = true,
            Content = new Dictionary<string, OpenApiMediaType>
            {
                ["multipart/form-data"] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchema
                    {
                        Type = JsonSchemaType.Object,
                        Required = new HashSet<string>(StringComparer.Ordinal) { "file" },
                        Properties = new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal)
                        {
                            ["file"] = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" }
                        }
                    }
                }
            }
        };

        return Task.CompletedTask;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext context,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKeyHeader,
        [FromHeader(Name = "X-Api-Key")] string? apiKeyHeader,
        IConfiguration configuration,
        UploadAcceptanceService acceptanceService,
        CancellationToken cancellationToken)
    {
        var traceId = Guid.NewGuid().ToString("D");
        if (!ApiKeyAuthentication.Matches(apiKeyHeader, configuration["Security:ApiKey"]))
        {
            return Error(traceId, StatusCodes.Status401Unauthorized, "unauthorized", "A valid API key is required.");
        }

        if (!TryParseVersion4(idempotencyKeyHeader, out var idempotencyKey))
        {
            return Error(traceId, StatusCodes.Status400BadRequest, "invalid_idempotency_key", "Idempotency-Key must be an RFC 4122 UUID version 4.");
        }

        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType)
            || !contentType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            return Error(traceId, StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type", "Content-Type must be multipart/form-data.");
        }

        var boundary = HeaderUtilities.RemoveQuotes(contentType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary))
        {
            return Error(traceId, StatusCodes.Status400BadRequest, "invalid_multipart", "The multipart boundary is missing.");
        }

        ParsedUploadFile file;
        var multipartSectionObserved = false;
        try
        {
            file = await ReadSingleFileAsync(context.Request, boundary, cancellationToken, () => multipartSectionObserved = true).ConfigureAwait(false);
        }
        catch (MultipartUploadException exception)
        {
            return Error(traceId, exception.StatusCode, exception.Code, exception.Message);
        }
        catch (InvalidDataException)
        {
            if (!multipartSectionObserved)
            {
                return Error(traceId, StatusCodes.Status400BadRequest, "missing_file", "Exactly one uploaded file field named 'file' is required.");
            }

            return Error(traceId, StatusCodes.Status400BadRequest, "invalid_multipart", "The multipart body is malformed.");
        }

        await using (file.Content.ConfigureAwait(false))
        {
            try
            {
                var accepted = await acceptanceService.AcceptAsync(
                    idempotencyKey,
                    file.FileName,
                    file.Content,
                    Guid.Parse(traceId),
                    cancellationToken).ConfigureAwait(false);

                var upload = accepted.Upload;
                var response = new UploadAcceptedResponse(upload.Id.ToString("D"), upload.Status.ToString(), upload.FileName, upload.FileHash);
                context.Response.Headers.Location = $"/api/v1/uploads/{upload.Id:D}/status";
                return TypedResults.Json(response, statusCode: StatusCodes.Status202Accepted);
            }
            catch (UploadAcceptanceException exception)
            {
                var (statusCode, code) = exception.Failure switch
                {
                    UploadAcceptanceFailure.InvalidRequest => (StatusCodes.Status400BadRequest, "invalid_upload"),
                    UploadAcceptanceFailure.PayloadTooLarge => (StatusCodes.Status413PayloadTooLarge, "payload_too_large"),
                    UploadAcceptanceFailure.InvalidJson => (StatusCodes.Status400BadRequest, "invalid_json"),
                    UploadAcceptanceFailure.IdempotencyConflict => (StatusCodes.Status409Conflict, "idempotency_conflict"),
                    UploadAcceptanceFailure.InsufficientStorage => (StatusCodes.Status507InsufficientStorage, "insufficient_storage"),
                    _ => (StatusCodes.Status400BadRequest, "invalid_upload")
                };
                return Error(traceId, statusCode, code, exception.Message);
            }
            catch (IOException)
            {
                return Error(traceId, StatusCodes.Status507InsufficientStorage, "insufficient_storage", "Persistent upload storage is unavailable.");
            }
            catch (UnauthorizedAccessException)
            {
                return Error(traceId, StatusCodes.Status507InsufficientStorage, "insufficient_storage", "Persistent upload storage is unavailable.");
            }
        }
    }

    private static async Task<ParsedUploadFile> ReadSingleFileAsync(HttpRequest request, string boundary, CancellationToken cancellationToken, Action sectionObserved)
    {
        var reader = new MultipartReader(boundary, request.Body);
        var fileCount = 0;
        ParsedUploadFile? parsedFile = null;
        try
        {
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                sectionObserved();
                if (string.IsNullOrWhiteSpace(section.ContentDisposition))
                {
                    throw BadRequest("missing_file", "Exactly one uploaded file field named 'file' is required.");
                }

                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                    || disposition is null
                    || !string.Equals(disposition.DispositionType.Value, "form-data", StringComparison.OrdinalIgnoreCase))
                {
                    throw BadMultipart();
                }

                var fieldName = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
                var isFile = disposition.FileName.HasValue || disposition.FileNameStar.HasValue;
                if (!isFile || !string.Equals(fieldName, "file", StringComparison.Ordinal))
                {
                    throw BadRequest("missing_file", "Exactly one uploaded file field named 'file' is required.");
                }

                fileCount++;
                if (fileCount > 1)
                {
                    throw BadRequest("multiple_files", "Exactly one uploaded file field named 'file' is allowed.");
                }

                if (section.Headers is { } headers
                    && headers.TryGetValue(HeaderNames.ContentLength, out var declaredLength)
                    && long.TryParse(declaredLength.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLength)
                    && parsedLength >= UploadAcceptanceService.MaximumFileSizeBytes)
                {
                    throw TooLarge();
                }

                var content = await ReadFileSectionBoundedAsync(section.Body, cancellationToken).ConfigureAwait(false);
                var suppliedName = disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName;
                var fileName = HeaderUtilities.RemoveQuotes(suppliedName).Value ?? string.Empty;
                parsedFile = new ParsedUploadFile(fileName, content);
            }
        }
        catch
        {
            if (parsedFile is not null)
            {
                await parsedFile.Content.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }

        if (fileCount != 1 || parsedFile is null)
        {
            throw BadRequest("missing_file", "Exactly one uploaded file field named 'file' is required.");
        }

        if (parsedFile.Content.Length == 0)
        {
            await parsedFile.Content.DisposeAsync().ConfigureAwait(false);
            throw BadRequest("empty_file", "The uploaded file is empty.");
        }

        parsedFile.Content.Position = 0;
        return parsedFile;
    }

    private static async Task<MemoryStream> ReadFileSectionBoundedAsync(Stream section, CancellationToken cancellationToken)
    {
        var content = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var read = await section.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (content.Length + read >= UploadAcceptanceService.MaximumFileSizeBytes)
                {
                    throw TooLarge();
                }

                await content.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            content.Position = 0;
            return content;
        }
        catch
        {
            await content.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool TryParseVersion4(string? value, out Guid key)
    {
        key = Guid.Empty;
        if (value is null || !Guid.TryParseExact(value.Trim(), "D", out var parsed))
        {
            return false;
        }

        var compact = parsed.ToString("N");
        if (compact[12] != '4' || compact[16] is not ('8' or '9' or 'a' or 'b'))
        {
            return false;
        }

        key = parsed;
        return true;
    }

    private static IResult Error(string traceId, int statusCode, string code, string message) =>
        TypedResults.Json(new ApiErrorResponse(traceId, code, message), statusCode: statusCode);

    private static MultipartUploadException BadMultipart() =>
        BadRequest("invalid_multipart", "The multipart body must contain exactly one file field named 'file'.");

    private static MultipartUploadException BadRequest(string code, string message) =>
        new(StatusCodes.Status400BadRequest, code, message);

    private static MultipartUploadException TooLarge() =>
        new(StatusCodes.Status413PayloadTooLarge, "payload_too_large", $"The uploaded file must be smaller than {UploadAcceptanceService.MaximumFileSizeBytes} bytes.");

    private sealed record ParsedUploadFile(string FileName, MemoryStream Content);

    private sealed class MultipartUploadException(int statusCode, string code, string message) : Exception(message)
    {
        public int StatusCode { get; } = statusCode;
        public string Code { get; } = code;
    }
}
