using Markdig;
using Markdig.Syntax.Inlines;
using Markdig.Syntax;
using System.Text.RegularExpressions;

namespace OKBrain.Core;

public static partial class BrainWrites
{
    private static readonly MarkdownPipeline LinkPipeline = new MarkdownPipelineBuilder().UsePreciseSourceLocation().Build();

    public static WriteResult Delete(string bundlePath, string document, CancellationToken cancellationToken = default) => ChangePath(bundlePath, document, null, cancellationToken);
    public static WriteResult Move(string bundlePath, string from, string to, CancellationToken cancellationToken = default) => ChangePath(bundlePath, from, to, cancellationToken);

    private static WriteResult ChangePath(string bundlePath, string from, string? to, CancellationToken cancellationToken) => Apply(bundlePath, snapshot =>
    {
        from = DocumentPath(from);
        if (to != null) to = DocumentPath(to);
        var files = snapshot.Documents.ToDictionary(d => d.Path, d => d.Text, StringComparer.Ordinal);
        if (!files.ContainsKey(from)) throw BundleReader.Error("document.not-found", from, "Document does not exist.");
        if (to != null)
        {
            var parent = Path.GetDirectoryName(Path.Combine(snapshot.RootPath, to))!;
            while (!Directory.Exists(parent)) parent = Path.GetDirectoryName(parent)!;
            if (!BundleReader.IsWithin(BundleReader.PhysicalPath(snapshot.RootPath), BundleReader.PhysicalPath(parent)))
                throw BundleReader.Error("document.path-escape", to, "Destination parent resolves outside the bundle.");
        }
        if (to != null && (files.ContainsKey(to) || File.Exists(Path.Combine(snapshot.RootPath, to)) || Directory.Exists(Path.Combine(snapshot.RootPath, to))))
            throw BundleReader.Error("document.exists", to, "Destination already exists.");
        foreach (var doc in snapshot.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (doc.Path == from && to == null) continue;
            string outputPath = doc.Path == from ? to! : doc.Path;
            var replacements = new Dictionary<(int, int), string>();
            var labels = new Dictionary<(int, int), (int Start, int End)>();
            var markdown = Markdown.Parse(doc.Body, LinkPipeline);
            foreach (var link in markdown.Descendants<LinkInline>())
            {
                var url = link.Url;
                if (string.IsNullOrEmpty(url) || url.StartsWith('#') || url.StartsWith('?') || url.StartsWith("//") || Regex.IsMatch(url, @"^[a-zA-Z][a-zA-Z0-9+.-]*:")) continue;
                string rawPath = url.Split('#', '?')[0];
                string target;
                try
                {
                    var baseDir = rawPath.StartsWith('/') ? snapshot.RootPath : Path.Combine(snapshot.RootPath, Path.GetDirectoryName(doc.Path) ?? "");
                    target = Path.GetRelativePath(snapshot.RootPath, Path.GetFullPath(Path.Combine(baseDir, Uri.UnescapeDataString(rawPath).TrimStart('/')))).Replace('\\', '/');
                }
                catch (Exception ex) when (ex is ArgumentException or UriFormatException or NotSupportedException) { continue; }
                bool inbound = target == from;
                if (to == null && inbound)
                {
                    int start = doc.BodyOffset + link.Span.Start, end = doc.BodyOffset + link.Span.End;
                    var source = doc.Text[start..(end + 1)];
                    int open = source.IndexOf('['), close = source.LastIndexOf(']');
                    // Preserve the link label including its Markdown formatting.
                    if (link.FirstChild != null && link.LastChild != null)
                    {
                        int labelStart = doc.BodyOffset + link.FirstChild.Span.Start;
                        int labelEnd = doc.BodyOffset + link.LastChild.Span.End + 1;
                        replacements[(start, end + 1)] = doc.Text[labelStart..labelEnd];
                        labels[(start, end + 1)] = (labelStart, labelEnd);
                    }
                    else if (open >= 0 && close > open) replacements[(start, end + 1)] = source[(open + 1)..close];
                    else throw BundleReader.Error("link.rewrite", doc.Path, "Cannot safely unlink a link.");
                }
                else if (to != null && (inbound || (doc.Path == from && !rawPath.StartsWith('/'))))
                {
                    var dest = inbound ? to : target;
                    var relative = rawPath.StartsWith('/') ? "/" + dest : Path.GetRelativePath(Path.Combine(snapshot.RootPath, Path.GetDirectoryName(outputPath) ?? ""), Path.Combine(snapshot.RootPath, dest)).Replace('\\', '/');
                    var span = link.Reference?.UrlSpan ?? link.UrlSpan;
                    int start = doc.BodyOffset + span.Start, end = doc.BodyOffset + span.End + 1;
                    if (start < doc.BodyOffset || end > doc.Text.Length || end <= start)
                        throw BundleReader.Error("link.rewrite", doc.Path, "Cannot safely retarget a link.");
                    var originalTarget = doc.Text[start..end];
                    replacements[(start, end)] = Retarget(originalTarget, relative);
                }
            }
            // A link label may itself contain an image. Apply nested edits inside the
            // retained label before replacing the enclosing link.
            foreach (var label in labels.OrderBy(x => x.Key.Item2 - x.Key.Item1))
            {
                var value = doc.Text[label.Value.Start..label.Value.End];
                foreach (var child in replacements.Where(x => x.Key != label.Key &&
                    x.Key.Item1 >= label.Value.Start && x.Key.Item2 <= label.Value.End)
                    .OrderByDescending(x => x.Key.Item1).ToArray())
                {
                    int childStart = child.Key.Item1 - label.Value.Start;
                    int childEnd = child.Key.Item2 - label.Value.Start;
                    value = value[..childStart] + child.Value + value[childEnd..];
                    replacements.Remove(child.Key);
                }
                replacements[label.Key] = value;
            }
            var text = doc.Text;
            foreach (var item in replacements.OrderByDescending(x => x.Key.Item1))
                text = text[..item.Key.Item1] + item.Value + text[item.Key.Item2..];
            files.Remove(doc.Path);
            files[outputPath] = text;
        }
        if (to == null) files.Remove(from);
        var view = new BundleSnapshot(snapshot.RootPath, files.Select(x => BundleReader.ParseDocument(x.Key, x.Value)).ToArray());
        var affected = new HashSet<string>(StringComparer.Ordinal) { "" };
        foreach (var path in new[] { from, to }.OfType<string>())
        {
            var dir = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
            while (dir.Length > 0)
            {
                affected.Add(dir);
                dir = Path.GetDirectoryName(dir)?.Replace('\\', '/') ?? "";
            }
        }
        return Reindexed(view, affected, cancellationToken);
    }, finding => to != null && finding.File == from ? finding with { File = to } : finding, cancellationToken);

