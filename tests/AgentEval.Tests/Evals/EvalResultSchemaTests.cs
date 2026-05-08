// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Evals;
using AgentEval.Output;
using Json.Schema;
using SchemaEvalOpts = Json.Schema.EvaluationOptions;

namespace AgentEval.Tests.Evals;

public class EvalResultSchemaTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static JsonSchema LoadSchema()
    {
        var asm = typeof(FileSystemOutputStore).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(".eval-result.schema.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    private static EvaluationResults Evaluate(string json)
    {
        var schema = LoadSchema();
        return schema.Evaluate(
            JsonNode.Parse(json),
            new SchemaEvalOpts { OutputFormat = OutputFormat.List });
    }

    private static string FormatErrors(EvaluationResults r) =>
        string.Join("\n", r.Details
            .Where(d => !d.IsValid)
            .Select(d => $"{d.EvaluationPath}: {string.Join(", ", d.Errors?.Select(e => $"{e.Key}={e.Value}") ?? [])}"));

    private const string PassResultJson = """
        {
          "metric": {
            "key": "art17_acknowledgment",
            "name": "Acknowledgment",
            "category": "compliance.gdpr",
            "version": "1.0.0"
          },
          "score": {
            "value": 0.95,
            "label": "pass",
            "passed": true,
            "severity": "none",
            "confidence": 0.92
          },
          "details": {
            "dimensions": { "clarity": 0.95 },
            "evidence": [
              { "source": "response", "reference": "We confirm receipt.", "message": "Acknowledgment is explicit." }
            ],
            "recommendations": null,
            "subResults": null,
            "aggregationStrategy": null
          },
          "provenance": {
            "type": "atomic-llm",
            "judgeModel": "gpt-4o",
            "promptId": "ack.v1",
            "estimatedCost": 0.0015,
            "cacheHit": false
          },
          "evaluatedAt": "2026-05-08T14:30:00Z"
        }
        """;

    private const string FailResultJson = """
        {
          "metric": {
            "key": "art17_deletion",
            "name": "Deletion",
            "category": "compliance.gdpr",
            "version": "2.0.0"
          },
          "score": {
            "value": 0.1,
            "label": "fail",
            "passed": false,
            "severity": "high"
          },
          "details": {
            "recommendations": ["Implement deletion workflow."]
          },
          "provenance": {
            "type": "atomic-code",
            "estimatedCost": 0.0,
            "cacheHit": true
          },
          "evaluatedAt": "2026-05-08T14:31:00Z"
        }
        """;

    private const string SkippedResultJson = """
        {
          "metric": {
            "key": "optional_check",
            "name": "Optional Check",
            "category": "quality",
            "version": "1.0.0"
          },
          "score": {
            "value": 0,
            "label": "skipped",
            "passed": false,
            "severity": "none"
          },
          "details": {
            "recommendations": ["Dependency not available."]
          },
          "provenance": {
            "type": "skipped",
            "estimatedCost": 0.0,
            "cacheHit": false
          },
          "evaluatedAt": "2026-05-08T14:32:00Z"
        }
        """;

    private const string NestedCompositeJson = """
        {
          "metric": {
            "key": "gdpr_composite",
            "name": "GDPR Composite",
            "category": "compliance.gdpr",
            "version": "1.0.0"
          },
          "score": {
            "value": 0.75,
            "label": "warn",
            "passed": false,
            "severity": "medium"
          },
          "details": {
            "subResults": [
              {
                "metric": {
                  "key": "art17_acknowledgment",
                  "name": "Acknowledgment",
                  "category": "compliance.gdpr",
                  "version": "1.0.0"
                },
                "score": {
                  "value": 0.95,
                  "label": "pass",
                  "passed": true,
                  "severity": "none"
                },
                "details": {},
                "provenance": {
                  "type": "atomic-llm",
                  "estimatedCost": 0.001,
                  "cacheHit": false
                },
                "evaluatedAt": "2026-05-08T14:30:00Z"
              },
              {
                "metric": {
                  "key": "art17_deletion",
                  "name": "Deletion",
                  "category": "compliance.gdpr",
                  "version": "1.0.0"
                },
                "score": {
                  "value": 0.55,
                  "label": "warn",
                  "passed": false,
                  "severity": "medium"
                },
                "details": {},
                "provenance": {
                  "type": "atomic-code",
                  "estimatedCost": 0.0,
                  "cacheHit": true
                },
                "evaluatedAt": "2026-05-08T14:30:01Z"
              }
            ],
            "aggregationStrategy": "WeightedSum"
          },
          "provenance": {
            "type": "composite",
            "estimatedCost": 0.001,
            "cacheHit": false
          },
          "evaluatedAt": "2026-05-08T14:30:02Z"
        }
        """;

    private const string MalformedResultJson = """
        {
          "metric": {
            "key": "bad",
            "name": "Bad",
            "category": "test",
            "version": "1.0.0"
          },
          "score": {
            "value": 1.5,
            "label": "invalid",
            "passed": true,
            "severity": "none"
          },
          "details": {},
          "provenance": {
            "type": "atomic-code",
            "estimatedCost": 0.0,
            "cacheHit": false
          },
          "evaluatedAt": "2026-05-08T14:30:00Z"
        }
        """;

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void PassResult_ValidatesAgainstSchema()
    {
        var result = Evaluate(PassResultJson);
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void FailResult_ValidatesAgainstSchema()
    {
        var result = Evaluate(FailResultJson);
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void SkippedResult_ValidatesAgainstSchema()
    {
        // Regression test: EvalResult.Skipped now uses provenance.type = "skipped"
        // which must be accepted by the schema's enum (["atomic-llm", "atomic-code", "composite", "skipped"]).
        var result = Evaluate(SkippedResultJson);
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void NestedCompositeResult_SubResultsValidateRecursively()
    {
        // Verifies that the schema's $ref: "#" in subResults allows full nested EvalResult validation.
        var result = Evaluate(NestedCompositeJson);
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void MalformedResult_ScoreValueAboveOne_LabelInvalid_FailsValidation()
    {
        // score.value = 1.5 (> 1.0) and label = "invalid" (not in enum) should both fail.
        var result = Evaluate(MalformedResultJson);
        Assert.False(result.IsValid);
    }
}
