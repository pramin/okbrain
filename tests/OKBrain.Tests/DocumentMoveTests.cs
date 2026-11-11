using OKBrain.Core;

namespace OKBrain.Tests;

public class DocumentMoveTests : BundleFixture
{
    [Test]
    public void MovePreservesTargetSpellingAndSuffixes()
    {
        Write("index.md", "# Index\n");
        Write("a file.md", "---\ntype: X\n---\n");
        Write("b.md", "---\ntype: X\n---\n[x](./a%20file.md?x=%2f#part \"rel\") [y](<./a%20file.md> 'title') [z][id]\n\n[id]: ./a%20file.md 'ref'\n");
        var result = BrainWrites.Move(Root, "a file.md", "c file.md");
        Assert.That(result.Success, Is.True, result.Error + string.Join('\n', result.NewFindings));
        Assert.That(File.ReadAllText(Path.Combine(Root, "b.md")), Does.Contain("[x](./c%20file.md?x=%2f#part \"rel\") [y](<./c%20file.md> 'title') [z][id]\n\n[id]: ./c%20file.md 'ref'"));
    }
    [Test]
    public void MoveRetargetsInlineAndReferenceLinksAndOwnLinks()
    {
        Write("index.md", "# Index\n");
        Write("a.md", "---\ntype: X\n---\n[other](b.md 'rel')\n");
        Write("b.md", "---\ntype: X\n---\n[inline](a.md \"rel\") [ref][a]\n\n[a]: a.md (relation)\n");
        var result = BrainWrites.Move(Root, "a.md", "sub/a.md");
        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(File.ReadAllText(Path.Combine(Root, "sub/a.md")), Does.Contain("(../b.md 'rel')"));
        var b = File.ReadAllText(Path.Combine(Root, "b.md"));
        Assert.That(b, Does.Contain("(sub/a.md \"rel\")"));
        Assert.That(b, Does.Contain("[a]: sub/a.md (relation)"));
    }

    [Test]
    public void DeleteUnlinksAndPreservesText()
    {
        Write("index.md", "# Index\n");
        Write("a.md", "---\ntype: X\n---\n");
        Write("b.md", "---\ntype: X\n---\nBefore [**label**](a.md 'relation') after.\n");
        var result = BrainWrites.Delete(Root, "a.md");
        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(File.ReadAllText(Path.Combine(Root, "b.md")), Does.Contain("Before **label** after."));
        Assert.That(File.Exists(Path.Combine(Root, "a.md")), Is.False);
    }
}

public class DocumentMoveCoverageTests : BundleFixture
{
    private const string Concept = "---\ntype: X\n---\n";

    [Test]
    public void MovePreservesTitlesFormattingFragmentsAndCodeAndRefreshesIndexes()
    {
        Write("index.md", "---\nokf_version: '0.2'\n---\n# Root\n\nAuthored.\n");
        Write("old/a.md", Concept + "[other](../b.md#section) [self](a.md?q=1#s)\n");
        Write("b.md", Concept + "[one](old/a.md \"double\") [two](<old/a.md> 'single') [three](old/a.md (paren)) [ref][id]\n\n[id]: old/a.md#part 'reference'\n\n`[code](old/a.md)`\n");
        var result = BrainWrites.Move(Root, "old/a.md", "new/a.md");
        Assert.That(result.Success, Is.True, result.Error + string.Join('\n', result.NewFindings));
        Assert.That(result.TouchedFiles, Is.SupersetOf(new[] { "old/a.md", "new/a.md", "b.md", "index.md", "new/index.md" }));
        Assert.That(File.ReadAllText(Path.Combine(Root, "b.md")), Does.Contain("[one](new/a.md \"double\") [two](<new/a.md> 'single') [three](new/a.md (paren)) [ref][id]")
            .And.Contain("[id]: new/a.md#part 'reference'").And.Contain("`[code](old/a.md)`"));
        Assert.That(File.ReadAllText(Path.Combine(Root, "new/a.md")), Does.Contain("[other](../b.md#section) [self](a.md?q=1#s)"));
        Assert.That(File.ReadAllText(Path.Combine(Root, "index.md")), Does.StartWith("---\nokf_version: '0.2'\n---\n# Root\n\nAuthored.\n"));
        Assert.That(File.ReadAllText(Path.Combine(Root, "new/index.md")), Does.Contain("(a.md)"));
        Assert.That(Brain.Validate(Root, true).Valid, Is.True);
    }

    [Test]
    public void DeleteUnlinksInlineAndAllReferenceFormsWithoutChangingOtherText()
    {
        Write("index.md", "# Root\n");
        Write("a.md", Concept);
        const string source = Concept + "[inline](a.md \"title\") [full][id] [id][] [id] ![picture](a.md (title))\n\n[id]: a.md 'title'\n\n`[code](a.md)`\n";
        Write("b.md", source);
        var result = BrainWrites.Delete(Root, "a.md");
        Assert.That(result.Success, Is.True, result.Error + string.Join('\n', result.NewFindings));
        Assert.That(File.ReadAllText(Path.Combine(Root, "b.md")), Is.EqualTo(Concept + "inline full id id picture\n\n[id]: a.md 'title'\n\n`[code](a.md)`\n"));
        Assert.That(result.TouchedFiles, Is.SupersetOf(new[] { "a.md", "b.md", "index.md" }));
    }

