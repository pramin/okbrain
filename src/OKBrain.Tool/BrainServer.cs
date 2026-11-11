using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OKBrain.Core;

namespace OKBrain.Tool;

public static class BrainServer
{
    public static int Run(string address, string? bundle)
    {
        if (!IsValidAddress(address))
            throw new ArgumentException("Address must be an HTTP listener address with an explicit port and no path.");
        bundle ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".brain");
        Brain.LoadBundle(bundle);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithHttpTransport().WithTools(new BrainTools(bundle));
        using var app = builder.Build();
        app.MapMcp();
        app.Run(address);
        return 0;
    }

    internal static bool IsValidAddress(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == "http" && System.Text.RegularExpressions.Regex.IsMatch(address, @"://(?:\[[^]]+\]|[^/:]+):[0-9]+(?:/|$)") && uri.Port > 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0;
}

[McpServerToolType]
public sealed class BrainTools(string bundle)
{
    private sealed class SessionState { public volatile string? Project; }
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SessionState> Sessions = new();

    private static string? ProjectOf(McpServer? server) => server?.SessionId is { } id && Sessions.TryGetValue(id, out var s) ? s.Project : null;

    private string Target(McpServer server, string? scope, bool write)
    {
        var project = ProjectOf(server);
        switch (scope)
        {
            case null when project == null: return bundle;
            case null when write: throw new ArgumentException("A project brain is set for this session: pass scope \"project\" or \"common\" for writes.");
            case null: return project!;
            case "common": return bundle;
            case "project": return project ?? throw new ArgumentException("No project brain is set for this session; call set_project first.");
            default: throw new ArgumentException("scope must be \"project\" or \"common\".");
        }
    }

    [McpServerTool, Description("Set this session's project brain. Accepts a project directory containing .brain/, or a bundle directory. Reads and searches then check the project brain first, then the common brain; writes require scope \"project\" or \"common\".")]
    public CallToolResult SetProject(McpServer server, string path, CancellationToken cancellationToken = default) => Invoke(() =>
    {
        var full = Path.GetFullPath(path);
        var candidate = Directory.Exists(Path.Combine(full, ".brain")) ? Path.Combine(full, ".brain") : full;
        if (!Directory.Exists(candidate)) throw new ArgumentException("No bundle directory found at " + full + ".");
        Brain.LoadBundle(candidate, cancellationToken);
        if (string.Equals(Path.GetFullPath(candidate).TrimEnd('/'), Path.GetFullPath(bundle).TrimEnd('/'), StringComparison.Ordinal))
            throw new ArgumentException("The project brain is the common brain; nothing to set.");
        Sessions.GetOrAdd(server.SessionId ?? throw new ArgumentException("This connection has no session; set_project needs a stateful session."), _ => new()).Project = candidate;
        return new { project = candidate, common = bundle };
    });

