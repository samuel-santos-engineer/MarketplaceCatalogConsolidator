using MarketplaceCatalogConsolidator.Api.Contracts;
using MarketplaceCatalogConsolidator.Api.Operations;
using MarketplaceCatalogConsolidator.Application.Ports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace MarketplaceCatalogConsolidator.Api.Endpoints;

internal static class LabResetEndpoints
{
    public static void MapLabResetEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v2/reset-database", HandleAsync)
            .RequireRateLimiting(ApiHardening.UploadPolicy)
            .WithName("LabResetDatabase")
            .WithSummary("Destructively reset the lab database")
            .WithDescription("Lab-only: permanently removes all uploads, reports, staged files, and seller links. Restores the starter catalog and reapplies migrations. Requires X-Api-Key and exact X-Reset-Confirmation: RESET_DATABASE; no request body. Competing storage operations wait on the shared workflow gate and use the clean state after reset. Production must disable/remove this endpoint or make a separate intentional decision.")
            .Produces<LabDatabaseResetResult>(200)
            .Produces<ApiErrorResponse>(400).Produces<ApiErrorResponse>(401)
            .Produces<ApiErrorResponse>(413).Produces<ApiErrorResponse>(429)
            .Produces<ApiErrorResponse>(500).Produces<ApiErrorResponse>(503);
    }

    public static Task TransformOpenApiAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.Description.RelativePath != "api/v2/reset-database") return Task.CompletedTask;
        operation.Parameters = new List<IOpenApiParameter>
        {
            new OpenApiParameter { Name = "X-Api-Key", In = ParameterLocation.Header, Required = true,
                Description = "Existing host-configured API key. No secret is included in this document.", Schema = new OpenApiSchema { Type = JsonSchemaType.String } },
            new OpenApiParameter { Name = "X-Reset-Confirmation", In = ParameterLocation.Header, Required = true,
                Description = "Exact case-sensitive value RESET_DATABASE acknowledges permanent deletion of lab data.", Schema = new OpenApiSchema { Type = JsonSchemaType.String } }
        };
        operation.RequestBody = null;
        return Task.CompletedTask;
    }

    private static async Task<IResult> HandleAsync(HttpContext context,
        [FromHeader(Name = "X-Api-Key")] string? apiKey,
        [FromHeader(Name = "X-Reset-Confirmation")] string? confirmation,
        IConfiguration configuration, ILabDatabaseResetService resetService, ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!ApiKeyAuthentication.Matches(apiKey, configuration["Security:ApiKey"]))
            return Error(401, "unauthorized", "A valid API key is required.");
        if (!string.Equals(confirmation, "RESET_DATABASE", StringComparison.Ordinal))
            return Error(400, "reset_not_confirmed", "Confirm the lab reset with X-Reset-Confirmation: RESET_DATABASE.");
        // Read one byte even for chunked bodies with no declared Content-Length.
        if (context.Request.ContentLength > 0 || await context.Request.Body.ReadAsync(new byte[1], cancellationToken) != 0)
            return Error(400, "invalid_request_body", "The lab reset request must have no body.");
        try
        {
            return TypedResults.Ok(await resetService.ResetAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (LabDatabaseResetException)
        {
            loggerFactory.CreateLogger("LabDatabaseReset").LogError("Lab reset initialization failed; storage remains unavailable until recovery");
            return Error(503, "reset_failed", "The lab reset could not be completed. Storage is unavailable until recovery.");
        }
    }

    private static IResult Error(int status, string code, string message) =>
        TypedResults.Json(new ApiErrorResponse(Guid.NewGuid().ToString("D"), code, message), statusCode: status);
}
