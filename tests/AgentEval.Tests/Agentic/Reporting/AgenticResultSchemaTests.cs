// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Reporting;
using AgentEval.Output;
using Json.Schema;
using Xunit;

namespace AgentEval.Tests.Agentic.Reporting;

/// <summary>
/// Schema validation tests for the embedded <c>agentic-result.schema.json</c> resource
/// and the <see cref="AgenticBenchmarkResult"/> serialization contract.
/// </summary>
public class AgenticResultSchemaTests
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
        var asm = typeof(AgenticBenchmarkReporter).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(".agentic-result.schema.json",
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
    public void MalformedResult_FailsValidation()
    {
        // Construct an AgenticBenchmarkResult with an invalid overallStatus ("banana")
        // by constructing the JSON directly, bypassing the C# type system.
        // The schema requires overallStatus to be one of PASS / FAIL / WARN.
        var malformedJson = """
            {
              "preset": "agentic-execution",
              "subject": { "kind": "agent", "name": "TestAgent" },
              "sourceRunId": "run-001",
              "sourceManifestHash": "sha256:0000000000000000000000000000000000000000000000000000000000000000",
              "generatedAt": "2026-01-01T00:00:00Z",
              "compositeTree": {},
              "summary": {
                "overallScore": 0.9,
                "overallStatus": "banana",
                "perCategory": {},
                "perEvaluator": {}
              },
              "criticalFindings": [],
              "recommendations": [],
              "disclaimer": "Test disclaimer text.",
              "attestation": {
                "agentEvalVersion": "0.0.0",
                "judgeMode": "single",
                "promptVersions": {}
              }
            }
            """;

        var schema = LoadSchema();
        var result = schema.Evaluate(
            JsonNode.Parse(malformedJson),
            new EvaluationOptions { OutputFormat = OutputFormat.List });

        Assert.False(result.IsValid,
            "Expected schema validation to fail for 'banana' overallStatus, but it passed.");
    }
}
