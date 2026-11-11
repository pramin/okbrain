using System.Collections.Concurrent;
using System.Text;

namespace OKBrain.Core;

public sealed record WriteResult(bool Success, IReadOnlyList<string> TouchedFiles,
    IReadOnlyList<Finding> NewFindings, string? Error = null);

public static partial class BrainWrites
{
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.Ordinal);
    internal static Action<string>? BeforeCommitFile;
    internal static Action<string>? BeforeDirectoryChange;
    internal static bool ForcePortableCommit;
    private const string Begin = "<!-- okbrain:generated -->";
    private const string End = "<!-- /okbrain:generated -->";

    public static WriteResult Reindex(string bundlePath, CancellationToken cancellationToken = default) => Apply(bundlePath, snapshot => Reindexed(snapshot, cancellationToken: cancellationToken), cancellationToken: cancellationToken);

    private static Dictionary<string, string> Reindexed(BundleSnapshot snapshot, HashSet<string>? affected = null, CancellationToken cancellationToken = default)
    {
        var files = snapshot.Documents.ToDictionary(d => d.Path, d => d.Text, StringComparer.Ordinal);
        var dirs = new HashSet<string>(StringComparer.Ordinal) { "" };
        foreach (var path in files.Keys.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dir = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
            while (dirs.Add(dir) && dir.Length > 0)
                dir = Path.GetDirectoryName(dir)?.Replace('\\', '/') ?? "";
        }
        foreach (var dir in dirs.Where(d => affected == null || affected.Contains(d)).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = dir.Length == 0 ? "index.md" : dir + "/index.md";
            var prefix = dir.Length == 0 ? "" : dir + "/";
            var entries = snapshot.Documents.Where(d => d.Path.StartsWith(prefix, StringComparison.Ordinal)
                && !d.Path[prefix.Length..].Contains('/') && d.Kind == DocumentKind.Concept)
                .OrderBy(d => d.Path, StringComparer.Ordinal)
                .Select(d => $"- [{Escape(d.Title ?? Path.GetFileNameWithoutExtension(d.Path))}]({Uri.EscapeDataString(Path.GetFileName(d.Path))})");
            var children = dirs.Where(d => d.StartsWith(prefix, StringComparison.Ordinal) && d.Length > prefix.Length
                && !d[prefix.Length..].Contains('/')).Order(StringComparer.Ordinal)
                .Select(d => $"- [{Escape(d[prefix.Length..])}]({Uri.EscapeDataString(d[prefix.Length..])}/index.md)");
            var generated = Begin + "\n## Contents\n\n" + string.Join("\n", entries.Concat(children)) + "\n" + End;
            if (!files.TryGetValue(index, out var old)) files[index] = "# Index\n\n" + generated + "\n";
            else
            {
                int start = old.IndexOf(Begin, StringComparison.Ordinal);
                int end = old.IndexOf(End, start < 0 ? 0 : start + Begin.Length, StringComparison.Ordinal);
                if ((start < 0) != (end < 0)) throw BundleReader.Error("index.markers", index, "Generated index markers are incomplete.");
                files[index] = start < 0 ? old + (old.EndsWith('\n') ? "\n" : "\n\n") + generated + "\n"
                    : old[..start] + generated + old[(end + End.Length)..];
            }
        }
        return files;
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("[", "\\[").Replace("]", "\\]");

    internal static WriteResult Apply(string bundlePath, Func<BundleSnapshot, Dictionary<string, string>> propose,
        Func<Finding, Finding>? mapBaseline = null, CancellationToken cancellationToken = default)
    {
        var root = BundleReader.PhysicalPath(bundlePath);
        lock (Locks.GetOrAdd(root, _ => new object()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var rootHandle = UseNativeCommit ? OpenDirectory(root) : null;
            var commitRoot = rootHandle == null ? root : HandlePath(rootHandle);
            var baseline = BundleReader.Load(root, cancellationToken);
            var old = baseline.Documents.ToDictionary(d => d.Path, d => d.Text, StringComparer.Ordinal);
            var proposed = propose(baseline);
            cancellationToken.ThrowIfCancellationRequested();
            var view = new BundleSnapshot(root, proposed.OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => { cancellationToken.ThrowIfCancellationRequested(); return BundleReader.ParseDocument(x.Key, x.Value); }).ToArray());
            var before = Validator.Validate(baseline, true, cancellationToken).Findings.Select(f => mapBaseline?.Invoke(f) ?? f).GroupBy(Key).ToDictionary(g => g.Key, g => g.Count());
            var added = new List<Finding>();
            foreach (var finding in Validator.Validate(view, true, cancellationToken).Findings)
            {
                var key = Key(finding);
                if (before.TryGetValue(key, out var count) && count > 0) before[key] = count - 1;
                else added.Add(finding);
            }
            if (added.Count > 0) return new(false, Array.Empty<string>(), added, "Proposed changes introduce validation findings.");
            var current = BundleReader.Load(root, cancellationToken).Documents.ToDictionary(d => d.Path, d => d.Text, StringComparer.Ordinal);
            if (old.Count != current.Count || old.Any(p => !current.TryGetValue(p.Key, out var text) || text != p.Value))
                return new(false, Array.Empty<string>(), Array.Empty<Finding>(), "Bundle changed concurrently.");
            // Cancellation is honored until this point. Once a commit starts, finish
            // it or report the paths that may have changed.
            cancellationToken.ThrowIfCancellationRequested();
            var touched = new List<string>();
            try
            {
                foreach (var path in old.Keys.Union(proposed.Keys).Order(StringComparer.Ordinal))
                {
                    if (old.TryGetValue(path, out var previous) && proposed.TryGetValue(path, out var next) && previous == next) continue;
                    BeforeCommitFile?.Invoke(path);
                    old.TryGetValue(path, out var expected);
                    proposed.TryGetValue(path, out var text);
                    touched.Add(path); // The operation may succeed before an I/O failure is reported.
                    CommitFile(commitRoot, rootHandle, path, expected, text);

                }
                return new(true, touched, Array.Empty<Finding>());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return new(false, touched, Array.Empty<Finding>(), $"Commit failed; {touched.Count} files may have been changed: {ex.Message}"); }
        }
    }
    private static (string, string, string, FindingKind) Key(Finding f) => (f.Rule, f.File, f.Subject ?? f.Message, f.Kind);
}
