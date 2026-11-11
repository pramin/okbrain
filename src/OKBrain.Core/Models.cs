using YamlDotNet.RepresentationModel;

namespace OKBrain.Core;

public enum DocumentKind { Concept, Index, Log }
public enum FindingKind { Conformance, OptionalMetadata, BrokenLink }

/// <summary>Line and column are one-based. File is relative to the bundle root.</summary>
public sealed record Finding(string Rule, string File, int Line, int Column, string Message, FindingKind Kind)
{
    internal string? Subject { get; init; }
}

public sealed record ValidationResult(bool Valid, bool Strict, IReadOnlyList<Finding> Findings);
public sealed record SearchHit(string File, DocumentKind Kind, string? Type, string? Title, int? Line, int? Column, string? Snippet);
public sealed record BundleStats(int Documents, int Concepts, int Indexes, int Logs,
    IReadOnlyDictionary<string, int> Types, IReadOnlyDictionary<string, int> Tags);

/// <summary>A snapshot of a Markdown document, including producer-defined metadata.</summary>
public sealed class BundleDocument
{
    internal BundleDocument(string path, string text, DocumentKind kind, YamlMappingNode? metadata,
        int bodyOffset, IReadOnlyList<Finding> parseFindings)
    {
        Path = path;
        Text = text;
        Kind = kind;
        Metadata = metadata;
        BodyOffset = bodyOffset;
        ParseFindings = parseFindings;
        Concept = kind == DocumentKind.Concept && metadata != null ? ConceptMetadata.From(metadata) : null;
    }

    public string Path { get; }
    public string Text { get; }
    public DocumentKind Kind { get; }
    public YamlMappingNode? Metadata { get; }
    public ConceptMetadata? Concept { get; }
    public string Body => Text[BodyOffset..];
    public string? Type => MetadataAccess.StringValue(Metadata, "type");
    public string? Title => MetadataAccess.StringValue(Metadata, "title");
    internal int BodyOffset { get; }
    internal IReadOnlyList<Finding> ParseFindings { get; }
}

/// <summary>A single filesystem snapshot. Obtain another snapshot to see external edits.</summary>
public sealed record BundleSnapshot(string RootPath, IReadOnlyList<BundleDocument> Documents);

public sealed class BundleException : Exception
{
    public BundleException(Finding finding, Exception? inner = null) : base(finding.Message, inner) => Finding = finding;
    public Finding Finding { get; }
}
