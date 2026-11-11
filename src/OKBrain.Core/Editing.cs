using System.Text.Json;
using YamlDotNet.RepresentationModel;

namespace OKBrain.Core;

public sealed record TextEdit(string OldText, string NewText);
public sealed record DocumentRead(string File, string Text, IReadOnlyList<Finding> Diagnostics);

public static partial class BrainWrites
{
    public static DocumentRead Read(string bundle, string path, CancellationToken cancellationToken = default)
    {
        path = DocumentPath(path);
        var snapshot = Brain.LoadBundle(bundle, cancellationToken);
        var doc = snapshot.Documents.FirstOrDefault(d => d.Path == path) ?? throw BundleReader.Error("document.not-found", path, "Document does not exist.");
        return new(path, doc.Text, Validator.Validate(snapshot, true, cancellationToken).Findings.Where(f => f.File == path).ToArray());
    }

    private static WriteResult Edit(string bundle, string path, Func<BundleDocument?, string> change, bool create = false, CancellationToken cancellationToken = default) => Apply(bundle, snapshot =>
    {
        path = DocumentPath(path);
        var files = snapshot.Documents.ToDictionary(d => d.Path, d => d.Text, StringComparer.Ordinal);
        var doc = snapshot.Documents.FirstOrDefault(d => d.Path == path);
        if (create && (doc != null || File.Exists(Path.Combine(snapshot.RootPath, path)) || Directory.Exists(Path.Combine(snapshot.RootPath, path))))
            throw BundleReader.Error("document.exists", path, "Destination already exists.");
        if (!create && doc == null) throw BundleReader.Error("document.not-found", path, "Document does not exist.");
        var changed = change(doc);
        if (doc != null && doc.Text.StartsWith('\uFEFF') && !changed.StartsWith('\uFEFF')) changed = "\uFEFF" + changed;
        files[path] = changed;
        return files;
    }, cancellationToken: cancellationToken);

