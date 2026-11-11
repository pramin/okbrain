using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace OKBrain.Core;

internal static class BundleReader
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static BundleSnapshot Load(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path))
            throw Error("bundle.path", path ?? "", "A bundle path is required.");
        string root;
        try { root = System.IO.Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw Error("bundle.path", path, $"Invalid bundle path: {ex.Message}", ex); }
        if (!Directory.Exists(root))
            throw Error("bundle.not-found", path, "Bundle directory does not exist or is not accessible.");

        var documents = new List<BundleDocument>();
        try
        {
            var physicalRoot = PhysicalPath(root);
            var pending = new Stack<(string Directory, string Physical, HashSet<string> Parents)>();
            pending.Push((root, physicalRoot, new HashSet<string>(PathComparer) { physicalRoot }));
            while (pending.TryPop(out var current))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(current.Directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = System.IO.Path.GetFileName(entry);
                    if (name == ".git") continue;
                    var attributes = File.GetAttributes(entry);
                    var isDirectory = (attributes & FileAttributes.Directory) != 0;
                    if (!isDirectory && System.IO.Path.GetExtension(entry) != ".md") continue;
                    var relative = System.IO.Path.GetRelativePath(root, entry).Replace(System.IO.Path.DirectorySeparatorChar, '/');
                    string physical;
                    try
                    {
                        physical = (attributes & FileAttributes.ReparsePoint) != 0
                            ? PhysicalPath(entry) : System.IO.Path.Combine(current.Physical, name);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { throw Error("bundle.link", relative, $"Could not resolve symbolic link: {ex.Message}", ex); }
                    if (!IsWithin(physicalRoot, physical))
                        throw Error("bundle.path-escape", relative, "A symbolic link resolves outside the bundle.");
                    if (isDirectory)
                    {
                        if (current.Parents.Contains(physical))
                            throw Error("bundle.link-cycle", relative, "A symbolic link creates a directory cycle.");
                        var parents = new HashSet<string>(current.Parents, PathComparer) { physical };
                        pending.Push((entry, physical, parents));
                    }
                    else documents.Add(ReadDocument(entry, relative));
                }
            }
        }
        catch (BundleException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw Error("bundle.read", root, $"Could not read the bundle: {ex.Message}", ex); }
        documents.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        return new(root, documents);
    }

    private static BundleDocument ReadDocument(string fullPath, string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(fullPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw Error("document.read", path, $"Could not read document: {ex.Message}", ex); }
        var findings = new List<Finding>();
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException)
        {
            text = Encoding.UTF8.GetString(bytes);
            findings.Add(new("document.utf8", path, 1, 1, "Document is not valid UTF-8.", FindingKind.Conformance));
        }
        return ParseDocument(path, text, findings);
    }

    internal static BundleDocument ParseDocument(string path, string text, List<Finding>? findings = null)
    {
        findings ??= new List<Finding>();
        var name = System.IO.Path.GetFileName(path);
        var kind = name switch { "index.md" => DocumentKind.Index, "log.md" => DocumentKind.Log, _ => DocumentKind.Concept };
        var (metadata, bodyOffset) = ReadFrontmatter(text, path, kind, findings);
        return new(path, text, kind, metadata, bodyOffset, findings);
    }

    private static (YamlMappingNode?, int) ReadFrontmatter(string text, string path, DocumentKind kind, List<Finding> findings)
    {
        int start = text.StartsWith('\uFEFF') ? 1 : 0;
        var firstEnd = LineEnd(text, start);
        if (text[start..firstEnd] != "---")
        {
            if (kind == DocumentKind.Concept)
                findings.Add(new("frontmatter.required", path, 1, 1, "Concept must begin with a YAML frontmatter block delimited by ---.", FindingKind.Conformance));
            return (null, start);
        }

        if (kind == DocumentKind.Log || (kind == DocumentKind.Index && path != "index.md"))
            findings.Add(new("frontmatter.reserved", path, 1, 1, "Frontmatter is permitted only in the root index.md and concept documents.", FindingKind.Conformance));
        var yamlStart = NextLine(text, firstEnd);
        var lineStart = yamlStart;
        while (lineStart < text.Length)
        {
            var end = LineEnd(text, lineStart);
            if (text[lineStart..end] == "---")
            {
                var bodyOffset = NextLine(text, end);
                try
                {
                    var stream = new YamlStream();
                    stream.Load(new StringReader(text[yamlStart..lineStart]));
                    if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode mapping)
                    {
                        findings.Add(new("frontmatter.mapping", path, 2, 1, "Frontmatter must be a YAML mapping.", FindingKind.Conformance));
                        return (null, bodyOffset);
                    }
                    return (mapping, bodyOffset);
                }
                catch (YamlException ex)
                {
                    findings.Add(new("frontmatter.yaml", path, (int)Math.Clamp(ex.Start.Line + 1, 2, int.MaxValue), (int)Math.Clamp(ex.Start.Column, 1, int.MaxValue),
                        $"Invalid YAML frontmatter: {ex.Message}", FindingKind.Conformance));
                    return (null, bodyOffset);
                }
                catch (ArgumentException ex)
                {
                    findings.Add(new("frontmatter.yaml", path, 2, 1, $"Invalid YAML frontmatter: {ex.Message}", FindingKind.Conformance));
                    return (null, bodyOffset);
                }
            }
            lineStart = NextLine(text, end);
        }
        findings.Add(new("frontmatter.unclosed", path, 1, 1, "Frontmatter is missing its closing --- delimiter.", FindingKind.Conformance));
        return (null, text.Length);
    }

    private static int LineEnd(string text, int start)
    {
        var end = start;
        while (end < text.Length && text[end] != '\r' && text[end] != '\n') end++;
        return end;
    }

    private static int NextLine(string text, int end)
    {
        if (end < text.Length && text[end] == '\r') end++;
        if (end < text.Length && text[end] == '\n') end++;
        return end;
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static bool IsWithin(string root, string candidate)
    {
        var relative = System.IO.Path.GetRelativePath(root, candidate);
        return relative != ".." && !relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !System.IO.Path.IsPathRooted(relative);
    }

    // Resolve every component, including symlinks in the parent of a link target.
    internal static string PhysicalPath(string path, int depth = 0)
    {
        if (depth > 40) throw new IOException("Too many symbolic links while resolving a bundle path.");
        path = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetPathRoot(path)!;
        var current = root;
        foreach (var part in path[root.Length..].Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var next = System.IO.Path.Combine(current, part);
            var attributes = File.GetAttributes(next);
            if ((attributes & FileAttributes.ReparsePoint) == 0) current = next;
            else
            {
                FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(next) : new FileInfo(next);
                var target = info.ResolveLinkTarget(true) ?? throw new IOException($"Cannot resolve symbolic link '{next}'.");
                current = PhysicalPath(target.FullName, depth + 1);
            }
        }
        return current;
    }

    internal static BundleException Error(string rule, string file, string message, Exception? inner = null) =>
        new(new(rule, file, 1, 1, message, FindingKind.Conformance), inner);
}
