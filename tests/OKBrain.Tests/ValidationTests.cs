using OKBrain.Core;

namespace OKBrain.Tests;

public class ValidationTests : BundleFixture
{
    [Test]
    public void MinimalConceptAndUnknownExtensionsAreConformant()
    {
        Write("index.md", "---\nokf_version: '0.2'\ncustom_root: future\n---\n# Concepts\n\n* [A](a.md)\n");
        Write("a.md", "---\ntype: Some Future Type\ncustom: { nested: [anything, 5] }\n---\nAny body.\n");
        Write("nested/index.md", "# Contents\n");
        Write("nested/log.md", "# Changes\n\n## 2026-05-22\n* First.\n\n## 2026-05-15\n* Earlier.\n");
        var result = Brain.Validate(Root, strict: true);
        Assert.Multiple(() =>
        {
            Assert.That(result.Valid, Is.True);
            Assert.That(result.Findings, Is.Empty);
            Assert.That(Brain.LoadBundle(Root).Documents.Single(d => d.Path == "a.md").Type, Is.EqualTo("Some Future Type"));
        });
    }

    [TestCase("body only", "frontmatter.required")]
    [TestCase("---\ntype: Metric\n", "frontmatter.unclosed")]
    [TestCase("---\n- not a mapping\n---\n", "frontmatter.mapping")]
    [TestCase("---\ntype: [unclosed\n---\n", "frontmatter.yaml")]
    [TestCase("---\ntitle: Missing type\n---\n", "concept.type")]
    [TestCase("---\ntype: ''\n---\n", "concept.type")]
    [TestCase("---\ntype: 42\n---\n", "concept.type")]
    [TestCase("---\ntype: true\n---\n", "concept.type")]
    [TestCase("---\ntype: null\n---\n", "concept.type")]
    public void NonconformantConceptFailsNormally(string content, string rule)
    {
        Write("nested/bad.md", content);
        var result = Brain.Validate(Root);
        Assert.Multiple(() =>
        {
            Assert.That(result.Valid, Is.False);
            Assert.That(result.Findings, Has.Some.Matches<Finding>(f => f.Rule == rule && f.File == "nested/bad.md" && f.Line >= 1 && f.Column >= 1));
        });
    }

    [Test]
    public void QuotedScalarTypeAndFullOptionalMetadataAreValid()
    {
        Write("metric.md", """
            ---
            type: "true"
            title: Metric
            description: One sentence.
            resource: /reference.md
            tags: [finance, revenue]
            status: stable
            generated: { by: agent/v1, at: 2026-06-20T22:53:05Z }
            verified: [{ by: human:ann, at: 2026-06-25T09:00:00+00:00 }, { by: process:nightly, at: 2026-06-26T02:00:00Z }]
            stale_after: 2027-09-23T00:00:00Z
            sources:
              - id: source-1
                resource: all reports in this scope
                title: Source
                author: team:finance
                usage_count: 5000
                last_modified: 2026-05-30T00:00:00Z
                usage_window: { from: 2026-06-01T00:00:00Z, to: 2026-06-30T00:00:00Z }
            usage_window: { from: 2026-06-01T00:00:00Z, to: 2026-06-30T00:00:00Z }
            ---
            Text.
            """);
        Write("calc.md", """
            ---
            type: Attested Computation
            runtime: bigquery
            parameters:
              - { name: year, type: integer, required: true }
            executor: { resource: references/run.md, receipt: [job_id, result] }
            attester: { resource: references/check.py }
            verified: { by: human:ann, at: 2026-06-25T09:00:00Z }
            ---
            # Computation

            ```sql
            SELECT 1
            ```
            """);
        var result = Brain.Validate(Root, strict: true);
        Assert.That(result.Findings, Is.Empty, string.Join("\n", result.Findings));
        Assert.That(result.Valid, Is.True);
    }

    [Test]
    public void OptionalMetadataAndBrokenLinksAreWarningsUnlessStrict()
    {
        Write("doc.md", "---\ntype: Metric\nstatus: unknown\ntags: nope\ngenerated: { at: yesterday }\nverified: {by: human:ann}\nsources: [{id: a}]\nstale_after: 2026-01-01\n---\nSee [missing](absent.md).\n");
        var normal = Brain.Validate(Root);
        var strict = Brain.Validate(Root, strict: true);
        Assert.Multiple(() =>
        {
            Assert.That(normal.Valid, Is.True);
            Assert.That(strict.Valid, Is.False);
            Assert.That(strict.Findings, Is.EqualTo(normal.Findings));
            Assert.That(normal.Findings.Select(f => f.Rule), Does.Contain("metadata.status").And.Contain("metadata.tags")
                .And.Contain("metadata.generated.by").And.Contain("metadata.generated.at").And.Contain("metadata.verified.at")
                .And.Contain("metadata.sources.resource").And.Contain("metadata.stale_after").And.Contain("link.broken"));
            var status = normal.Findings.Single(f => f.Rule == "metadata.status");
            Assert.That((status.File, status.Line, status.Column, status.Kind), Is.EqualTo(("doc.md", 3, 9, FindingKind.OptionalMetadata)));
            var link = normal.Findings.Single(f => f.Rule == "link.broken");
            Assert.That((link.Line, link.Column, link.Kind), Is.EqualTo((10, 5, FindingKind.BrokenLink)));
        });
    }

