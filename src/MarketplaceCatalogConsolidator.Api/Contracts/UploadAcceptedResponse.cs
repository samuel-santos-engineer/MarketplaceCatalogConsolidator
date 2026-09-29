namespace MarketplaceCatalogConsolidator.Api.Contracts;

/// <summary>Describes an accepted catalog upload and its initial or current state.</summary>
public sealed record UploadAcceptedResponse(string UploadId, string Status, string FileName, string FileHash);
