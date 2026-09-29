using System.Security.Cryptography;
using System.Text;

namespace MarketplaceCatalogConsolidator.Api.Operations;

internal static class ApiKeyAuthentication
{
    public static bool Matches(string? suppliedKey, string? configuredKey)
    {
        if (string.IsNullOrEmpty(configuredKey)) return false;
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey));
        var suppliedHash = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedKey ?? string.Empty));
        return CryptographicOperations.FixedTimeEquals(expectedHash, suppliedHash);
    }
}
