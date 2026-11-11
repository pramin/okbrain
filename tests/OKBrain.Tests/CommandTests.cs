using System.Text.Json;
using OKBrain.Tool;

namespace OKBrain.Tests;

[NonParallelizable]
public class CommandTests : BundleFixture
{
    private static (int Exit, string Output, string Error) Run(params string[] arguments)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exit = Program.Main(arguments);
            return (exit, output.ToString(), error.ToString());
        }
        finally { Console.SetOut(originalOut); Console.SetError(originalError); }
    }

    [Test]
    public void ReindexAndMcpWriteThroughSymlinkedBundleRoot()
    {
        Write("index.md", "# Index\n");
        var link = Root + "-link";
        Directory.CreateSymbolicLink(link, Root);
        try
        {
            var result = Run("reindex", link);
            Assert.That(result.Exit, Is.Zero, result.Output + result.Error);
            var write = new BrainTools(link).Append(null!, "index.md", "Added\n");
            Assert.That(write.IsError, Is.Not.True);
            Assert.That(File.ReadAllText(Path.Combine(Root, "index.md")), Does.Contain("Added\n"));
        }
        finally { Directory.Delete(link); }
    }

    [Test]
    public void HelpPrintsUsageAndNeverRequiresBundle()
    {
        foreach (var args in new[] { new[] { "--help" }, new[] { "help" }, new[] { "help", "search" }, new[] { "validate", "--help" }, new[] { "serve", "--help" } })
        {
            var result = Run(args);
            Assert.That(result.Exit, Is.Zero);
            Assert.That(result.Output, Does.Contain("Usage:").And.Contain("okbrain validate <bundle>"));
            Assert.That(result.Error, Is.Empty);
        }
    }

    [Test]
    public void RequiredArgumentsAndInvalidOptionsAreInputErrors()
    {
        foreach (var args in new[] { Array.Empty<string>(), new[] { "validate" }, new[] { "search", "--query", "text" },
            new[] { "stats" }, new[] { "search", Root, "--query" }, new[] { "stats", Root, "extra" },
            new[] { "validate", Root, "--unknown" } })
        {
            var result = Run(args);
            Assert.That(result.Exit, Is.EqualTo(2));
            Assert.That(JsonDocument.Parse(result.Error).RootElement.GetProperty("rule").GetString(), Is.EqualTo("input.arguments"));
        }
    }

    [Test]
    public void CliReturnsStructuredFindingsAndCorrectExitCodes()
    {
        Write("doc.md", "---\ntype: Future\nstatus: invalid\n---\n[broken](absent.md)\n");
        var normal = Run("validate", Root);
        var strict = Run("validate", "--strict", Root);
        Assert.Multiple(() =>
        {
            Assert.That(normal.Exit, Is.Zero);
            Assert.That(strict.Exit, Is.EqualTo(1));
            var parsed = JsonDocument.Parse(strict.Output).RootElement;
            Assert.That(parsed.GetProperty("valid").GetBoolean(), Is.False);
            var finding = parsed.GetProperty("findings")[0];
            Assert.That(finding.GetProperty("file").GetString(), Is.EqualTo("doc.md"));
            Assert.That(finding.GetProperty("rule").GetString(), Is.Not.Empty);
            Assert.That(finding.GetProperty("line").GetInt32(), Is.GreaterThan(0));
        });
        Write("doc.md", "---\ntype: ''\n---\n");
        Assert.That(Run("validate", Root).Exit, Is.EqualTo(1));
    }

    [Test]
    public void SearchAndStatsUseTheSharedCore()
    {
        Write("doc.md", "---\ntype: Metric\n---\nneedle\n");
        Assert.That(JsonDocument.Parse(Run("search", Root).Output).RootElement.GetArrayLength(), Is.EqualTo(1));
        Assert.That(JsonDocument.Parse(Run("search", Root, "--query", "NEEDLE").Output).RootElement.GetArrayLength(), Is.EqualTo(1));
        Assert.That(JsonDocument.Parse(Run("search", Root, "--query=--help").Output).RootElement.GetArrayLength(), Is.Zero);
        Assert.That(JsonDocument.Parse(Run("stats", Root).Output).RootElement.GetProperty("concepts").GetInt32(), Is.EqualTo(1));
        Assert.That(Run("stats", Path.Combine(Root, "missing")).Exit, Is.EqualTo(1));
    }
    [Test]
    public void HelpTokensCanBeQueryValuesOrBundlePathsAfterOptionTerminator()
    {
        Write("-h/doc.md", "---\ntype: Metric\n---\nSearch terms: -h and --help\n");
        var bundle = Path.Combine(Root, "-h");
        foreach (var query in new[] { "-h", "--help" })
        {
            var result = Run("search", bundle, "--query", query);
            Assert.That(result.Exit, Is.Zero);
            Assert.That(JsonDocument.Parse(result.Output).RootElement.GetArrayLength(), Is.EqualTo(1));
        }
        var originalDirectory = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(Root);
            var result = Run("stats", "--", "-h");
            Assert.That(result.Exit, Is.Zero);
            Assert.That(JsonDocument.Parse(result.Output).RootElement.GetProperty("concepts").GetInt32(), Is.EqualTo(1));
        }
        finally { Directory.SetCurrentDirectory(originalDirectory); }
    }

    [Test]
    public void DeleteAndMoveExposeResultsAndFailures()
    {
        Write("index.md", "# Root\n");
        Write("a.md", "---\ntype: X\n---\n");
        Write("b.md", "---\ntype: X\n---\n[link](a.md)\n");
        Assert.That(Run("move", Root, "a.md").Exit, Is.EqualTo(2));
        Assert.That(Run("move", Root, "a.md", "b.md").Exit, Is.EqualTo(1));
        var moved = Run("move", Root, "a.md", "c.md");
        Assert.That(moved.Exit, Is.Zero, moved.Output + moved.Error);
        Assert.That(JsonDocument.Parse(moved.Output).RootElement.GetProperty("touchedFiles").EnumerateArray().Select(x => x.GetString()), Does.Contain("b.md"));
        Assert.That(File.ReadAllText(Path.Combine(Root, "b.md")), Does.Contain("[link](c.md)"));
        var deleted = Run("delete", Root, "c.md");
        Assert.That(deleted.Exit, Is.Zero, deleted.Output + deleted.Error);
        Assert.That(File.ReadAllText(Path.Combine(Root, "b.md")), Does.Contain("link\n"));
        Assert.That(Run("delete", Root, "c.md").Exit, Is.EqualTo(1));
        Assert.That(Run("graph", Root).Exit, Is.Zero);
    }

    [Test]
    public void GraphExposesQueriesAndRejectsInvalidOptions()
    {
        Write("a.md", "---\ntype: X\n---\n[b](b.md 'uses') [missing](missing.png)\n");
        Write("b.md", "---\ntype: X\n---\n");
        var graph = JsonDocument.Parse(Run("graph", Root).Output).RootElement;
        Assert.That(graph.GetProperty("relations").GetProperty("uses").GetInt32(), Is.EqualTo(1));
        var query = JsonDocument.Parse(Run("graph", Root, "--doc", "b.md", "--depth", "1", "--relation", "uses").Output).RootElement;
        Assert.That(query.GetProperty("backlinks").GetArrayLength(), Is.EqualTo(1));
        Assert.That(query.GetProperty("neighbors").GetArrayLength(), Is.EqualTo(1));
        Assert.That(JsonDocument.Parse(Run("graph", Root, "--findings").Output).RootElement.GetProperty("brokenLinks").GetArrayLength(), Is.EqualTo(1));
        Assert.That(Run("graph", Root, "--depth", "-1").Exit, Is.EqualTo(2));
        Assert.That(Run("graph", Root, "--relation", "uses").Exit, Is.EqualTo(2));
    }


    [Test]
    public void CreateMakesValidBundleWithGitAndRefusesNonEmpty()
    {
        // Hermetic: CI runners have no git identity, so supply one for the child git processes.
        var vars = new Dictionary<string, string> { ["GIT_AUTHOR_NAME"] = "Test", ["GIT_AUTHOR_EMAIL"] = "test@example.invalid", ["GIT_COMMITTER_NAME"] = "Test", ["GIT_COMMITTER_EMAIL"] = "test@example.invalid" };
        var saved = vars.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        foreach (var (k, v) in vars) Environment.SetEnvironmentVariable(k, v);
        try { CreateMakesValidBundleCore(); }
        finally { foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v); }
    }

    private void CreateMakesValidBundleCore()
    {
        var target = Path.Combine(Root, "new", ".brain");
        var result = Run("create", target);
        Assert.That(result.Exit, Is.Zero, result.Output + result.Error);
        Assert.That(File.Exists(Path.Combine(target, "index.md")), Is.True);
        Assert.That(Directory.Exists(Path.Combine(target, ".git")), Is.True);
        Assert.That(Run("validate", target, "--strict").Exit, Is.Zero);
        Assert.That(File.ReadAllLines(Path.Combine(target, ".gitignore")), Does.Contain(".git/"));
        Assert.That(Git(target, "ls-files"), Does.Contain(".gitignore").And.Contain("index.md").And.Not.Contain(".git/"));
        Assert.That(Git(target, "status", "--porcelain"), Is.Empty);
        Assert.That(Run("create", target).Exit, Is.EqualTo(1));
        var plain = Path.Combine(Root, "plain");
        Assert.That(Run("create", plain, "--no-git").Exit, Is.Zero);
        Assert.That(Directory.Exists(Path.Combine(plain, ".git")), Is.False);
        Assert.That(Run("create").Exit, Is.EqualTo(2));
    }

    private static string Git(string dir, params string[] args)
    {
        var info = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(info)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }
}
