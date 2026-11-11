using OKBrain.Core;

namespace OKBrain.Tests;

public class WriteTests : BundleFixture
{
    [Test]
    public void ReindexPreservesAuthoredTextAndIsIdempotent()
    {
        Write("index.md", "---\nokf_version: '0.2'\n---\n# My index\n\nNotes.\n");
        Write("topic/a.md", "---\ntype: concept\ntitle: Alpha\n---\n# Alpha\n");
        var first = BrainWrites.Reindex(Root);
        Assert.That(first.Success, Is.True, first.Error);
        var index = File.ReadAllText(Path.Combine(Root, "index.md"));
        Assert.That(index, Does.StartWith("---\nokf_version: '0.2'\n---\n# My index\n\nNotes.\n"));
        Assert.That(index, Does.Contain("topic/index.md"));
        Assert.That(File.ReadAllText(Path.Combine(Root, "topic/index.md")), Does.Contain("[Alpha](a.md)"));
        var second = BrainWrites.Reindex(Root);
        Assert.That(second.Success, Is.True, second.Error);
        Assert.That(second.TouchedFiles, Is.Empty);
    }

    [Test]
    public void ReindexRejectsIncompleteMarkersWithoutWriting()
    {
        const string original = "# Index\n<!-- okbrain:generated -->\n";
        Write("index.md", original);
        Write("a.md", "---\ntype: concept\n---\n# A\n");
        Assert.Throws<BundleException>(() => BrainWrites.Reindex(Root));
        Assert.That(File.ReadAllText(Path.Combine(Root, "index.md")), Is.EqualTo(original));
    }
}

[NonParallelizable]
public class CommitRaceTests : BundleFixture
{
    [TearDown]
    public void ClearHook()
    {
        BrainWrites.BeforeCommitFile = null;
        BrainWrites.BeforeDirectoryChange = null;
        BrainWrites.ForcePortableCommit = false;
    }

    [Test]
    public void ConcurrentEditImmediatelyBeforeWriteIsNotOverwritten()
    {
        Write("index.md", "# Index\n");
        BrainWrites.BeforeCommitFile = path =>
        {
            if (path == "index.md") File.WriteAllText(Path.Combine(Root, path), "external edit\n");
        };
        var result = BrainWrites.Reindex(Root);
        Assert.That(result.Success, Is.False);
        Assert.That(File.ReadAllText(Path.Combine(Root, "index.md")), Is.EqualTo("external edit\n"));
    }

