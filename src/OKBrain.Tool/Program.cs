using System.Text.Json;
using System.Text.Json.Serialization;
using OKBrain.Core;

namespace OKBrain.Tool;

public static class Program
{
    private const string Usage = """
        Usage:
          okbrain validate <bundle> [--strict]
          okbrain search <bundle> [--query <text>]
          okbrain stats <bundle>
          okbrain reindex <bundle>
          okbrain graph <bundle> [--doc <doc>] [--depth <n>] [--relation <name>] [--findings]
          okbrain delete <bundle> <doc>
          okbrain move <bundle> <from> <to>
          okbrain create <bundle> [--no-git]
          okbrain serve <address> [--bundle <path>]

        Bundle paths are required. Results are JSON; --help prints this usage.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static int Main(string[] args)
    {
        try
        {
            var command = Parse(args);
            if (command == null)
            {
                Console.Out.WriteLine(Usage);
                return 0;
            }
            switch (command.Name)
            {
                case "serve":
                    return BrainServer.Run(command.Bundle, command.Query);
                case "create":
                    Write(Console.Out, BundleCreator.Create(command.Bundle, git: !command.Strict)); // for create, the boolean flag slot carries --no-git
                    return 0;
                case "validate":
                    var validation = Brain.Validate(command.Bundle, command.Strict);
                    Write(Console.Out, validation);
                    return validation.Valid ? 0 : 1;
                case "search":
                    Write(Console.Out, Brain.Search(command.Bundle, command.Query));
                    return 0;
                case "stats":
                    Write(Console.Out, Brain.Stats(command.Bundle));
                    return 0;
                case "graph":
                    if (command.Findings) Write(Console.Out, BrainGraph.Findings(command.Bundle));
                    else if (command.Doc != null) Write(Console.Out, new { backlinks = BrainGraph.Backlinks(command.Bundle, command.Doc), neighbors = BrainGraph.Neighbors(command.Bundle, command.Doc, command.Depth, relation: command.Relation) });
                    else Write(Console.Out, new { graph = BrainGraph.Export(command.Bundle), relations = BrainGraph.Relations(command.Bundle) });
                    return 0;
                case "reindex":
                    var result = BrainWrites.Reindex(command.Bundle);
                    Write(Console.Out, result);
                    return result.Success ? 0 : 1;
                case "delete":
                case "move":
                    var change = command.Name == "delete" ? BrainWrites.Delete(command.Bundle, command.Paths[0]) : BrainWrites.Move(command.Bundle, command.Paths[0], command.Paths[1]);
                    Write(Console.Out, change);
                    return change.Success ? 0 : 1;
                default: throw new InvalidOperationException();
            }
        }
        catch (InputException ex)
        {
            Write(Console.Error, new { rule = "input.arguments", message = ex.Message });
            return 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Write(Console.Error, new { rule = "server.start", message = ex.Message });
            return 1;
        }
        catch (BundleException ex)
        {
            Write(Console.Error, ex.Finding);
            return 1;
        }
    }

    private static Command? Parse(string[] args)
    {
        if (args.Length == 0) throw new InputException("A command and bundle path are required. Use --help for usage.");
        string name = args[0];
        if (name == "help" || IsHelp(name)) return null;
        if (name == "serve")
        {
            if (args.Skip(1).Any(IsHelp)) return null;
            if (args.Length != 2 && args.Length != 4) throw new InputException("serve requires an address and optional --bundle path.");
            if (args[1].StartsWith('-') || (args.Length == 4 && (args[2] != "--bundle" || string.IsNullOrWhiteSpace(args[3])))) throw new InputException("serve requires an address and optional --bundle path.");
            if (!BrainServer.IsValidAddress(args[1])) throw new InputException("Address must be an HTTP listener address with an explicit port and no path.");
            return new(name, args[1], false, args.Length == 4 ? args[3] : null, [], null, 1, null, false);
        }
        if (name is not ("create" or "validate" or "search" or "stats" or "reindex" or "graph" or "delete" or "move"))
        {
            if (args.Length == 2 && IsHelp(args[1])) return null;
            throw new InputException($"Unknown command '{name}'. Use --help for usage.");
        }
        string? bundle = null, query = null;
        bool strict = false, querySeen = false, endOptions = false;
        string? doc = null, relation = null;
        int depth = 1;
        bool depthSeen = false, findings = false;
        var paths = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if (!endOptions && arg == "--") { endOptions = true; continue; }
            if (!endOptions && IsHelp(arg)) return null;
            if (!endOptions && name == "graph" && arg == "--findings")
            {
                if (findings) throw new InputException("--findings may only be supplied once.");
                findings = true;
            }
            else if (!endOptions && name == "graph" && (arg == "--doc" || arg == "--depth" || arg == "--relation"))
            {
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new InputException($"{arg} requires a value.");
                var value = args[i];
                if (arg == "--doc") { if (doc != null || string.IsNullOrWhiteSpace(value)) throw new InputException("--doc requires one document."); doc = value; }
                else if (arg == "--relation") { if (relation != null) throw new InputException("--relation may only be supplied once."); relation = value; }
                else { if (depthSeen || !int.TryParse(value, out depth) || depth < 0) throw new InputException("--depth requires one nonnegative integer."); depthSeen = true; }
            }
            else if (!endOptions && ((arg == "--strict" && name == "validate") || (arg == "--no-git" && name == "create")))
            {
                if (strict) throw new InputException($"{arg} may only be supplied once.");
                strict = true;
            }
            else if (!endOptions && arg.StartsWith("--query=", StringComparison.Ordinal) && name == "search")
            {
                if (querySeen) throw new InputException("--query may only be supplied once.");
                querySeen = true;
                query = arg["--query=".Length..];
            }
            else if (!endOptions && arg == "--query" && name == "search")
            {
                if (querySeen || i + 1 >= args.Length)
                    throw new InputException("--query requires one text value and may only be supplied once.");
                querySeen = true;
                query = args[++i];
            }
            else if (!endOptions && arg.StartsWith('-')) throw new InputException($"Unknown option '{arg}'.");
            else if (bundle == null && !string.IsNullOrWhiteSpace(arg)) bundle = arg;
            else if (name is "delete" or "move") paths.Add(arg);
            else throw new InputException("Exactly one bundle path is required.");
        }
        if (bundle == null) throw new InputException("A bundle path is required.");
        if (paths.Count != (name == "delete" ? 1 : name == "move" ? 2 : 0))
            throw new InputException("The required document paths must be supplied.");
        if (name == "graph" && ((doc == null && (depthSeen || relation != null)) || (findings && (doc != null || depthSeen || relation != null))))
            throw new InputException("--depth and --relation require --doc; --findings cannot be combined with document options.");
        return new(name, bundle, strict, query, paths, doc, depth, relation, findings);
    }

    private static bool IsHelp(string arg) => arg is "--help" or "-h";

    private static void Write(TextWriter output, object value) => output.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
    private sealed record Command(string Name, string Bundle, bool Strict, string? Query, List<string> Paths, string? Doc, int Depth, string? Relation, bool Findings);
    private sealed class InputException(string message) : Exception(message);
}
