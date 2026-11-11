using OKBrain.Core;

namespace OKBrain.Tests;

public class ReviewRegressionTests : BundleFixture
{
    [TestCase("> ## 2026-01-01\n> * Quoted entry\n")]
    [TestCase("Prose outside a group.\n\n## 2026-01-01\n* Entry\n")]
    [TestCase("### Ungrouped entry\n\n## 2026-01-01\n* Entry\n")]
    [TestCase("## 2026-01-01\n> * Quoted entry\n")]
    public void LogRequiresTopLevelDateGroupsAndEntries(string body)
    {
        Write("log.md", "# Log\n\n" + body);
        Assert.That(Brain.Validate(Root).Valid, Is.False);
    }

    [TestCase("+01")]
    [TestCase("-01")]
    [TestCase("+0100")]
    [TestCase("+01:00")]
    [TestCase("Z")]
    public void TimestampAcceptsUtcOffsetForms(string offset)
    {
        Write("concept.md", $"---\ntype: Metric\nstale_after: '2026-06-20T22:53:05{offset}'\n---\n");
        Assert.That(Brain.Validate(Root, strict: true).Findings, Is.Empty);
    }

    [Test]
    public void LinkEntityIsDecodedOnlyOnce()
    {
        Write("a&amp;b.md", "---\ntype: Metric\n---\n");
        Write("source.md", "---\ntype: Metric\n---\n[l](a&amp;amp;b.md)\n");
        Assert.That(Brain.Validate(Root, strict: true).Findings, Is.Empty);
    }
    [Test]
    public void SourceFootnotesAreNotBrokenLinksButLinksInFootnotesAreChecked()
    {
        Write("concept.md", """
            ---
            type: Metric
            sources: [{ id: spec, resource: https://example.invalid/spec }]
            ---
            A claim.[^spec]

            [^spec]: Source.
            """);
        Assert.That(Brain.Validate(Root, strict: true).Findings, Is.Empty);

        Write("concept.md", """
            ---
            type: Metric
            sources: [{ id: spec, resource: https://example.invalid/spec }]
            ---
            A claim.[^spec]

            [^spec]: See the [local source](missing.md).
            """);
        var findings = Brain.Validate(Root, strict: true).Findings;
        Assert.Multiple(() =>
        {
            Assert.That(findings, Has.Count.EqualTo(1));
            Assert.That(findings.Single().Rule, Is.EqualTo("link.broken"));
            Assert.That(findings.Single().File, Is.EqualTo("concept.md"));
            Assert.That(findings.Single().Line, Is.EqualTo(7));
        });
    }

    [Test]
    public void LogFootnoteDefinitionsAreNotUngroupedLogEntries()
    {
        Write("log.md", "# Log\n\n## 2026-01-01\n* Created.[^source]\n\n[^source]: Source.\n");
        Assert.That(Brain.Validate(Root, strict: true).Findings, Is.Empty);
    }

    [Test]
    public void CodeInAFootnoteDoesNotCreateAnotherComputation()
    {
        Write("calc.md", """
            ---
            type: Attested Computation
            runtime: sql
            ---
            # Computation

            ```sql
            SELECT 1
            ```

            This is documented.[^source]

            [^source]: Example:

                ```sql
                SELECT 2
                ```
            """);
        Assert.That(Brain.Validate(Root, strict: true).Findings, Is.Empty);
    }

    [TestCase("", "# Computation\n\n```sql\nSELECT 1\n```\n", true, TestName = "SingleFencedComputationIsValid")]
    [TestCase("computation: query.sql\n", "# Examples\n\n```sql\nSELECT 1\n```\n", true, TestName = "PathAndUnrelatedExampleAreValid")]
    [TestCase("computation: query.sql\n", "# Computation\n\n```sql\nSELECT 1\n```\n", false, TestName = "PathAndInlineComputationAreAmbiguous")]
    [TestCase("", "# Computation\n\n```sql\nSELECT 1\n```\n\n```sql\nSELECT 2\n```\n", false, TestName = "MultipleInlineComputationsAreAmbiguous")]
    [TestCase("", "# Computation\n\n    SELECT 1\n", false, TestName = "IndentedComputationIsNotFenced")]
    [TestCase("", "# Examples\n\n```sql\nSELECT 1\n```\n", false, TestName = "ExampleFenceDoesNotDefineAComputation")]
    [TestCase("computation: query.sql\n", "# Computation\n\n    SELECT 1\n", false, TestName = "PathAndIndentedInlineComputationAreAmbiguous")]
    public void ComputationHasExactlyOneUnambiguousDefinition(string pathField, string body, bool expectedValid)
    {
        Write("calc.md", $"---\ntype: Attested Computation\nruntime: sql\n{pathField}---\n{body}");
        var normal = Brain.Validate(Root);
        var strict = Brain.Validate(Root, strict: true);
        Assert.Multiple(() =>
        {
            Assert.That(normal.Valid, Is.True);
            Assert.That(strict.Valid, Is.EqualTo(expectedValid));
            Assert.That(strict.Findings.Where(f => f.Rule == "metadata.computation").Count(), Is.EqualTo(expectedValid ? 0 : 1));
            Assert.That(strict.Findings.All(f => f.Kind == FindingKind.OptionalMetadata && f.File == "calc.md" && f.Line > 0 && f.Column > 0), Is.True);
        });
    }

    [Test]
    public void LogEntryOutsideDateGroupFailsNormalValidation()
    {
        Write("log.md", "# Log\n\n* Ungrouped event\n\n## 2026-01-01\n* Grouped event\n");
        var result = Brain.Validate(Root);
        Assert.Multiple(() =>
        {
            Assert.That(result.Valid, Is.False);
            Assert.That(result.Findings, Has.Some.Matches<Finding>(f => f.Rule == "log.entry" && f.File == "log.md" &&
                f.Line == 3 && f.Column == 1 && f.Kind == FindingKind.Conformance));
        });
    }

    [Test]
    public void AllTimestampFamiliesRequireAnExplicitOffsetAsSpecifiedInSectionFive()
    {
        Write("concept.md", """
            ---
            type: Metric
            generated: {by: human:alice, at: '2026-06-20T22:53:05'}
            verified: {by: human:alice, at: '2026-06-20T22:53:05'}
            sources: [{resource: example, last_modified: '2026-06-20T22:53:05'}]
            usage_window: {from: '2026-06-20T22:53:05', to: '2026-06-21T22:53:05'}
            stale_after: '2026-06-20T22:53:05'
            ---
            """);
        var result = Brain.Validate(Root, strict: true);
        Assert.That(result.Valid, Is.False);
        Assert.That(result.Findings.Select(f => f.Rule), Does.Contain("metadata.generated.at")
            .And.Contain("metadata.verified.at").And.Contain("metadata.sources.last_modified")
            .And.Contain("metadata.usage_window.from").And.Contain("metadata.usage_window.to")
            .And.Contain("metadata.stale_after"));
        Assert.That(Brain.Validate(Root).Valid, Is.True);
    }
}