    [Test]
    public void ChecksNestedOptionalMetadataAndConditionalComputationRules()
    {
        Write("calc.md", """
            ---
            type: Attested Computation
            parameters: [{name: x, type: integer, required: not-a-bool}]
            executor: {receipt: incorrect}
            attester: {}
            sources:
              - {resource: x, id: same, usage_count: -3, last_modified: invalid}
              - {resource: y, id: same}
            usage_window: {from: 2026-02-01T00:00:00Z, to: 2026-01-01T00:00:00Z}
            ---
            """);
        var result = Brain.Validate(Root, strict: true);
        Assert.That(result.Valid, Is.False);
        Assert.That(result.Findings.Select(f => f.Rule), Does.Contain("metadata.runtime").And.Contain("metadata.computation")
            .And.Contain("metadata.parameters.required").And.Contain("metadata.executor.resource").And.Contain("metadata.executor.receipt")
            .And.Contain("metadata.attester.resource").And.Contain("metadata.sources.usage_count").And.Contain("metadata.sources.last_modified")
            .And.Contain("metadata.sources.id").And.Contain("metadata.usage_window"));
        Assert.That(result.Findings.All(f => f.Kind == FindingKind.OptionalMetadata), Is.True);
    }

    [Test]
    public void ReservedFilesHaveTheirOwnRules()
    {
        Write("index.md", "No section here.\n");
        Write("x/index.md", "---\nokf_version: '0.2'\n---\n# Contents\n");
        Write("x/log.md", "# Changes\n\n## 2026-01-01\n* Earlier.\n\n## 2026-02-01\n* Later.\n\n## not a date\n* Bad.\n");
        var result = Brain.Validate(Root);
        Assert.Multiple(() =>
        {
            Assert.That(result.Valid, Is.False);
            Assert.That(result.Findings.Select(f => f.Rule), Does.Contain("index.section").And.Contain("frontmatter.reserved")
                .And.Contain("log.order").And.Contain("log.date-heading"));
            Assert.That(result.Findings.Single(f => f.Rule == "log.order").Line, Is.EqualTo(6));
        });
    }

    [Test]
    public void InvalidUtf8IsAConformanceFinding()
    {
        EnsureBundle();
        File.WriteAllBytes(Path.Combine(Root, "bad.md"), [0x2d, 0x2d, 0x2d, 0x0a, 0xff]);
        Assert.That(Brain.Validate(Root).Findings.Select(f => f.Rule), Does.Contain("document.utf8"));
    }
    [Test]
    public void RecognizesYamlScalarTypesForCountsAndParameterFlags()
    {
        Write("calc.md", """
            ---
            type: Attested Computation
            runtime: sql
            computation: query.sql
            parameters: [{name: a, type: integer, required: !!bool "true"}]
            sources: [{resource: first, usage_count: 1_000}, {resource: second, usage_count: 0x10}]
            ---
            """);
        Assert.That(Brain.Validate(Root, true).Findings, Is.Empty);
        var model = Brain.LoadBundle(Root).Documents.Single().Concept!;
        Assert.That(model.Sources.Select(s => s.UsageCount!.Value.ToString()), Is.EqualTo(new[] { "1000", "16" }));
        Write("calc.md", """
            ---
            type: Attested Computation
            runtime: sql
            computation: query.sql
            parameters: [{name: a, type: integer, required: !!str true}]
            sources: [{resource: x, usage_count: "100"}]
            ---
            """);
        Assert.That(Brain.Validate(Root, true).Findings.Select(f => f.Rule),
            Does.Contain("metadata.parameters.required").And.Contain("metadata.sources.usage_count"));
    }

    [Test]
    public void AuthoredIndexPrefixesRemainAllowedAndLogEntriesAreDateGroupedLists()
    {
        Write("index.md", "# Notes\n\n* An authored note\n\n# Contents\n\n* [Item](item.md)\n");
        Write("item.md", "---\ntype: Reference\n---\n");
        Assert.That(Brain.Validate(Root, true).Valid, Is.True);
        Write("log.md", "# Log\n\n## 2026-01-01\nAn entry outside a list.\n");
        var result = Brain.Validate(Root);
        Assert.That(result.Valid, Is.False);
        Assert.That(result.Findings.Select(f => f.Rule), Does.Contain("log.entry"));
    }

    [Test]
    public void UnknownVersionsAreAcceptedAndMalformedVersionIsOptionalFinding()
    {
        Write("index.md", "---\nokf_version: '42.9'\n---\n# Contents\n");
        Assert.That(Brain.Validate(Root, true).Valid, Is.True);
        Write("index.md", "---\nokf_version: future\n---\n# Contents\n");
        Assert.That(Brain.Validate(Root).Valid, Is.True);
        Assert.That(Brain.Validate(Root, true).Findings.Single().Rule, Is.EqualTo("index.version"));
    }

}
