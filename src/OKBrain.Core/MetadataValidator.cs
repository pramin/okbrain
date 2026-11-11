using System.Globalization;
using System.Text.RegularExpressions;
using Markdig.Syntax;
using Markdig.Extensions.Footnotes;
using YamlDotNet.RepresentationModel;

namespace OKBrain.Core;

internal sealed class MetadataValidator(BundleDocument document, List<Finding> findings)
{
    private readonly YamlMappingNode metadata = document.Metadata!;

    public static void Check(BundleDocument document, MarkdownDocument markdown, List<Finding> findings) =>
        new MetadataValidator(document, findings).CheckAll(markdown);

    private void CheckAll(MarkdownDocument markdown)
    {
        foreach (var key in new[] { "title", "description", "resource" }) OptionalString(metadata, key, key);
        Strings(metadata, "tags", "tags");
        if (MetadataAccess.Get(metadata, "status") is { } status &&
            (!MetadataAccess.IsString(status, out var value) || value is not ("draft" or "stable" or "deprecated")))
            Report("metadata.status", status, "status must be draft, stable, or deprecated.");
        Date(metadata, "stale_after", "stale_after");
        if (MetadataAccess.Get(metadata, "generated") is { } generated)
        {
            if (Mapping(generated, "generated") is { } map)
            {
                Actor(map, "by", "generated.by", required: true);
                Date(map, "at", "generated.at");
            }
        }
        if (MetadataAccess.Get(metadata, "verified") is { } verified)
        {
            var events = verified is YamlSequenceNode sequence ? sequence.Children : new[] { verified };
            foreach (var entry in events)
                if (Mapping(entry, "verified") is { } map)
                {
                    Actor(map, "by", "verified.by", required: true);
                    Date(map, "at", "verified.at", required: true);
                }
        }
        if (MetadataAccess.Get(metadata, "sources") is { } sources)
        {
            if (sources is not YamlSequenceNode sequence) Report("metadata.sources", sources, "sources must be a list of mappings.");
            else
            {
                var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in sequence.Children)
                    if (Mapping(entry, "sources") is { } map)
                    {
                        RequiredString(map, "resource", "sources.resource");
                        foreach (var key in new[] { "id", "title", "author" }) OptionalString(map, key, $"sources.{key}");
                        var id = MetadataAccess.Get(map, "id");
                        if (MetadataAccess.IsString(id, out var idValue) && !ids.Add(idValue))
                            Report("metadata.sources.id", id, $"Source id '{idValue}' is duplicated.");
                        var count = MetadataAccess.Get(map, "usage_count");
                        if (count != null && (!MetadataAccess.TryInteger(count, out var number) || number < 0))
                            Report("metadata.sources.usage_count", count, "usage_count must be a non-negative integer.");
                        Date(map, "last_modified", "sources.last_modified");
                        UsageWindow(map, "sources.usage_window");
                    }
            }
        }
        UsageWindow(metadata, "usage_window");
        if (document.Type == "Attested Computation") CheckComputation(markdown);
    }

    private void CheckComputation(MarkdownDocument markdown)
    {
        RequiredString(metadata, "runtime", "runtime");
        OptionalString(metadata, "computation", "computation");
        if (MetadataAccess.Get(metadata, "parameters") is { } parameters)
        {
            if (parameters is not YamlSequenceNode sequence) Report("metadata.parameters", parameters, "parameters must be a list of mappings.");
            else foreach (var entry in sequence.Children)
                if (Mapping(entry, "parameters") is { } map)
                {
                    RequiredString(map, "name", "parameters.name");
                    RequiredString(map, "type", "parameters.type");
                    var required = MetadataAccess.Get(map, "required");
                    if (!MetadataAccess.TryBoolean(required, out _))
                        Report("metadata.parameters.required", required ?? map, "parameters.required must be a boolean.");
                }
        }
        foreach (var key in new[] { "executor", "attester" })
            if (MetadataAccess.Get(metadata, key) is { } node && Mapping(node, key) is { } map)
            {
                RequiredString(map, "resource", $"{key}.resource");
                if (key == "executor") Strings(map, "receipt", "executor.receipt");
            }
        // Only code in the Computation section is a candidate definition. Code
        // in an Examples section or in a footnote may describe something else.
        var blocks = new List<CodeBlock>();
        bool inSection = false;
        foreach (var block in markdown)
        {
            if (block is HeadingBlock heading && heading.Level == 1)
                inSection = Validator.HeadingText(document.Body, heading) == "Computation";
            else if (inSection && block is CodeBlock code)
                blocks.Add(code);
            else if (inSection && block is ContainerBlock container && block is not FootnoteGroup)
                blocks.AddRange(container.Descendants<CodeBlock>());
        }
        var path = MetadataAccess.Get(metadata, "computation");
        if (path != null && blocks.Count > 0)
            Validator.Add(findings, document, "metadata.computation", document.BodyOffset + blocks[0].Span.Start,
                "Use either a computation path or an inline computation, not both.", FindingKind.OptionalMetadata);
        else if (path == null && (blocks.Count != 1 || blocks[0] is not FencedCodeBlock))
        {
            const string message = "Provide a computation path or exactly one fenced code block under # Computation.";
            if (blocks.Count == 0) Report("metadata.computation", metadata, message);
            else Validator.Add(findings, document, "metadata.computation", document.BodyOffset + blocks[0].Span.Start,
                message, FindingKind.OptionalMetadata);
        }
    }

    private void UsageWindow(YamlMappingNode parent, string path)
    {
        if (MetadataAccess.Get(parent, "usage_window") is not { } node || Mapping(node, path) is not { } map) return;
        var from = Date(map, "from", $"{path}.from", required: true);
        var to = Date(map, "to", $"{path}.to", required: true);
        if (from != null && to != null && from > to) Report($"metadata.{path}", node, "usage_window.from must not be later than usage_window.to.");
    }

    // OKF v0.2 §5 requires an explicit UTC offset for every timestamp-valued key.
    private DateTimeOffset? Date(YamlMappingNode map, string key, string path, bool required = false)
    {
        var node = MetadataAccess.Get(map, key);
        if (node == null && !required) return null;
        if (node is not YamlScalarNode scalar || scalar.Value == null ||
            !Regex.IsMatch(scalar.Value, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?:Z|[+-]\d{2}(?::?\d{2})?)$", RegexOptions.IgnoreCase) ||
            !DateTimeOffset.TryParse(Regex.Replace(scalar.Value, @"([+-]\d{2})$", "$1:00"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
        {
            Report($"metadata.{path}", node ?? map, $"{path} must be an ISO 8601 datetime with an explicit UTC offset.");
            return null;
        }
        return result;
    }

    private void Actor(YamlMappingNode map, string key, string path, bool required)
    {
        var node = MetadataAccess.Get(map, key);
        if (node == null && !required) return;
        if (!MetadataAccess.IsString(node, out var value) ||
            !Regex.IsMatch(value, @"^(?:(?:human|process):\S+|[^\s/]+/[^\s/]+)$"))
            Report($"metadata.{path}", node ?? map, $"{path} must identify an actor as human:<id>, process:<id>, or <producer>/<version>.");
    }

    private void RequiredString(YamlMappingNode map, string key, string path)
    {
        var node = MetadataAccess.Get(map, key);
        if (!MetadataAccess.IsString(node, out _)) Report($"metadata.{path}", node ?? map, $"{path} must be a non-empty string.");
    }

    private void OptionalString(YamlMappingNode map, string key, string path)
    {
        var node = MetadataAccess.Get(map, key);
        if (node != null && !MetadataAccess.IsString(node, out _)) Report($"metadata.{path}", node, $"{path} must be a non-empty string.");
    }

    private void Strings(YamlMappingNode map, string key, string path)
    {
        var node = MetadataAccess.Get(map, key);
        if (node == null) return;
        if (node is not YamlSequenceNode sequence) { Report($"metadata.{path}", node, $"{path} must be a list of strings."); return; }
        foreach (var entry in sequence.Children)
            if (!MetadataAccess.IsString(entry, out _)) Report($"metadata.{path}", entry, $"{path} entries must be non-empty strings.");
    }

    private YamlMappingNode? Mapping(YamlNode node, string path)
    {
        if (node is YamlMappingNode mapping) return mapping;
        Report($"metadata.{path}", node, $"{path} must be a mapping.");
        return null;
    }

    private void Report(string rule, YamlNode? node, string message) => Validator.AddNode(findings, document, rule, node, message);
}
