using System.Diagnostics;
using OKBrain.Core;

namespace OKBrain.Tool;

public sealed record GitInit(bool Initialized, bool Committed, string? Note);
public sealed record CreateResult(string Bundle, IReadOnlyList<string> Files, GitInit? Git);

public static class BundleCreator
{
    public static CreateResult Create(string bundle, bool git)
    {
        var root = Path.GetFullPath(bundle);
        if (File.Exists(root)) throw new IOException("The bundle path is an existing file.");
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("The bundle directory already exists and is not empty.");
        Directory.CreateDirectory(root);
        var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)).Trim('.');
        var title = name.Length == 0 ? "Bundle" : char.ToUpperInvariant(name[0]) + name[1..];
        File.WriteAllText(Path.Combine(root, "index.md"), $"---\ntype: Index\ntitle: {title}\ndescription: OKF knowledge bundle.\n---\n# {title}\n");
        // Always written, with or without git: keeps git metadata and the tool's temp files out of any repository.
        File.WriteAllText(Path.Combine(root, ".gitignore"), ".git/\n.okbrain-*.tmp\n");
        var reindex = BrainWrites.Reindex(root);
        if (!reindex.Success) throw new IOException("Could not index the new bundle.");
        var validation = Brain.Validate(root, true);
        if (!validation.Valid) throw new IOException("The new bundle does not validate.");
        return new(root, ["index.md", ".gitignore"], git ? InitGit(root) : null);
    }

    private static GitInit InitGit(string root)
    {
        if (Git(root, "init", "-q", "-b", "main").Exit != 0) return new(false, false, "git init failed; is git installed?");
        Git(root, "add", "-A");
        var commit = Git(root, "commit", "-q", "-m", "Initialize brain");
        return commit.Exit == 0 ? new(true, true, null) : new(true, false, "Committing failed (set user.name and user.email); files are staged. " + commit.Output.Trim());
    }

    private static (int Exit, string Output) Git(string root, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in args) info.ArgumentList.Add(a);
            using var p = Process.Start(info)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception) { return (-1, "git not found"); }
    }
}