    [McpServerTool, Description("Report this session's project brain (null when unset) and the common brain.")]
    public CallToolResult GetProject(McpServer server) => Invoke(() => new { project = ProjectOf(server), common = bundle });

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    private static CallToolResult Invoke(Func<object> action)
    {
        try
        {
            var result = action();
            return Result(result, (result is WriteResult w && !w.Success) || (result is ValidationResult v && !v.Valid));
        }
        catch (BundleException ex) { return Result(ex.Finding, true); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return Result(new { rule = "operation.error", message = ex.Message }, true); }
    }
    private static CallToolResult Result(object value, bool error) => new() { IsError = error, Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, Json) }] };

    [McpServerTool, Description("Validate the bundle.")] public CallToolResult Validate(McpServer server, bool strict = false, string? scope = null, CancellationToken cancellationToken = default) => Invoke(() => Brain.Validate(Target(server, scope, false), strict, cancellationToken));
    [McpServerTool, Description("Search current documents. With a project brain set, searches both (project first); each hit carries its source.")] public CallToolResult Search(McpServer server, string? query = null, string? scope = null, CancellationToken cancellationToken = default) => Invoke(() =>
    {
        var project = ProjectOf(server);
        var sources = scope != null || project == null ? [(scope ?? "common", Target(server, scope, false))] : new[] { ("project", project), ("common", bundle) };
        return sources.SelectMany(x => Brain.Search(x.Item2, query, cancellationToken).Select(h => new { source = x.Item1, h.File, h.Kind, h.Type, h.Title, h.Line, h.Column, h.Snippet })).ToArray();
    });
    [McpServerTool, Description("Summarize the bundle.")] public CallToolResult Stats(McpServer server, string? scope = null, CancellationToken cancellationToken = default) => Invoke(() => Brain.Stats(Target(server, scope, false), cancellationToken));
    [McpServerTool, Description("Regenerate index listings.")] public CallToolResult Reindex(McpServer server, string? scope = null, CancellationToken cancellationToken = default) => Invoke(() => BrainWrites.Reindex(Target(server, scope, true), cancellationToken));
    [McpServerTool, Description("Get inbound links to a document.")] public CallToolResult Backlinks(McpServer server, string path, string? scope = null, CancellationToken cancellationToken = default) => Invoke(() => BrainGraph.Backlinks(Target(server, scope, false), path, cancellationToken));
    [McpServerTool, Description("Get graph neighbors within a number of hops; direction is in, out, or both.")] public CallToolResult Neighbors(McpServer server, string path, int depth = 1, string direction = "both", string? relation = null, string? scope = null, CancellationToken cancellationToken = default) => Invoke(() => BrainGraph.Neighbors(Target(server, scope, false), path, depth, direction, relation, cancellationToken));
    [McpServerTool, Description("Find orphans, dead ends, and broken links.")] public CallToolResult Findings(McpServer server, string? scope = null, CancellationToken cancellationToken = default) => Invoke(() => BrainGraph.Findings(Target(server, scope, false), cancellationToken));
    [McpServerTool, Description("Export graph nodes and edges.")] public CallToolResult Export(McpServer server, string? scope = null, CancellationToken cancellationToken = default) => Invoke(() => BrainGraph.Export(Target(server, scope, false), cancellationToken));
    [McpServerTool, Description("Count graph relations.")] public CallToolResult Relations(McpServer server, string? scope = null, CancellationToken cancellationToken = default) => Invoke(() => BrainGraph.Relations(Target(server, scope, false), cancellationToken));
    [McpServerTool, Description("Read a Markdown document and its diagnostics. With a project brain set and no scope, checks the project brain first, then the common brain; the result names its source.")] public CallToolResult Read(McpServer server, string path, string? scope = null, CancellationToken cancellationToken = default) => Invoke(() =>
    {
        var project = ProjectOf(server);
        if (scope == null && project != null)
        {
            try { var r = BrainWrites.Read(project, path, cancellationToken); return (object)new { source = "project", r.File, r.Text, r.Diagnostics }; }
            catch (BundleException ex) when (ex.Finding.Rule == "document.not-found") { }
            var c = BrainWrites.Read(bundle, path, cancellationToken);
            return new { source = "common", c.File, c.Text, c.Diagnostics };
        }
        var d = BrainWrites.Read(Target(server, scope, false), path, cancellationToken);
        return new { source = scope ?? "common", d.File, d.Text, d.Diagnostics };
    });
    [McpServerTool, Description("Create a Markdown document.")] public CallToolResult Create(McpServer server, string path, string text, string? scope = null, CancellationToken cancellationToken = default) => Invoke(() => BrainWrites.Create(Target(server, scope, true), path, text, cancellationToken));
    [McpServerTool, Description("Replace a document.")] public CallToolResult Replace(McpServer server, string path, string text, string? scope = null, CancellationToken cancellationToken = default) => Invoke(() => BrainWrites.Replace(Target(server, scope, true), path, text, cancellationToken));
    [McpServerTool, Description("Replace exactly one text match.")] public CallToolResult ExactEdit(McpServer server, string path, string oldText, string newText, string? scope = null, CancellationToken cancellationToken = default) => Invoke(() => BrainWrites.ExactEdit(Target(server, scope, true), path, oldText, newText, cancellationToken));
    [McpServerTool, Description("Delete a document and unlink inbound links.")] public CallToolResult Delete(McpServer server, string path, string? scope = null, CancellationToken cancellationToken = default) => Invoke(() => BrainWrites.Delete(Target(server, scope, true), path, cancellationToken));
    [McpServerTool, Description("Move a document and adjust links.")] public CallToolResult Move(McpServer server, string from, string to, string? scope = null, CancellationToken cancellationToken = default) => Invoke(() => BrainWrites.Move(Target(server, scope, true), from, to, cancellationToken));
    [McpServerTool, Description("Append text to a document.")] public CallToolResult Append(McpServer server, string path, string text, string? scope = null, CancellationToken cancellationToken = default) => Invoke(() => BrainWrites.Append(Target(server, scope, true), path, text, cancellationToken));
    [McpServerTool, Description("Prepend text after frontmatter.")] public CallToolResult Prepend(McpServer server, string path, string text, string? scope = null, CancellationToken cancellationToken = default) => Invoke(() => BrainWrites.Prepend(Target(server, scope, true), path, text, cancellationToken));
    [McpServerTool, Description("Insert text before or after a unique anchor.")] public CallToolResult Insert(McpServer server, string path, string anchor, string text, bool after = false, string? scope = null, CancellationToken cancellationToken = default) => Invoke(() => BrainWrites.Insert(Target(server, scope, true), path, anchor, text, after, cancellationToken));
    [McpServerTool, Description("Replace an inclusive line range matching expected text, including line endings.")] public CallToolResult LineEdit(McpServer server, string path, int startLine, int endLine, string expected, string text, string? scope = null, CancellationToken cancellationToken = default) => Invoke(() => BrainWrites.LineEdit(Target(server, scope, true), path, startLine, endLine, expected, text, cancellationToken));
    [McpServerTool, Description("Apply ordered exact-text edits atomically.")] public CallToolResult MultiEdit(McpServer server, string path, TextEdit[] edits, string? scope = null, CancellationToken cancellationToken = default) => Invoke(() => BrainWrites.MultiEdit(Target(server, scope, true), path, edits, cancellationToken));
    [McpServerTool, Description("Set a frontmatter key to a YAML value.")] public CallToolResult FrontmatterSet(McpServer server, string path, string key, string yamlValue, string? scope = null, CancellationToken cancellationToken = default) => Invoke(() => BrainWrites.FrontmatterSet(Target(server, scope, true), path, key, yamlValue, cancellationToken));
    [McpServerTool, Description("Remove a frontmatter key.")] public CallToolResult FrontmatterRemove(McpServer server, string path, string key, string? scope = null, CancellationToken cancellationToken = default) => Invoke(() => BrainWrites.FrontmatterRemove(Target(server, scope, true), path, key, cancellationToken));
    [McpServerTool, Description("Copy a document to an absent destination.")] public CallToolResult Copy(McpServer server, string from, string to, string? scope = null, CancellationToken cancellationToken = default) => Invoke(() => BrainWrites.Copy(Target(server, scope, true), from, to, cancellationToken));
    [McpServerTool, Description("Create a directory.")] public CallToolResult MakeDirectory(McpServer server, string path, string? scope = null, CancellationToken cancellationToken = default) => Invoke(() => BrainWrites.MakeDirectory(Target(server, scope, true), path, cancellationToken));
    [McpServerTool, Description("Remove an empty directory.")] public CallToolResult RemoveDirectory(McpServer server, string path, string? scope = null, CancellationToken cancellationToken = default) => Invoke(() => BrainWrites.RemoveDirectory(Target(server, scope, true), path, cancellationToken));
}
