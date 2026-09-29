using System.Text.Json;
using MarketplaceCatalogConsolidator.Domain;

namespace MarketplaceCatalogConsolidator.Application.Parsing;

public sealed class SourceDocumentFormatException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class SourceDocumentParser
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32
    };

    public async Task<IReadOnlyList<SourceProductEntry>> ParseAsync(Stream utf8Json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(utf8Json);

        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(utf8Json, DocumentOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new SourceDocumentFormatException("The source document is not valid UTF-8 JSON.", exception);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new SourceDocumentFormatException("The source document must be a JSON array.");
            }

            var entries = new List<SourceProductEntry>(document.RootElement.GetArrayLength());
            var sourceIndex = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(ParseEntry(element, sourceIndex));
                sourceIndex++;
            }

            return entries;
        }
    }

    private static SourceProductEntry ParseEntry(JsonElement element, int sourceIndex)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return new SourceProductEntry(sourceIndex, null, null, null, null, null, "Each source array entry must be a JSON object.");
        }

        var invalidFields = new List<string>();
        var id = ReadOptionalString(element, "Id", invalidFields);
        var sellerName = ReadOptionalString(element, "SellerName", invalidFields);
        var name = ReadOptionalString(element, "Name", invalidFields);
        var brand = ReadOptionalString(element, "Brand", invalidFields);
        var category = ReadOptionalString(element, "Category", invalidFields);

        return new SourceProductEntry(
            sourceIndex,
            id,
            sellerName,
            name,
            brand,
            category,
            invalidFields.Count == 0 ? null : $"Fields must be JSON strings: {string.Join(", ", invalidFields)}.");
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName, ICollection<string> invalidFields)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        invalidFields.Add(propertyName);
        return value.GetRawText();
    }
}
