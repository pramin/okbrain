using OKBrain.Core;

namespace OKBrain.Tests;

public class LinkTests : BundleFixture
{
    [Test]
    public void FindsRealMarkdownLinksWithoutTreatingCodeOrExternalUrlsAsLocalLinks()
    {
        Write("folder/source.md", """
            ---
            type: Playbook
            ---
            [exists](../present.md#section) [root](/present.md) [self](#part)
            [web](https://example.invalid/missing.md) [email](mailto:test@example.invalid)
            `[inline](missing-inline.md)`

            ```md
            [fenced](missing-fenced.md)
            ```
            Broken [relative](not%20here.md "relation") and [reference][ref].

            [ref]: /missing-reference.md
            """);
        Write("present.md", "---\ntype: Thing\n---\n");
        var findings = Brain.Validate(Root).Findings.Where(f => f.Kind == FindingKind.BrokenLink).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(findings, Has.Length.EqualTo(2), string.Join("\n", findings));
            Assert.That(findings.Select(f => f.Message), Has.Some.Contains("not%20here.md"));
            Assert.That(findings.Select(f => f.Message), Has.Some.Contains("missing-reference.md"));
            Assert.That(findings[0].Line, Is.EqualTo(11));
            Assert.That(findings[0].Column, Is.EqualTo(8));
        });
    }

    [Test]
    public void ChecksOtherFilesDirectoriesEscapesAndEditsToTargets()
    {
        Write("source.md", "---\ntype: X\n---\n[asset](assets/example.txt) [dir](assets/) [escape](../outside.md)\n");
        Write("assets/example.txt", "text");
        var result = Brain.Validate(Root, true);
        Assert.That(result.Findings.Count(f => f.Rule == "link.broken"), Is.EqualTo(1));
        File.Delete(Path.Combine(Root, "assets/example.txt"));
        Assert.That(Brain.Validate(Root, true).Findings.Count(f => f.Rule == "link.broken"), Is.EqualTo(2));
    }
}
