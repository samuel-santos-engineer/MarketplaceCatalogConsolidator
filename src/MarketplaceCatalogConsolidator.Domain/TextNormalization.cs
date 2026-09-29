using System.Globalization;
using System.Text;

namespace MarketplaceCatalogConsolidator.Domain;

public static class TextNormalization
{
    public static string NormalizeProductNameForComparison(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var name = value.TrimEnd();
        if (name.Length >= 2 && name[^1] == '"' && char.IsDigit(name[^2]))
        {
            name = name[..^1];
        }

        return NormalizeForComparison(name);
    }

    public static string NormalizeForComparison(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