    [Test]
    public void SwappedDirectorySymlinkCannotWriteOutsideBundle()
    {
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(outside);
        try
        {
            Write("index.md", "# Index\n");
            Write("sub/a.md", "---\ntype: X\n---\n");
            BrainWrites.BeforeCommitFile = path =>
            {
                if (path != "sub/index.md") return;
                Directory.Move(Path.Combine(Root, "sub"), Path.Combine(Root, "displaced"));
                Directory.CreateSymbolicLink(Path.Combine(Root, "sub"), outside);
            };
            var result = BrainWrites.Reindex(Root);
            Assert.That(result.Success, Is.False);
            Assert.That(File.Exists(Path.Combine(outside, "index.md")), Is.False);
        }
        finally { Directory.Delete(outside, true); }
    }
    [Test]
    public void SwappedRootSymlinkCannotRedirectCommit()
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("Linux handle test");
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var displaced = Root + "-displaced";
        Directory.CreateDirectory(outside);
        try
        {
            Write("index.md", "# Index\n");
            File.WriteAllText(Path.Combine(outside, "index.md"), "# Index\n");
            BrainWrites.BeforeCommitFile = _ => { Directory.Move(Root, displaced); Directory.CreateSymbolicLink(Root, outside); };
            var result = BrainWrites.Reindex(Root);
            Assert.That(result.Success, Is.False);
            Assert.That(File.ReadAllText(Path.Combine(outside, "index.md")), Is.EqualTo("# Index\n"));
        }
        finally { if (Directory.Exists(Root)) Directory.Delete(Root); if (Directory.Exists(displaced)) Directory.Move(displaced, Root); Directory.Delete(outside, true); }
    }

    [TestCase(true)]
    [TestCase(false)]
    public void SwappedParentCannotRedirectDirectoryChange(bool create)
    {
        if (!OperatingSystem.IsLinux()) Assert.Ignore("Linux handle test");
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(outside);
        try
        {
            Write("index.md", "# Index\n");
            Directory.CreateDirectory(Path.Combine(Root, "sub"));
            if (!create) { Directory.CreateDirectory(Path.Combine(Root, "sub", "target")); Directory.CreateDirectory(Path.Combine(outside, "target")); }
            BrainWrites.BeforeDirectoryChange = _ => { Directory.Move(Path.Combine(Root, "sub"), Path.Combine(Root, "displaced")); Directory.CreateSymbolicLink(Path.Combine(Root, "sub"), outside); };
            var result = create ? BrainWrites.MakeDirectory(Root, "sub/target") : BrainWrites.RemoveDirectory(Root, "sub/target");
            Assert.That(result.Success, Is.False);
            Assert.That(Directory.Exists(Path.Combine(outside, "target")), Is.EqualTo(!create));
        }
        finally { Directory.Delete(outside, true); }
    }

    [Test]
    public void PortableFallbackWritesAndDetectsConcurrentEdits()
    {
        BrainWrites.ForcePortableCommit = true;
        Write("index.md", "# Index\n");
        Assert.That(BrainWrites.Reindex(Root).Success, Is.True);
        BrainWrites.BeforeCommitFile = _ => File.WriteAllText(Path.Combine(Root, "index.md"), "external\n");
        var result = BrainWrites.Append(Root, "index.md", "text\n");
        Assert.That(result.Success, Is.False);
        Assert.That(File.ReadAllText(Path.Combine(Root, "index.md")), Is.EqualTo("external\n"));
    }

    [Test]
    public void PortableFallbackAcceptsBomBaselineForAppendReplaceAndDelete()
    {
        BrainWrites.ForcePortableCommit = true;
        Write("index.md", "# Index\n");
        Write("a.md", "\uFEFF---\ntype: X\n---\nBody\n");
        Assert.That(BrainWrites.Append(Root, "a.md", "More\n").Success, Is.True);
        Assert.That(BrainWrites.Replace(Root, "a.md", "---\ntype: X\n---\nNew\n").Success, Is.True);
        Assert.That(BrainWrites.Delete(Root, "a.md").Success, Is.True);
        Assert.That(File.Exists(Path.Combine(Root, "a.md")), Is.False);
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SymlinkedRootSupportsPortableAndNativeWritesAndDirectoryChanges(bool portable)
    {
        BrainWrites.ForcePortableCommit = portable;
        Write("index.md", "# Index\n");
        var link = Root + "-link";
        Directory.CreateSymbolicLink(link, Root);
        try
        {
            Assert.That(BrainWrites.Append(link, "index.md", "text\n").Success, Is.True);
            Assert.That(BrainWrites.MakeDirectory(link, "new").Success, Is.True);
            Assert.That(BrainWrites.RemoveDirectory(link, "new").Success, Is.True);
            Assert.That(File.ReadAllText(Path.Combine(Root, "index.md")), Does.Contain("text\n"));
        }
        finally { Directory.Delete(link); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SymlinkedRootStillRejectsSwappedInteriorSymlink(bool portable)
    {
        BrainWrites.ForcePortableCommit = portable;
        Write("sub/index.md", "# Index\n");
        var link = Root + "-link";
        var outside = Root + "-outside";
        Directory.CreateSymbolicLink(link, Root);
        Directory.CreateDirectory(outside);
        try
        {
            BrainWrites.BeforeCommitFile = _ => { Directory.Move(Path.Combine(Root, "sub"), Path.Combine(Root, "displaced")); Directory.CreateSymbolicLink(Path.Combine(Root, "sub"), outside); };
            var result = BrainWrites.Append(link, "sub/index.md", "text\n");
            Assert.That(result.Success, Is.False);
            Assert.That(File.Exists(Path.Combine(outside, "index.md")), Is.False);
        }
        finally { Directory.Delete(link); Directory.Delete(outside, true); }
    }

    [Test]
    public void PortableFallbackRejectsSwappedSymlink()
    {
        BrainWrites.ForcePortableCommit = true;
        var outside = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(outside);
        try
        {
            Write("sub/index.md", "# Index\n");
            BrainWrites.BeforeCommitFile = _ => { Directory.Move(Path.Combine(Root, "sub"), Path.Combine(Root, "displaced")); Directory.CreateSymbolicLink(Path.Combine(Root, "sub"), outside); };
            var result = BrainWrites.Append(Root, "sub/index.md", "text\n");
            Assert.That(result.Success, Is.False);
            Assert.That(File.Exists(Path.Combine(outside, "index.md")), Is.False);
        }
        finally { Directory.Delete(outside, true); }
    }

}
