namespace MarketplaceCatalogConsolidator.Api.Contracts;

/// <summary>Describes a safe, traceable API error response.</summary>
public sealed record ApiErrorResponse(string TraceId, string Code, string Message);
