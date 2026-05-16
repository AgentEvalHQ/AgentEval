// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentEval.Evals;
using AgentEval.EuAiActBenchmark.Reporting;
using AgentEval.Output;
using Json.Schema;
using Xunit;

namespace AgentEval.Tests.EuAiActBenchmark.Reporting;

/// <summary>
/// Schema validation tests for the embedded <c>eu-ai-act-evidence.schema.json</c> resource
/// and the <see cref="EuAiActComplianceEvidence"/> serialization contract.
/// </summary>
public class EuAiActEvidenceSchemaTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static JsonSchema LoadSchema()
    {
        var asm = typeof(EuAiActComplianceReporter).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(".eu-ai-act-evidence.schema.json",
                StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void SchemaIsLoadable()
    {
        var schema = LoadSchema();
        Assert.NotNull(schema);
    }

    [Fact]
    public void MalformedEvidence_FailsValidation()
    {
        // Construct evidence with an invalid preset value ("banana" is not in the schema enum)
        // and validate directly against the schema to assert IsValid == false.
        var subject = new SubjectIdentity(SubjectKind.Agent, "TestAgent");
        var sourceRun = new SourceRunRef("run-1", "sha256:" + new string('0', 64));

        var atomic = new EvalResult(
            Metric: new("k", "n", "compliance.test", "1.0.0"),
            Score: new(1.0, null, "pass", true, null, "none", null),
            Details: new(null, null, null, null, null),
            Provenance: new("atomic-llm", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        var baseEvidence = new ComplianceEvidence(
            SchemaVersion: "1.0",
            Regulation: "EU-AI-Act",
            Subject: subject,
            GeneratedAt: DateTimeOffset.UtcNow,
            SourceRun: sourceRun,
            Controls: new[] { new EvidenceControl("k", "Test", "PASS", 1.0, new[] { "s1" }, null) },
            Summary: new EvidenceSummary(1, 1, 0, 0, "PASS"),
            Attestation: new Attestation("0.0.0", null, "test", "stub"));

        var malformed = new EuAiActComplianceEvidence(
            Base: baseEvidence,
            Preset: "banana",   // <-- invalid; schema enum rejects
            DomainPacks: Array.Empty<string>(),
            CompositeTree: atomic,
            Summary: new EuAiActSummary(
                OverallScore: 1.0,
                OverallStatus: "PASS",
                PerPillar: new Dictionary<string, EuAiActPillarSummary>(),
                PerArticle: new Dictionary<string, EuAiActArticleSummary>()),
            CriticalFindings: Array.Empty<EvalResult>(),
            Recommendations: Array.Empty<AgentEval.EuAiActBenchmark.Reporting.Recommendation>(),
            Disclaimer: EuAiActComplianceReporter.Disclaimer,
            EuAiActAttestation: new EuAiActAttestation(
                JudgeMode: "mode-a",
                PromptVersions: new Dictionary<string, string>()));

        var json = JsonSerializer.Serialize(malformed, s_jsonOpts);
        var schema = LoadSchema();
        var result = schema.Evaluate(
            JsonNode.Parse(json),
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.False(result.IsValid,
            "Expected schema validation to fail for a 'banana' preset, but it passed.");
    }
}