    public static WriteResult Create(string bundle, string path, string text, CancellationToken cancellationToken = default) => Edit(bundle, path, _ => text, true, cancellationToken: cancellationToken);
    public static WriteResult Replace(string bundle, string path, string text, CancellationToken cancellationToken = default) => Edit(bundle, path, _ => text, cancellationToken: cancellationToken);
    public static WriteResult Append(string bundle, string path, string text, CancellationToken cancellationToken = default) => Edit(bundle, path, d => d!.Text + text, cancellationToken: cancellationToken);
    public static WriteResult Prepend(string bundle, string path, string text, CancellationToken cancellationToken = default) => Edit(bundle, path, d => d!.Text.Insert(d.BodyOffset, text), cancellationToken: cancellationToken);
    public static WriteResult ExactEdit(string bundle, string path, string oldText, string newText, CancellationToken cancellationToken = default) => MultiEdit(bundle, path, [new(oldText, newText)], cancellationToken);
    public static WriteResult MultiEdit(string bundle, string path, IReadOnlyList<TextEdit> edits, CancellationToken cancellationToken = default) => Edit(bundle, path, d =>
    {
        var text = d!.Text;
        foreach (var edit in edits) { cancellationToken.ThrowIfCancellationRequested(); text = ReplaceOne(text, edit.OldText, edit.NewText, path); }
        return text;
    }, cancellationToken: cancellationToken);
    private static string ReplaceOne(string text, string oldText, string newText, string path)
    {
        int index = string.IsNullOrEmpty(oldText) ? -1 : text.IndexOf(oldText, StringComparison.Ordinal);
        if (index < 0 || text.IndexOf(oldText, index + 1, StringComparison.Ordinal) >= 0)
            throw BundleReader.Error("edit.match", path, "Expected exactly one nonempty match.");
        return text[..index] + newText + text[(index + oldText.Length)..];
    }
    public static WriteResult Insert(string bundle, string path, string anchor, string text, bool after = false, CancellationToken cancellationToken = default) =>
        Edit(bundle, path, d => ReplaceOne(d!.Text, anchor, after ? anchor + text : text + anchor, path), cancellationToken: cancellationToken);
    public static WriteResult LineEdit(string bundle, string path, int startLine, int endLine, string expected, string text, CancellationToken cancellationToken = default) => Edit(bundle, path, d =>
    {
        var source = d!.Text;
        var starts = new List<int> { 0 };
        for (int i = 0; i < source.Length; i++) if (source[i] == '\n' && i + 1 < source.Length) starts.Add(i + 1);
        if (startLine < 1 || endLine < startLine || endLine > starts.Count) throw BundleReader.Error("edit.lines", path, "Invalid line range.");
        int start = starts[startLine - 1], end = endLine < starts.Count ? starts[endLine] : source.Length;
        if (source[start..end] != expected) throw BundleReader.Error("edit.expected", path, "Line range does not match expected text.");
        return source[..start] + text + source[end..];
    }, cancellationToken: cancellationToken);
    public static WriteResult Copy(string bundle, string from, string to, CancellationToken cancellationToken = default) => Apply(bundle, snapshot =>
    {
        from = DocumentPath(from); to = DocumentPath(to);
        var files = snapshot.Documents.ToDictionary(d => d.Path, d => d.Text, StringComparer.Ordinal);
        if (!files.TryGetValue(from, out var text)) throw BundleReader.Error("document.not-found", from, "Document does not exist.");
        if (files.ContainsKey(to) || File.Exists(Path.Combine(snapshot.RootPath, to)) || Directory.Exists(Path.Combine(snapshot.RootPath, to))) throw BundleReader.Error("document.exists", to, "Destination already exists.");
        files[to] = text; return files;
    }, cancellationToken: cancellationToken);
    public static WriteResult FrontmatterSet(string bundle, string path, string key, string yamlValue, CancellationToken cancellationToken = default) => Frontmatter(bundle, path, key, yamlValue, cancellationToken);
    public static WriteResult FrontmatterRemove(string bundle, string path, string key, CancellationToken cancellationToken = default) => Frontmatter(bundle, path, key, null, cancellationToken);
    private static WriteResult Frontmatter(string bundle, string path, string key, string? value, CancellationToken cancellationToken) => Edit(bundle, path, d =>
    {
        if (string.IsNullOrEmpty(key) || key.Contains('\n') || key.Contains('\r')) throw BundleReader.Error("frontmatter.key", path, "A nonempty metadata key is required.");
        string yamlKey = System.Text.RegularExpressions.Regex.IsMatch(key, @"^[A-Za-z_][A-Za-z0-9_-]*$") ? key : JsonSerializer.Serialize(key);
        if (value != null)
        {
            try
            {
                var stream = new YamlStream();
                stream.Load(new StringReader(yamlKey + ": " + value));
                if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode mapping || mapping.Children.Count != 1)
                    throw BundleReader.Error("frontmatter.value", path, "Value must describe a single YAML value.");
            }
            catch (YamlDotNet.Core.YamlException ex) { throw BundleReader.Error("frontmatter.value", path, "Invalid YAML value: " + ex.Message); }
        }
        var text = d!.Text;
        if (d.BodyOffset == 0 || (d.BodyOffset == 1 && text.StartsWith('\uFEFF'))) {
            if (value == null) throw BundleReader.Error("frontmatter.key", path, "Metadata key does not exist.");
            var bom = text.StartsWith('\uFEFF') ? "\uFEFF" : "";
            return bom + "---\n" + yamlKey + ": " + value + "\n---\n" + text[bom.Length..];
        }
        if (d.Metadata == null) throw BundleReader.Error("frontmatter.yaml", path, "Frontmatter must be a valid mapping.");
        var node = d.Metadata.Children.FirstOrDefault(p => p.Key is YamlDotNet.RepresentationModel.YamlScalarNode s && s.Value == key);
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        int close = text.LastIndexOf("---", d.BodyOffset - 1, d.BodyOffset, StringComparison.Ordinal);
        if (node.Key == null) {
            if (value == null) throw BundleReader.Error("frontmatter.key", path, "Metadata key does not exist.");
            return text.Insert(close, yamlKey + ": " + value + newline);
        }
        // YAML marks are relative to the YAML block. Replace whole entry, leaving other entries untouched.
        int yamlStart = text.IndexOf('\n') + 1;
        int start = yamlStart + (int)(value == null ? node.Key.Start.Index : node.Value.Start.Index);
        int end = yamlStart + (int)node.Value.End.Index;
        if (value == null)
        {
            while (end < close && text[end] != '\n') end++;
            if (end < close) end++;
        }
        return text[..start] + (value ?? "") + text[end..];
    }, cancellationToken: cancellationToken);

    public static WriteResult MakeDirectory(string bundle, string path, CancellationToken cancellationToken = default) => DirectoryChange(bundle, path, true, cancellationToken);
    public static WriteResult RemoveDirectory(string bundle, string path, CancellationToken cancellationToken = default) => DirectoryChange(bundle, path, false, cancellationToken);
    private static WriteResult DirectoryChange(string bundle, string path, bool create, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains('\\') || path.Split('/').Any(p => p is "" or "." or ".." or ".git")) throw BundleReader.Error("directory.path", path ?? "", "A bundle-relative directory path is required.");
        var root = BundleReader.PhysicalPath(bundle);
        lock (Locks.GetOrAdd(root, _ => new object()))
        {
            using var rootHandle = UseNativeCommit ? OpenDirectory(root) : null;
            var commitRoot = rootHandle == null ? root : HandlePath(rootHandle);
            var gate = Apply(root, s => s.Documents.ToDictionary(d => d.Path, d => d.Text, StringComparer.Ordinal), cancellationToken: cancellationToken);
            if (!gate.Success) return gate;
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (rootHandle != null) ChangeDirectoryNative(commitRoot, rootHandle, path, create);
                else
                {
                    VerifyPortablePath(root, path, create);
                    BeforeDirectoryChange?.Invoke(path);
                    VerifyPortablePath(root, path, false);
                    var full = Path.Combine(root, path);
                    if (create)
                    {
                        if (Directory.Exists(full) || File.Exists(full)) throw new IOException("Destination already exists.");
                        Directory.CreateDirectory(full);
                    }
                    else Directory.Delete(full);
                }
                return new(true, [path], [], null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(false, [], [], ex.Message); }
        }
    }
}
