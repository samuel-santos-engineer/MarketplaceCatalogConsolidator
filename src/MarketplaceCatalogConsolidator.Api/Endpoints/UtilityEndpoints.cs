using MarketplaceCatalogConsolidator.Api.Contracts;
using MarketplaceCatalogConsolidator.Api.Operations;

namespace MarketplaceCatalogConsolidator.Api.Endpoints;

internal static class UtilityEndpoints
{
    public static void MapUtilityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v2/random-uuid", () => TypedResults.Ok(new RandomUuidResponse(Guid.NewGuid().ToString("D"))))
            .RequireRateLimiting(ApiHardening.ReadPolicy)
            .WithName("GenerateRandomUuid")
            .WithSummary("Generate a random UUID v4")
            .Produces<RandomUuidResponse>(200)
            .Produces<ApiErrorResponse>(429)
            .Produces<ApiErrorResponse>(500);
    }
}
