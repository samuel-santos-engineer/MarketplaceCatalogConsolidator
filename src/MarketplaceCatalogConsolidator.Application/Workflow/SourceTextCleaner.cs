using System.Text;
using MarketplaceCatalogConsolidator.Domain;

namespace MarketplaceCatalogConsolidator.Application.Workflow;

public static class SourceTextCleaner
{
    public static string? CleanSourceId(string? value) =>
        Guid.TryParseExact(Clean(value), "D", out var guid) ? guid.ToString("D") : null;

    public static string? CleanCategory(string? value)
    {
        var cleaned = Clean(value);
        return cleaned is not null && string.Equals(TextNormalization.NormalizeForComparison(cleaned), "photo", StringComparison.Ordinal)
            ? "Photography"
            : cleaned;
    }

    public static string? Clean(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (!Rune.IsLetterOrDigit(rune) && !Rune.IsPunctuation(rune) && rune.Value is not (>= 0x21 and <= 0x7E))
            {
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(rune.ToString());
        }

        return builder.Length == 0 ? null : builder.ToString();
    }
}