    private static string DocumentPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\') ||
            path.Split('/').Any(p => p is "" or "." or ".." or ".git") || !path.EndsWith(".md", StringComparison.Ordinal))
            throw BundleReader.Error("document.path", path ?? "", "A bundle-relative Markdown path is required.");
        return path;
    }

    private static string Retarget(string original, string relative)
    {
        bool angle = original.StartsWith('<') && original.EndsWith('>');
        var inner = angle ? original[1..^1] : original;
        int suffixStart = -1;
        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length) { i++; continue; }
            if (inner[i] is '?' or '#') { suffixStart = i; break; }
        }
        var path = suffixStart < 0 ? inner : inner[..suffixStart];
        var suffix = suffixStart < 0 ? "" : inner[suffixStart..];
        var oldParts = path.Split('/');
        var newParts = relative.Split('/');
        // Keep the exact spelling of unchanged path components, including escapes.
        string Decode(string part) => Uri.UnescapeDataString(Regex.Replace(part, @"\\([!""#$%&'()*+,\-./:;<=>?@\[\]\\^_`{|}~])", "$1"));
        int prefix = 0;
        while (prefix < oldParts.Length && prefix < newParts.Length && Decode(oldParts[prefix]) == newParts[prefix]) prefix++;
        int oldEnd = oldParts.Length - 1, newEnd = newParts.Length - 1;
        while (oldEnd >= prefix && newEnd >= prefix && Decode(oldParts[oldEnd]) == newParts[newEnd]) { oldEnd--; newEnd--; }
        var parts = new string[newParts.Length];
        for (int i = 0; i < newParts.Length; i++)
        {
            if (i < prefix) parts[i] = oldParts[i];
            else if (i > newEnd) parts[i] = oldParts[oldEnd + 1 + i - newEnd - 1];
            else if (newParts[i] is "." or "..") parts[i] = newParts[i];
            else
            {
                var sample = oldParts[Math.Clamp(oldEnd, 0, oldParts.Length - 1)];
                var encoded = Uri.EscapeDataString(newParts[i]);
                foreach (Match escape in Regex.Matches(sample, @"\\([!""#$%&'()*+,\-./:;<=>?@\[\]\\^_`{|}~])"))
                {
                    var character = escape.Groups[1].Value;
                    encoded = encoded.Replace(Uri.EscapeDataString(character), "\\" + character, StringComparison.OrdinalIgnoreCase);
                }
                if (angle && !sample.Contains("%20", StringComparison.OrdinalIgnoreCase)) encoded = encoded.Replace("%20", " ");
                else if (sample.Contains("\\ ")) encoded = encoded.Replace("%20", "\\ ");
                parts[i] = encoded;
            }
        }
        var rewritten = string.Join('/', parts);
        if (path.StartsWith("./", StringComparison.Ordinal) && !rewritten.StartsWith("../", StringComparison.Ordinal) && !rewritten.StartsWith("./", StringComparison.Ordinal)) rewritten = "./" + rewritten;
        return (angle ? "<" : "") + rewritten + suffix + (angle ? ">" : "");
    }
}