    [Test]
    public void DeleteHandlesAnImageNestedInADeletedLink()
    {
        Write("index.md", "# Root\n");
        Write("a.md", Concept);
        Write("b.md", Concept + "[![picture](a.md)](a.md)\n");
        var result = BrainWrites.Delete(Root, "a.md");
        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(File.ReadAllText(Path.Combine(Root, "b.md")), Is.EqualTo(Concept + "picture\n"));
    }

    [Test]
    public void InvalidPathsMissingSourceAndExistingDestinationDoNotChangeFiles()
    {
        Write("index.md", "# Root\n");
        Write("a.md", Concept);
        Write("b.md", Concept + "existing\n");
        foreach (var action in new TestDelegate[] { () => BrainWrites.Delete(Root, "../a.md"), () => BrainWrites.Delete(Root, "missing.md"),
                     () => BrainWrites.Move(Root, "a.md", "b.md"), () => BrainWrites.Move(Root, "a.md", ".git/a.md") })
            Assert.Throws<BundleException>(action);
        Assert.That(File.ReadAllText(Path.Combine(Root, "a.md")), Is.EqualTo(Concept));
        Assert.That(File.ReadAllText(Path.Combine(Root, "b.md")), Is.EqualTo(Concept + "existing\n"));
        Assert.That(File.ReadAllText(Path.Combine(Root, "index.md")), Is.EqualTo("# Root\n"));
    }

    [Test]
    public void MoveDoesNotTreatAnUnchangedFindingAsNew()
    {
        Write("index.md", "# Root\n");
        Write("a.md", "---\ntype: X\nstatus: invalid\n---\n");
        var result = BrainWrites.Move(Root, "a.md", "b.md");
        Assert.That(result.Success, Is.True, result.Error + string.Join('\n', result.NewFindings));
        Assert.That(Brain.Validate(Root, true).Findings, Has.Some.Matches<Finding>(f => f.File == "b.md"));
    }

    [Test]
    public void GateRejectsNewFindingsWithoutWritingAnyProposedChanges()
    {
        Write("index.md", "# Root\n");
        Write("log.md", "## 2025-01-01\n\n- Entry\n");
        Write("ref.md", Concept + "[log](log.md)\n");
        var result = BrainWrites.Move(Root, "log.md", "new.md");
        Assert.That(result.Success, Is.False);
        Assert.That(result.NewFindings, Has.Some.Matches<Finding>(f => f.File == "new.md" && f.Rule == "frontmatter.required"));
        Assert.That(result.TouchedFiles, Is.Empty);
        Assert.That(File.Exists(Path.Combine(Root, "new.md")), Is.False);
        Assert.That(File.ReadAllText(Path.Combine(Root, "log.md")), Is.EqualTo("## 2025-01-01\n\n- Entry\n"));
        Assert.That(File.ReadAllText(Path.Combine(Root, "ref.md")), Is.EqualTo(Concept + "[log](log.md)\n"));
        Assert.That(File.ReadAllText(Path.Combine(Root, "index.md")), Is.EqualTo("# Root\n"));
    }
}

public class FindingIdentityMoveTests : BundleFixture
{
    [Test]
    public void MovePreservesIdentityOfRewrittenBrokenLink()
    {
        Write("index.md", "# Index\n");
        Write("a.md", "---\ntype: X\n---\n[x](missing.md)\n");
        var result = BrainWrites.Move(Root, "a.md", "sub/a.md");
        Assert.That(result.Success, Is.True, result.Error + string.Join('\n', result.NewFindings));
        Assert.That(File.ReadAllText(Path.Combine(Root, "sub/a.md")), Does.Contain("(../missing.md)"));
    }

    [Test]
    public void MoveRejectsNewBrokenLinkEvenWhenAnotherAlreadyExists()
    {
        Write("index.md", "# Index\n");
        Write("a.md", "---\ntype: X\n---\n[x](missing.md)\n");
        Write("b.md", "---\ntype: X\n---\n[x](a.md)\n");
        // Model a move whose proposal fails to rewrite an inbound link.
        var direct = BrainWrites.Apply(Root, snapshot =>
        {
            var files = snapshot.Documents.ToDictionary(d => d.Path, d => d.Text);
            files["moved.md"] = files["a.md"];
            files.Remove("a.md");
            return files;
        }, f => f.File == "a.md" ? f with { File = "moved.md" } : f);
        Assert.That(direct.Success, Is.False);
        Assert.That(direct.NewFindings, Has.Some.Matches<Finding>(f => f.Rule == "link.broken" && f.File == "b.md"));
    }
}
