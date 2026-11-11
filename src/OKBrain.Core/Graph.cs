using Markdig;
using Markdig.Extensions.Footnotes;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using System.Text.RegularExpressions;

namespace OKBrain.Core;

public sealed record GraphNode(string Id, string Path, string? Type, string? Title);
public sealed record GraphEdge(string Source, string Target, string Relation, int Line, int Column, bool NonDocumentTarget = false);
public sealed record GraphExport(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges);
public sealed record GraphFindings(IReadOnlyList<string> Orphans, IReadOnlyList<string> DeadEnds, IReadOnlyList<GraphEdge> BrokenLinks);
public sealed record GraphNeighbor(string Path, int Depth);

public static class BrainGraph
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseFootnotes().UsePreciseSourceLocation().Build();

    public static GraphExport Export(string bundlePath, CancellationToken cancellationToken = default)
    {
        var bundle = Brain.LoadBundle(bundlePath, cancellationToken);
        var edges = Links(bundle, cancellationToken);
        var nodes = bundle.Documents.Select(d => new GraphNode(d.Path, d.Path, d.Type, d.Title)).ToList();
        return new(nodes, edges);
    }

    private static bool IsMarkdown(string path) => Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<GraphEdge> Links(BundleSnapshot bundle, CancellationToken cancellationToken)
    {
        var edges = new List<GraphEdge>();
        foreach (var doc in bundle.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parsed = Markdown.Parse(doc.Body, Pipeline);
            foreach (var link in parsed.Descendants<LinkInline>())
            {
                var url = link.Url;
                if (string.IsNullOrEmpty(url) || url.StartsWith('?') || url.StartsWith("//") || Regex.IsMatch(url, @"^[a-zA-Z][a-zA-Z0-9+.-]*:")) continue;
                var path = url.Split('#', '?')[0];
                string target;
                try
                {
                    if (path.Length == 0) target = doc.Path;
                    else
                    {
                        var dir = path.StartsWith('/') ? bundle.RootPath : Path.Combine(bundle.RootPath, Path.GetDirectoryName(doc.Path) ?? "");
                        target = Path.GetRelativePath(bundle.RootPath, Path.GetFullPath(Path.Combine(dir, Uri.UnescapeDataString(path).TrimStart('/')))).Replace('\\', '/');
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or UriFormatException or NotSupportedException or IOException) { target = path; }
                var (line, column) = TextLocation.At(doc.Text, doc.BodyOffset + link.Span.Start);
                edges.Add(new(doc.Path, target, link.Title ?? "links-to", line, column, !IsMarkdown(target)));
            }
        }
        return edges;
    }

    public static IReadOnlyList<GraphEdge> Backlinks(string bundlePath, string document, CancellationToken cancellationToken = default) =>
        Export(bundlePath, cancellationToken).Edges.Where(e => e.Target == document).ToArray();

    public static IReadOnlyList<GraphNeighbor> Neighbors(string bundlePath, string document, int depth = 1, string direction = "both", string? relation = null, CancellationToken cancellationToken = default)
    {
        if (depth < 0) throw new ArgumentException("Depth must be nonnegative.", nameof(depth));
        if (direction is not ("in" or "out" or "both")) throw new ArgumentException("Direction must be in, out, or both.", nameof(direction));
        var graph = Export(bundlePath, cancellationToken);
        if (!graph.Nodes.Any(n => n.Path == document)) throw BundleReader.Error("document.not-found", document, "Document does not exist.");
        var nodes = graph.Nodes.Select(n => n.Path).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { document };
        var result = new List<GraphNeighbor>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((document, 0));
        while (queue.TryDequeue(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.Depth == depth) continue;
            foreach (var edge in graph.Edges.Where(e => relation == null || e.Relation == relation))
            {
                string? next = direction != "in" && edge.Source == current.Path ? edge.Target :
                    direction != "out" && edge.Target == current.Path ? edge.Source : null;
                if (next != null && nodes.Contains(next) && seen.Add(next))
                {
                    result.Add(new(next, current.Depth + 1));
                    queue.Enqueue((next, current.Depth + 1));
                }
            }
        }
        return result;
    }

    public static GraphFindings Findings(string bundlePath, CancellationToken cancellationToken = default)
    {
        var bundle = Brain.LoadBundle(bundlePath, cancellationToken);
        var links = Links(bundle, cancellationToken);
        var graph = new GraphExport(bundle.Documents.Select(d => new GraphNode(d.Path, d.Path, d.Type, d.Title)).ToArray(), links);
        var brokenLocations = Validator.Validate(bundle, true, cancellationToken).Findings.Where(f => f.Kind == FindingKind.BrokenLink)
            .Select(f => (f.File, f.Line, f.Column)).ToHashSet();
        var broken = links.Where(e => brokenLocations.Contains((e.Source, e.Line, e.Column))).ToArray();
        var orphans = graph.Nodes.Where(n => !graph.Edges.Any(e => e.Source == n.Path || e.Target == n.Path)).Select(n => n.Path).ToArray();
        var deadEnds = graph.Nodes.Where(n => !graph.Edges.Any(e => e.Source == n.Path)).Select(n => n.Path).ToArray();
        return new(orphans, deadEnds, broken);
    }

    public static IReadOnlyDictionary<string, int> Relations(string bundlePath, CancellationToken cancellationToken = default) => Export(bundlePath, cancellationToken).Edges
        .GroupBy(e => e.Relation, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
}
