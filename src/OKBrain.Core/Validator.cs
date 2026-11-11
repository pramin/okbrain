using System.Globalization;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Footnotes;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using YamlDotNet.RepresentationModel;

namespace OKBrain.Core;

internal static class Validator
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseFootnotes().UsePreciseSourceLocation().Build();
    public static ValidationResult Validate(BundleSnapshot bundle, bool strict, CancellationToken cancellationToken = default)
    {
        var findings = new List<Finding>();
        var markdownPaths = bundle.Documents.Select(d => d.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var document in bundle.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            findings.AddRange(document.ParseFindings);
            var markdown = Markdown.Parse(document.Body, Pipeline);
            if (document.Kind == DocumentKind.Concept)
            {
                if (document.Metadata != null)
                {
                    var typeNode = MetadataAccess.Get(document.Metadata, "type");
                    if (!MetadataAccess.IsString(typeNode, out _))
                        AddNode(findings, document, "concept.type", typeNode ?? document.Metadata,
                            "Concept requires a non-empty string type.", FindingKind.Conformance);
                    MetadataValidator.Check(document, markdown, findings);
                }
            }
            else if (document.Kind == DocumentKind.Index)
                CheckIndex(document, markdown, findings);
            else
                CheckLog(document, markdown, findings);
            CheckLinks(bundle, document, markdown, markdownPaths, findings);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var ordered = findings.OrderBy(f => f.File, StringComparer.Ordinal).ThenBy(f => f.Line)
            .ThenBy(f => f.Column).ThenBy(f => f.Rule, StringComparer.Ordinal).ToArray();
        return new(!ordered.Any(f => f.Kind == FindingKind.Conformance || strict), strict, ordered);
    }

    private static void CheckIndex(BundleDocument document, MarkdownDocument markdown, List<Finding> findings)
    {
        if (document.Path == "index.md" && document.Metadata != null)
        {
            var version = MetadataAccess.Get(document.Metadata, "okf_version");
            if (version != null && (version is not YamlScalarNode scalar || scalar.Value == null ||
                !Regex.IsMatch(scalar.Value, @"^\d+\.\d+$")))
                AddNode(findings, document, "index.version", version, "okf_version should use the major.minor form.", FindingKind.OptionalMetadata);
        }
        if (!markdown.Descendants<HeadingBlock>().Any())
            Add(findings, document, "index.section", document.BodyOffset,
                "Index must contain at least one section heading.", FindingKind.Conformance);
    }

    private static void CheckLog(BundleDocument document, MarkdownDocument markdown, List<Finding> findings)
    {
        var headings = markdown.OfType<HeadingBlock>().Where(h => h.Level == 2).ToArray();
        if (headings.Length == 0)
            Add(findings, document, "log.date-heading", document.BodyOffset,
                "Log must group its entries under YYYY-MM-DD headings.", FindingKind.Conformance);
        DateOnly? previous = null;
        foreach (var heading in headings)
        {
            var headingText = HeadingText(document.Body, heading);
            if (!Regex.IsMatch(headingText, @"^\d{4}-\d{2}-\d{2}$") ||
                !DateOnly.TryParseExact(headingText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                Add(findings, document, "log.date-heading", document.BodyOffset + heading.Span.Start,
                    "Log date headings must use a valid YYYY-MM-DD date.", FindingKind.Conformance);
                continue;
            }
            if (previous != null && date > previous)
                Add(findings, document, "log.order", document.BodyOffset + heading.Span.Start,
                    "Log date headings must be newest first.", FindingKind.Conformance);
            previous = date;
        }
        bool inDateGroup = false;
        foreach (var block in markdown)
        {
            if (block is HeadingBlock heading)
            {
                if (heading.Level == 2) inDateGroup = true;
                else if (heading.Level == 1) inDateGroup = false;
                else
                    Add(findings, document, "log.entry", document.BodyOffset + heading.Span.Start,
                        "Log entries must be a flat list under date headings.", FindingKind.Conformance);
            }
            else if (block is ListBlock list)
            {
                if (!inDateGroup)
                    Add(findings, document, "log.entry", document.BodyOffset + list.Span.Start,
                        "Log entries must be grouped under a date heading.", FindingKind.Conformance);
                foreach (var item in list.Descendants<ListItemBlock>())
                    if (item.Descendants<ListBlock>().Any())
                        Add(findings, document, "log.entry", document.BodyOffset + item.Span.Start,
                            "Log entries must be a flat list under date headings.", FindingKind.Conformance);
            }
            else if (block is not (LinkReferenceDefinitionGroup or FootnoteGroup))
                Add(findings, document, "log.entry", document.BodyOffset + block.Span.Start,
                    "Log entries must be a flat list under date headings.", FindingKind.Conformance);
        }
    }

    internal static string HeadingText(string body, HeadingBlock heading)
    {
        int start = heading.Span.Start;
        int end = Math.Min(heading.Span.End + 1, body.Length);
        var line = body[start..end].Split('\n', '\r')[0].Trim();
        if (line.StartsWith('#')) line = line.TrimStart('#').Trim().TrimEnd('#').TrimEnd();
        return line;
    }

    private static void CheckLinks(BundleSnapshot bundle, BundleDocument document, MarkdownDocument markdown,
        HashSet<string> markdownPaths, List<Finding> findings)
    {
        foreach (var link in markdown.Descendants<LinkInline>())
        {
            var url = link.Url;
            if (string.IsNullOrEmpty(url) || url.StartsWith('#') || url.StartsWith('?') || url.StartsWith("//") ||
                Regex.IsMatch(url, @"^[a-zA-Z][a-zA-Z0-9+.-]*:")) continue;
            var target = url.Split('#', '?')[0];
            if (target.Length == 0) continue;
            try
            {
                target = Uri.UnescapeDataString(target);
                var basedir = target.StartsWith('/') ? bundle.RootPath :
                    System.IO.Path.Combine(bundle.RootPath, System.IO.Path.GetDirectoryName(document.Path) ?? "");
                var fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(basedir, target.TrimStart('/')));
                var relative = System.IO.Path.GetRelativePath(bundle.RootPath, fullPath).Replace(System.IO.Path.DirectorySeparatorChar, '/');
                var outside = relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || System.IO.Path.IsPathRooted(relative);
                bool exists = !outside && (markdownPaths.Contains(relative) ||
                    ((!relative.EndsWith(".md", StringComparison.Ordinal) && File.Exists(fullPath) || Directory.Exists(fullPath)) &&
                     BundleReader.IsWithin(BundleReader.PhysicalPath(bundle.RootPath), BundleReader.PhysicalPath(fullPath))));
                if (!exists)
                {
                    Add(findings, document, "link.broken", document.BodyOffset + link.Span.Start,
                        $"Local link target '{url}' does not exist within the bundle.", FindingKind.BrokenLink);
                    findings[^1] = findings[^1] with { Subject = relative };
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or UriFormatException)
            {
                Add(findings, document, "link.broken", document.BodyOffset + link.Span.Start,
                    $"Invalid local link target '{url}': {ex.Message}", FindingKind.BrokenLink);
            }
        }
    }

    internal static void AddNode(List<Finding> findings, BundleDocument document, string rule, YamlNode? node,
        string message, FindingKind kind = FindingKind.OptionalMetadata)
    {
        var (line, column) = MetadataAccess.Location(node);
        findings.Add(new(rule, document.Path, line, column, message, kind));
    }

    internal static void Add(List<Finding> findings, BundleDocument document, string rule, int offset, string message, FindingKind kind)
    {
        var (line, column) = TextLocation.At(document.Text, offset);
        findings.Add(new(rule, document.Path, line, column, message, kind));
    }
}
