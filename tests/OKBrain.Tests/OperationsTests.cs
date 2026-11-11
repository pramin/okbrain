using OKBrain.Core;

namespace OKBrain.Tests;

public class OperationsTests : BundleFixture
{
    [Test]
    public void EveryOperationObservesExternalChangesAndMissingIndexesAreAllowed()
    {
        Write("one.md", "---\ntype: First\ntags: [alpha]\n---\nOriginal text\n");
        Assert.That(Brain.Validate(Root, true).Valid, Is.True);
        Assert.That(Brain.Search(Root, "original"), Has.Count.EqualTo(1));
        Assert.That(Brain.Stats(Root).Types["First"], Is.EqualTo(1));
        Write("one.md", "---\ntype: Second\ntags: [beta]\n---\nChanged text [new](two.md)\n");
        Assert.That(Brain.Validate(Root, true).Valid, Is.False);
        Assert.That(Brain.Search(Root, "original"), Is.Empty);
        Assert.That(Brain.Search(Root, "CHANGED").Single().Line, Is.EqualTo(5));
        Assert.That(Brain.Stats(Root).Types.Keys, Does.Contain("Second").And.Not.Contain("First"));
        Write("two.md", "---\ntype: Second\n---\n");
        Assert.That(Brain.Validate(Root, true).Valid, Is.True);
        Assert.That(Brain.Search(Root), Has.Count.EqualTo(2));
        Assert.That(Brain.Stats(Root).Types["Second"], Is.EqualTo(2));
        File.Delete(Path.Combine(Root, "two.md"));
        Assert.That(Brain.Validate(Root, true).Valid, Is.False);
    }

    [Test]
    public void StatsDistinguishReservedDocumentsAndCountTypesAndTags()
    {
        Write("index.md", "# Contents\n");
        Write("log.md", "# Log\n\n## 2026-01-01\n* Created\n");
        Write("a.md", "---\ntype: Metric\ntags: [sales, finance, sales]\n---\n");
        Write("sub/b.md", "---\ntype: Metric\ntags: [sales]\n---\n");
        Write("sub/c.md", "---\ntype: Playbook\n---\n");
        Write(".git/ignored.md", "should never be scanned");
        var stats = Brain.Stats(Root);
        Assert.Multiple(() =>
        {
            Assert.That((stats.Documents, stats.Concepts, stats.Indexes, stats.Logs), Is.EqualTo((5, 3, 1, 1)));
            Assert.That(stats.Types["Metric"], Is.EqualTo(2));
            Assert.That(stats.Tags["sales"], Is.EqualTo(2));
            Assert.That(stats.Tags["finance"], Is.EqualTo(1));
        });
    }

    [Test]
    public void MissingBundleProducesStructuredError()
    {
        var ex = Assert.Throws<BundleException>(() => Brain.Validate(Root));
        Assert.That(ex!.Finding.Rule, Is.EqualTo("bundle.not-found"));
        Assert.That(ex.Finding.File, Is.EqualTo(Root));
    }
    [Test]
    public void SafeSymbolicLinksExposeCurrentContentAndCyclesAreReported()
    {
        Write("real/doc.md", "---\ntype: Before\n---\n");
        File.CreateSymbolicLink(Path.Combine(Root, "alias.md"), "real/doc.md");
        Directory.CreateSymbolicLink(Path.Combine(Root, "alias-dir"), "real");
        var first = Brain.Stats(Root);
        Assert.That(first.Concepts, Is.EqualTo(3));
        Assert.That(first.Types["Before"], Is.EqualTo(3));
        Write("real/doc.md", "---\ntype: After\n---\n");
        Assert.That(Brain.Stats(Root).Types["After"], Is.EqualTo(3));
        Directory.CreateSymbolicLink(Path.Combine(Root, "real", "cycle"), Root);
        var error = Assert.Throws<BundleException>(() => Brain.Validate(Root));
        Assert.That(error!.Finding.Rule, Is.EqualTo("bundle.link-cycle"));
        Assert.That(error.Finding.File, Does.EndWith("cycle"));
    }

    [Test]
    public void ExternalSymbolicLinksCannotEscapeTheBundle()
    {
        Write("doc.md", "---\ntype: X\n---\n[asset](external.txt)\n");
        var external = Path.Combine(Path.GetDirectoryName(Root)!, Guid.NewGuid() + ".txt");
        File.WriteAllText(external, "external");
        try
        {
            File.CreateSymbolicLink(Path.Combine(Root, "external.txt"), external);
            Assert.That(Brain.Validate(Root, true).Findings.Select(f => f.Rule), Does.Contain("link.broken"));
            File.CreateSymbolicLink(Path.Combine(Root, "external.md"), external);
            var error = Assert.Throws<BundleException>(() => Brain.Validate(Root));
            Assert.That(error!.Finding.Rule, Is.EqualTo("bundle.path-escape"));
            Assert.That(error.Finding.File, Is.EqualTo("external.md"));
        }
        finally { File.Delete(external); }
    }

    [Test]
    public void ModelReadsRecognizedMetadataAndKeepsExtensions()
    {
        Write("doc.md", """
            ---
            type: Attested Computation
            tags: [finance, revenue]
            generated: {by: agent/v1, at: 2026-01-01T00:00:00Z}
            verified: {by: human:ann, at: 2026-01-02T00:00:00Z}
            sources: [{id: a, resource: /data, usage_count: 10}]
            usage_window: {from: 2026-01-01T00:00:00Z, to: 2026-02-01T00:00:00Z}
            runtime: sql
            parameters: [{name: year, type: integer, required: true}]
            computation: query.sql
            executor: {resource: run.md, receipt: [job_id]}
            attester: {resource: check.py}
            custom: {nested: retained}
            ---
            """);
        var doc = Brain.LoadBundle(Root).Documents.Single();
        var concept = doc.Concept!;
        Assert.Multiple(() =>
        {
            Assert.That(concept.Type, Is.EqualTo("Attested Computation"));
            Assert.That(concept.Tags, Is.EqualTo(new[] { "finance", "revenue" }));
            Assert.That(concept.Generated!.By, Is.EqualTo("agent/v1"));
            Assert.That(concept.Verified.Single().By, Is.EqualTo("human:ann"));
            Assert.That(concept.Sources.Single().UsageCount!.Value.ToString(), Is.EqualTo("10"));
            Assert.That(concept.UsageWindow!.From, Is.EqualTo("2026-01-01T00:00:00Z"));
            Assert.That(concept.Parameters.Single().Required, Is.True);
            Assert.That(concept.Executor!.Receipt.Single(), Is.EqualTo("job_id"));
            Assert.That(concept.Attester!.Resource, Is.EqualTo("check.py"));
            Assert.That(doc.Metadata!.Children.Keys.Any(k => k.ToString() == "custom"), Is.True);
        });
    }

}
