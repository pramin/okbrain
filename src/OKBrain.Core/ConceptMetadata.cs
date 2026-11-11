using System.Numerics;
using YamlDotNet.RepresentationModel;

namespace OKBrain.Core;

// Invalid or absent optional values are represented as null/empty here; validation
// reports the distinction, and BundleDocument.Metadata retains all original nodes.
public sealed record ActorEvent(string? By, string? At);
public sealed record UsageWindow(string? From, string? To);
public sealed record Source(string? Id, string? Resource, string? Title, string? Author,
    BigInteger? UsageCount, string? LastModified, UsageWindow? UsageWindow);
public sealed record ComputationParameter(string? Name, string? Type, bool? Required);
public sealed record Executor(string? Resource, IReadOnlyList<string> Receipt);
public sealed record Attester(string? Resource);

/// <summary>Recognized OKF v0.2 concept fields. Datetimes retain their original ISO 8601 text.</summary>
public sealed record ConceptMetadata(
    string? Type, string? Title, string? Description, string? Resource, IReadOnlyList<string> Tags,
    ActorEvent? Generated, IReadOnlyList<ActorEvent> Verified, IReadOnlyList<Source> Sources,
    UsageWindow? UsageWindow, string? Status, string? StaleAfter, string? Runtime,
    IReadOnlyList<ComputationParameter> Parameters, string? Computation, Executor? Executor, Attester? Attester)
{
    internal static ConceptMetadata From(YamlMappingNode map)
    {
        var generated = Mapping(map, "generated");
        var verifiedNode = MetadataAccess.Get(map, "verified");
        IEnumerable<YamlMappingNode> verified = verifiedNode switch
        {
            YamlMappingNode single => [single],
            YamlSequenceNode many => many.Children.OfType<YamlMappingNode>(),
            _ => []
        };
        var executor = Mapping(map, "executor");
        var attester = Mapping(map, "attester");
        return new(
            Text(map, "type"), Text(map, "title"), Text(map, "description"), Text(map, "resource"), Strings(map, "tags"),
            generated is null ? null : Event(generated), verified.Select(Event).ToArray(),
            Mappings(map, "sources").Select(source => new Source(Text(source, "id"), Text(source, "resource"),
                Text(source, "title"), Text(source, "author"), Integer(source, "usage_count"), Scalar(source, "last_modified"),
                Window(source))).ToArray(),
            Window(map), Text(map, "status"), Scalar(map, "stale_after"), Text(map, "runtime"),
            Mappings(map, "parameters").Select(parameter => new ComputationParameter(Text(parameter, "name"),
                Text(parameter, "type"), Boolean(parameter, "required"))).ToArray(),
            Text(map, "computation"),
            executor is null ? null : new Executor(Text(executor, "resource"), Strings(executor, "receipt")),
            attester is null ? null : new Attester(Text(attester, "resource")));
    }

    private static string? Text(YamlMappingNode map, string key) => MetadataAccess.StringValue(map, key);
    private static string? Scalar(YamlMappingNode map, string key) => (MetadataAccess.Get(map, key) as YamlScalarNode)?.Value;
    private static YamlMappingNode? Mapping(YamlMappingNode map, string key) => MetadataAccess.Get(map, key) as YamlMappingNode;
    private static IEnumerable<YamlMappingNode> Mappings(YamlMappingNode map, string key) =>
        MetadataAccess.Get(map, key) is YamlSequenceNode sequence ? sequence.Children.OfType<YamlMappingNode>() : [];
    private static IReadOnlyList<string> Strings(YamlMappingNode map, string key) =>
        MetadataAccess.Get(map, key) is YamlSequenceNode sequence
            ? sequence.Children.Select(node => MetadataAccess.IsString(node, out var value) ? value : null)
                .Where(value => value != null).Select(value => value!).ToArray()
            : [];
    private static ActorEvent Event(YamlMappingNode map) => new(Text(map, "by"), Scalar(map, "at"));
    private static UsageWindow? Window(YamlMappingNode map) => Mapping(map, "usage_window") is { } window
        ? new(Scalar(window, "from"), Scalar(window, "to")) : null;
    private static BigInteger? Integer(YamlMappingNode map, string key) =>
        MetadataAccess.TryInteger(MetadataAccess.Get(map, key), out var number) && number >= 0 ? number : null;
    private static bool? Boolean(YamlMappingNode map, string key) =>
        MetadataAccess.TryBoolean(MetadataAccess.Get(map, key), out var value) ? value : null;
}
