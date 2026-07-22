using System.Globalization;
using System.Text;
using Quiver.Text;

namespace Quiver.Index.FullText;

/// <summary>全文definitionをcatalogのtarget payloadへ現行形式で格納するcodec。</summary>
internal static class FullTextDefinitionCodec
{
    private const string Prefix = "qft2;";

    internal static string Encode(FullTextIndexDefinition definition)
    {
        FullTextSegmentPolicy policy = definition.SegmentPolicy ?? new();
        string scope = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(definition.Target.Scope ?? string.Empty));
        string filters = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(EncodeFilters(definition.Filters)));
        return string.Join(
            ';',
            "qft2",
            ((int)definition.Target.OwnerKind).ToString(CultureInfo.InvariantCulture),
            scope,
            definition.K1.ToString("R", CultureInfo.InvariantCulture),
            definition.B.ToString("R", CultureInfo.InvariantCulture),
            policy.MaximumDeltaEntries.ToString(CultureInfo.InvariantCulture),
            policy.MaximumSegments.ToString(CultureInfo.InvariantCulture),
            policy.MaximumTombstoneRatio.ToString("R", CultureInfo.InvariantCulture),
            filters);
    }

    internal static FullTextIndexDefinition Decode(
        string name,
        string encoded,
        string propertyKey,
        string tokenizerId)
    {
        if (encoded.StartsWith(Prefix, StringComparison.Ordinal))
        {
            string[] parts = encoded.Split(';');
            if (parts.Length == 9
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int rawKind)
                && Enum.IsDefined((PropertyOwnerKind)rawKind)
                && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double k1)
                && double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double b)
                && int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int maximumDeltaEntries)
                && int.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out int maximumSegments)
                && double.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out double maximumTombstoneRatio))
            {
                string decodedScope = Encoding.UTF8.GetString(
                    Convert.FromBase64String(parts[2]));
                IReadOnlyList<ITokenFilter>? filters = DecodeFilters(
                    Encoding.UTF8.GetString(Convert.FromBase64String(parts[8])));
                return new(
                    name,
                    new(
                        (PropertyOwnerKind)rawKind,
                        propertyKey,
                        decodedScope.Length == 0 ? null : decodedScope),
                    tokenizerId,
                    Filters: filters,
                    K1: k1,
                    B: b,
                    SegmentPolicy: new(
                        maximumDeltaEntries,
                        maximumSegments,
                        maximumTombstoneRatio));
            }
        }

        throw new InvalidDataException(
            "Full-text definition does not use the current qft2 catalog format.");
    }

    private static string EncodeFilters(IReadOnlyList<ITokenFilter>? filters)
    {
        if (filters is not { Count: > 0 })
            return string.Empty;
        var encoded = new List<string>(filters.Count);
        foreach (ITokenFilter filter in filters)
        {
            switch (filter)
            {
                case LowercaseFilter:
                    encoded.Add("l");
                    break;
                case StopWordFilter stopWords:
                    encoded.Add("s," + string.Join(
                        ',',
                        stopWords.StopWords
                            .Order(StringComparer.Ordinal)
                            .Select(static word => Convert.ToBase64String(
                                Encoding.UTF8.GetBytes(word)))));
                    break;
                default:
                    throw new NotSupportedException(
                        $"Token filter '{filter.FilterId}' cannot be persisted.");
            }
        }
        return string.Join('|', encoded);
    }

    private static IReadOnlyList<ITokenFilter>? DecodeFilters(string encoded)
    {
        if (encoded.Length == 0)
            return null;
        var filters = new List<ITokenFilter>();
        foreach (string descriptor in encoded.Split('|'))
        {
            if (descriptor == "l")
            {
                filters.Add(new LowercaseFilter());
                continue;
            }
            if (descriptor.StartsWith("s,", StringComparison.Ordinal))
            {
                string[] words = descriptor[2..]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries);
                filters.Add(new StopWordFilter(words.Select(static word =>
                    Encoding.UTF8.GetString(Convert.FromBase64String(word)))));
                continue;
            }
            throw new InvalidDataException(
                $"Unknown persisted token filter descriptor '{descriptor}'.");
        }
        return filters;
    }
}
