using System.Text.RegularExpressions;

namespace MarketplaceCatalogConsolidator.Domain;

public static partial class SqlControlSequencePolicy
{
    private const string SqlKeywords = "SELECT|INSERT|UPDATE|DELETE|DROP|ALTER|CREATE|PRAGMA|ATTACH|DETACH|UNION|EXECUTE|EXEC";

    [GeneratedRegex(@";\s*(?:" + SqlKeywords + @")\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex KeywordAfterSemicolon();

    [GeneratedRegex(@"(?:--|/\*|\*/)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex SqlCommentMarker();

    [GeneratedRegex(@"\b(?:" + SqlKeywords + @")\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex SqlKeyword();

    public static string? GetRejectionReason(string fieldName, string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);

        if (value is null)
        {
            return null;
        }

        if (KeywordAfterSemicolon().IsMatch(value)
            || (SqlCommentMarker().IsMatch(value) && SqlKeyword().IsMatch(value)))
        {
            return $"{fieldName} contains a suspicious SQL control sequence";
        }

        return null;
    }
}
