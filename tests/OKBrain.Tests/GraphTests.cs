using OKBrain.Core;

namespace OKBrain.Tests;

public class GraphTests : BundleFixture
{
    [Test]
    public void ExportAndRelationsIncludeAllLocalTargets()
    {
        Write("a.md", "---\ntype: X\n---\n[asset](missing.png \"uses\") [upper](missing.MD 'uses')\n");
        var graph = BrainGraph.Export(Root);
        Assert.That(graph.Edges.Select(e => e.Target), Is.EquivalentTo(new[] { "missing.png", "missing.MD" }));
        Assert.That(graph.Nodes.Select(n => n.Path), Does.Not.Contain("missing.png"));
        Assert.That(graph.Edges.Single(e => e.Target == "missing.png").NonDocumentTarget, Is.True);
        Assert.That(BrainGraph.Neighbors(Root, "a.md", direction: "out").Select(n => n.Path), Does.Not.Contain("missing.png"));
        Assert.That(BrainGraph.Relations(Root)["uses"], Is.EqualTo(2));
        Assert.That(BrainGraph.Findings(Root).BrokenLinks.Select(e => e.Target), Is.EquivalentTo(new[] { "missing.png", "missing.MD" }));
    }
    [Test]
    public void GraphUsesCurrentMarkdownAndRelations()
    {
        Write("a.md", "---\ntype: X\n---\n[B](b.md 'depends-on') [broken](missing.md)\n");
        Write("b.md", "---\ntype: X\n---\n");
        var graph = BrainGraph.Export(Root);
        Assert.That(graph.Edges, Has.Count.EqualTo(2));
        Assert.That(BrainGraph.Backlinks(Root, "b.md")[0].Relation, Is.EqualTo("depends-on"));
        Assert.That(BrainGraph.Neighbors(Root, "b.md", direction: "in")[0].Path, Is.EqualTo("a.md"));
        Assert.That(BrainGraph.Findings(Root).BrokenLinks[0].Target, Is.EqualTo("missing.md"));
        Write("a.md", "---\ntype: X\n---\n");
        Assert.That(BrainGraph.Backlinks(Root, "b.md"), Is.Empty);
    }

    [Test]
    public void TitlesTraversalAndAllBrokenLocalTargetsAreReported()
    {
        Write("a.md", "---\ntype: X\n---\n[b](b.md \"double\") [b](b.md 'single') [b](b.md (paren)) [ref][id] [asset](missing.png) [escape](../outside)\n\n[id]: b.md 'reference'\n");
        Write("b.md", "---\ntype: X\n---\n[c](c.md 'single')\n");
        Write("c.md", "---\ntype: X\n---\n");
        File.WriteAllText(Path.Combine(Root, "present.png"), "data");
        var relations = BrainGraph.Relations(Root);
        Assert.That(relations.Keys, Is.SupersetOf(new[] { "double", "single", "paren", "reference" }));
        Assert.That(BrainGraph.Neighbors(Root, "a.md", 2, "out", "single").Select(n => n.Path), Is.EqualTo(new[] { "b.md", "c.md" }));
        Assert.That(BrainGraph.Findings(Root).BrokenLinks.Select(e => e.Target), Is.EquivalentTo(new[] { "missing.png", "../outside" }));
    }
}
