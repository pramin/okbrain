namespace OKBrain.Core;

/// <summary>Shared read operations. Each operation reads the current bundle from disk.</summary>
public static class Brain
{
    public static BundleSnapshot LoadBundle(string bundlePath, CancellationToken cancellationToken = default) => BundleReader.Load(bundlePath, cancellationToken);

    public static ValidationResult Validate(string bundlePath, bool strict = false, CancellationToken cancellationToken = default) =>
        Validator.Validate(LoadBundle(bundlePath, cancellationToken), strict, cancellationToken);

    public static IReadOnlyList<SearchHit> Search(string bundlePath, string? query = null, CancellationToken cancellationToken = default)
    {
        var hits = new List<SearchHit>();
        foreach (var document in LoadBundle(bundlePath, cancellationToken).Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(query))
            {
                hits.Add(new(document.Path, document.Kind, document.Type, document.Title, null, null, null));
                continue;
            }
            int index = document.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;
            var (line, column) = TextLocation.At(document.Text, index);
            int start = index;
            while (start > 0 && document.Text[start - 1] is not '\n' and not '\r') start--;
            int end = index;
            while (end < document.Text.Length && document.Text[end] is not '\n' and not '\r') end++;
            hits.Add(new(document.Path, document.Kind, document.Type, document.Title, line, column, document.Text[start..end]));
        }
        return hits;
    }

    public static BundleStats Stats(string bundlePath, CancellationToken cancellationToken = default)
    {
        var documents = LoadBundle(bundlePath, cancellationToken).Documents;
        var types = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var tags = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var document in documents.Where(d => d.Kind == DocumentKind.Concept))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (document.Type is { } type) Increment(types, type);
            if (MetadataAccess.Get(document.Metadata, "tags") is YamlDotNet.RepresentationModel.YamlSequenceNode sequence)
                foreach (var tag in sequence.Children.Select(node => MetadataAccess.IsString(node, out var value) ? value : null)
                    .Where(value => value != null).Distinct(StringComparer.Ordinal))
                    Increment(tags, tag!);
        }
        return new(documents.Count, documents.Count(d => d.Kind == DocumentKind.Concept),
            documents.Count(d => d.Kind == DocumentKind.Index), documents.Count(d => d.Kind == DocumentKind.Log), types, tags);
    }

    private static void Increment(IDictionary<string, int> counts, string value) =>
        counts[value] = counts.TryGetValue(value, out var count) ? count + 1 : 1;
}

internal static class TextLocation
{
    public static (int Line, int Column) At(string text, int index)
    {
        int line = 1, column = 1;
        for (int i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\r') { line++; column = 1; if (i + 1 < index && text[i + 1] == '\n') i++; }
            else if (text[i] == '\n') { line++; column = 1; }
            else column++;
        }
        return (line, column);
    }
}
