// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Evals;
using AgentEval.Compliance.Gdpr.Reporting;
using AgentEval.Output;
using Json.Schema;
using Xunit;
using AgentEval.Compliance.Core;

namespace AgentEval.Tests.Compliance.Gdpr;

/// <summary>
/// v1.1 task 1.5 — Structured <see cref="Recommendation"/> shape acceptance tests.
/// <para>
/// Covers:
/// <list type="bullet">
///   <item>New structured shape serialises and validates against the updated schema (<c>oneOf</c>).</item>
///   <item>Legacy <c>string[]</c> shape still validates (backward-compat).</item>
///   <item><see cref="RecommendationExtractor"/> produces <see cref="Recommendation"/> records with correct fields.</item>
///   <item><see cref="MarkdownRenderer"/> renders <c>controlId [severity]: text</c> for structured entries.</item>
/// </list>
/// </para>
/// </summary>
public class RecommendationStructuredShapeTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static JsonSchema LoadGdprSchema()
    {
        var asm = typeof(GDPRComplianceReporter).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(".gdpr-evidence.schema.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    private static EvalResult MakeArticleNode(string key, double score, bool passed, string severity) =>
        new(
            Metric: new(key, key, "compliance.test", "1.0"),
            Score: new(score, null, passed ? "pass" : "fail", passed, 0.75, severity, null),
            Details: new(null, null, null, null, null),
            Provenance: new("atomic", "stub", null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

    private static EvalResult MakeRoot(params EvalResult[] children) =>
        new(
            Metric: new("root", "root", "compliance.test", "1.0"),
            Score: new(0.5, null, "fail", false, 0.85, "high", null),
            Details: new(null, null, null, children, "WeightedSum"),
            Provenance: new("composite", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

    private static GdprComplianceEvidence MakeMinimalEvidence(
        IReadOnlyList<Recommendation> recs) =>
        new(
            Base: new ComplianceEvidence(
                SchemaVersion: "1.0",
                Regulation: "GDPR",
                Subject: new SubjectIdentity(SubjectKind.Agent, "TestAgent"),
                GeneratedAt: DateTimeOffset.UtcNow,
                SourceRun: new SourceRunRef("run-001", "sha256:" + new string('a', 64)),
                Controls: [],
                Summary: new EvidenceSummary(1, 0, 0, 1, "FAIL"),
                Attestation: new Attestation("0.0.0", null, "test", "stub")),
            Preset: "smoke",
            DomainPacks: Array.Empty<string>(),
            CompositeTree: MakeArticleNode("root", 0.5, false, "high"),
            Summary: new GdprSummary(0.5, "FAIL",
                new Dictionary<string, GdprPillarSummary>(),
                new Dictionary<string, GdprArticleSummary>()),
            CriticalFindings: Array.Empty<EvalResult>(),
            Recommendations: recs,
            Disclaimer: GDPRComplianceReporter.Disclaimer,
            GdprAttestation: new GdprAttestation("mode-a", new Dictionary<string, string>()));

    // ── Schema: structured shape validates ───────────────────────────────────

    [Fact]
    public void Schema_StructuredRecommendations_Validates()
    {
        var recs = new[]
        {
            new Recommendation("gdpr.art17.erasure", "high", "Acknowledge deletion requests."),
            new Recommendation("gdpr.art9.special_categories", "critical", "Refuse special-category processing."),
        };
        var evidence = MakeMinimalEvidence(recs);
        var json = JsonSerializer.Serialize(evidence, s_jsonOpts);

        var schema = LoadGdprSchema();
        var result = schema.Evaluate(
            JsonNode.Parse(json),
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid,
            "Structured Recommendation[] should validate against the updated schema. Errors: " +
            string.Join("; ", (result.Details ?? [])
                .Where(d => !d.IsValid)
                .SelectMany(d => d.Errors?.Select(e => $"{d.EvaluationPath} {e.Key}={e.Value}") ?? [])));
    }

    // ── Schema: legacy string[] shape still validates (backward-compat) ──────

    [Fact]
    public void Schema_LegacyStringArrayRecommendations_StillValidates()
    {
        // Build raw JSON that uses the v0.8.1-beta strings-only shape.
        // We can't construct GdprComplianceEvidence with string[] directly any more (the
        // type is Recommendation[]), so we write a small JSON template that mirrors the
        // schema shape and inject the legacy array.
        var legacyJson = """
            {
              "base": {
                "schemaVersion": "1.0",
                "regulation": "GDPR",
                "subject": { "kind": "agent", "name": "LegacyAgent" },
                "generatedAt": "2025-01-01T00:00:00+00:00",
                "sourceRun": { "runId": "run-old", "manifestHash": "sha256:aaaa" },
                "controls": [],
                "summary": { "controlsTotal": 0, "passed": 0, "warnings": 0, "failed": 0, "overallStatus": "PASS" },
                "attestation": { "agentEvalVersion": "0.8.1-beta", "evaluator": "test", "evaluatorModel": "stub" }
              },
              "preset": "smoke",
              "domainPacks": [],
              "compositeTree": {},
              "summary": {
                "overallScore": 1.0,
                "overallStatus": "PASS",
                "perPillar": {},
                "perArticle": {}
              },
              "criticalFindings": [],
              "recommendations": ["Acknowledge deletion requests.", "Audit consent flows."],
              "disclaimer": "This is a first-line screening tool and not a legal compliance attestation.",
              "gdprAttestation": { "judgeMode": "mode-a" }
            }
            """;

        var schema = LoadGdprSchema();
        var result = schema.Evaluate(
            JsonNode.Parse(legacyJson),
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.True(result.IsValid,
            "Legacy string[] recommendations from v0.8.1-beta must still validate (oneOf backward-compat). " +
            "Errors: " + string.Join("; ", (result.Details ?? [])
                .Where(d => !d.IsValid)
                .SelectMany(d => d.Errors?.Select(e => $"{d.EvaluationPath} {e.Key}={e.Value}") ?? [])));
    }

    // ── RecommendationExtractor produces structured records ──────────────────

    [Fact]
    public void RecommendationExtractor_StructuredRecord_HasCorrectFields()
    {
        var extractor = new RecommendationExtractor();
        var root = MakeRoot(MakeArticleNode("gdpr.art17.erasure", 0.30, false, "high"));
        var recs = extractor.Build(root);

        Assert.Single(recs);
        var rec = recs[0];
        Assert.Equal("gdpr.art17.erasure", rec.ControlId);
        Assert.Equal("high", rec.Severity);
        Assert.False(string.IsNullOrWhiteSpace(rec.Text), "Text must be non-empty");
    }

    [Fact]
    public void RecommendationExtractor_CriticalSeverity_PreservedInRecord()
    {
        var extractor = new RecommendationExtractor();
        var root = MakeRoot(MakeArticleNode("gdpr.art9.special_categories", 0.10, false, "critical"));
        var recs = extractor.Build(root);

        Assert.Single(recs);
        Assert.Equal("critical", recs[0].Severity);
    }

    [Fact]
    public void RecommendationExtractor_MultipleArticles_OrderedByControlId()
    {
        var extractor = new RecommendationExtractor();
        var root = MakeRoot(
            MakeArticleNode("gdpr.art9.special_categories", 0.10, false, "critical"),
            MakeArticleNode("gdpr.art17.erasure", 0.20, false, "high"),
            MakeArticleNode("gdpr.art5.lawfulness", 0.30, false, "medium"));

        var recs = extractor.Build(root);

        Assert.Equal(3, recs.Count);
        // Must be alphabetical by controlId
        Assert.Equal("gdpr.art17.erasure",         recs[0].ControlId);
        Assert.Equal("gdpr.art5.lawfulness",        recs[1].ControlId);
        Assert.Equal("gdpr.art9.special_categories", recs[2].ControlId);
    }

    // ── MarkdownRenderer renders structured format ───────────────────────────

    [Fact]
    public void MarkdownRenderer_StructuredRecommendation_RendersControlIdSeverityText()
    {
        var recs = new[]
        {
            new Recommendation("gdpr.art17.erasure", "high", "Acknowledge deletion requests."),
        };
        var evidence = MakeMinimalEvidence(recs);
        var renderer = new MarkdownRenderer();
        var md = renderer.Render(evidence);

        Assert.Contains("## Recommendations", md);
        Assert.Contains("`gdpr.art17.erasure`", md);
        Assert.Contains("[high]", md);
        Assert.Contains("Acknowledge deletion requests.", md);
    }

    [Fact]
    public void MarkdownRenderer_MultipleStructuredRecommendations_EachFormatted()
    {
        var recs = new[]
        {
            new Recommendation("gdpr.art9.special_categories", "critical", "Refuse special-category processing."),
            new Recommendation("gdpr.art22.automated", "high", "Offer human review."),
        };
        var evidence = MakeMinimalEvidence(recs);
        var renderer = new MarkdownRenderer();
        var md = renderer.Render(evidence);

        // Each recommendation should appear as: - `controlId` [severity]: text
        Assert.Contains("- `gdpr.art9.special_categories` [critical]:", md);
        Assert.Contains("- `gdpr.art22.automated` [high]:", md);
    }

    // ── Round-trip: serialise + deserialise preserves all fields ─────────────

    [Fact]
    public void StructuredRecommendation_SerialiseDeserialise_RoundTrips()
    {
        var original = new Recommendation("gdpr.art17.erasure", "high", "Acknowledge deletion requests.");
        var json = JsonSerializer.Serialize(original, s_jsonOpts);
        var restored = JsonSerializer.Deserialize<Recommendation>(json, s_jsonOpts);

        Assert.NotNull(restored);
        Assert.Equal(original.ControlId, restored!.ControlId);
        Assert.Equal(original.Severity, restored.Severity);
        Assert.Equal(original.Text, restored.Text);
    }
}
