// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Output;
using Json.Schema;
using SchemaEvalOpts = Json.Schema.EvaluationOptions;

namespace AgentEval.Tests.Output;

public class SchemaValidationTests
{
    [Fact]
    public async Task Manifest_File_Validates_Against_Schema()
    {
        using var temp = TempWorkspace.Create("SchemaTest");
        var store = new FileSystemOutputStore(temp.Path);
        var subject = new SubjectIdentity(SubjectKind.Agent, "TravelAgent");
        await store.EnsureSubjectAsync(subject);
        var ctx = new RunContext("Sample.Evals", "path", "TestHarness", null, null, "eval");
        var manifest = await store.StartRunAsync(subject, ctx);
        await store.CompleteRunAsync(manifest, MakeSummaryFixture("PASS", manifest.Run.RunId));

        var manifestPath = Path.Combine(
            temp.Path, "subjects", "agents", "TravelAgent",
            "runs", manifest.Run.RunId, "manifest.json");
        var json = await File.ReadAllTextAsync(manifestPath);

        var schema = LoadEmbeddedSchema("manifest.schema.json");
        var result = schema.Evaluate(
            JsonNode.Parse(json),
            new SchemaEvalOpts { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public async Task Summary_File_Validates_Against_Schema()
    {
        using var temp = TempWorkspace.Create("SchemaTest");
        var store = new FileSystemOutputStore(temp.Path);
        var subject = new SubjectIdentity(SubjectKind.Agent, "TravelAgent");
        await store.EnsureSubjectAsync(subject);
        var ctx = new RunContext("Sample.Evals", "path", "TestHarness", null, null, "eval");
        var manifest = await store.StartRunAsync(subject, ctx);
        await store.CompleteRunAsync(manifest, MakeSummaryFixture("PASS", manifest.Run.RunId));

        var summaryPath = Path.Combine(
            temp.Path, "subjects", "agents", "TravelAgent",
            "runs", manifest.Run.RunId, "summary.json");
        var json = await File.ReadAllTextAsync(summaryPath);

        var schema = LoadEmbeddedSchema("summary.schema.json");
        var result = schema.Evaluate(
            JsonNode.Parse(json),
            new SchemaEvalOpts { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public async Task Subject_File_Validates_Against_Schema()
    {
        using var temp = TempWorkspace.Create("SchemaTest");
        var store = new FileSystemOutputStore(temp.Path);
        var subject = new SubjectIdentity(SubjectKind.Agent, "TravelAgent");
        await store.EnsureSubjectAsync(subject);

        var subjectPath = Path.Combine(
            temp.Path, "subjects", "agents", "TravelAgent", "subject.json");
        var json = await File.ReadAllTextAsync(subjectPath);

        var schema = LoadEmbeddedSchema("subject.schema.json");
        var result = schema.Evaluate(
            JsonNode.Parse(json),
            new SchemaEvalOpts { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public async Task Solution_File_Validates_Against_Schema()
    {
        using var temp = TempWorkspace.Create("MySolution");
        var solutionPath = Path.Combine(temp.Path, "solution.json");
        var json = await File.ReadAllTextAsync(solutionPath);

        var schema = LoadEmbeddedSchema("solution.schema.json");
        var result = schema.Evaluate(
            JsonNode.Parse(json),
            new SchemaEvalOpts { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void HistoryLine_HandwrittenFixture_ValidatesAgainstSchema()
    {
        var json = """
        {
          "runId": "2026-05-08_10-00-00_abcd1234",
          "timestamp": "2026-05-08T10:00:00Z",
          "verdict": "PASS",
          "score": 0.95,
          "notes": null
        }
        """;
        var schema = LoadEmbeddedSchema("history-line.schema.json");
        var result = schema.Evaluate(
            JsonNode.Parse(json),
            new SchemaEvalOpts { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void Evidence_HandwrittenFixture_ValidatesAgainstSchema()
    {
        var json = """
        {
          "schemaVersion": "1.0",
          "regulation": "SOC2",
          "subject": { "kind": "agent", "name": "TravelAgent" },
          "generatedAt": "2026-05-08T12:00:00Z",
          "sourceRun": {
            "runId": "2026-05-08_10-00-00_abcd1234",
            "manifestHash": "sha256:0000000000000000000000000000000000000000000000000000000000000000"
          },
          "controls": [
            {
              "id": "CC6.1",
              "title": "Logical Access",
              "status": "PASS",
              "passRate": 1.0,
              "scenarioRefs": ["scenario1"],
              "notes": null
            }
          ],
          "summary": {
            "controlsTotal": 1,
            "passed": 1,
            "warnings": 0,
            "failed": 0,
            "overallStatus": "PASS"
          },
          "attestation": {
            "agentEvalVersion": "0.8.1",
            "configurationId": null,
            "evaluator": "automated",
            "evaluatorModel": "gpt-4o"
          }
        }
        """;
        var schema = LoadEmbeddedSchema("evidence.schema.json");
        var result = schema.Evaluate(
            JsonNode.Parse(json),
            new SchemaEvalOpts { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void RedTeamManifest_HandwrittenFixture_ValidatesAgainstSchema()
    {
        var json = """
        {
          "schemaVersion": "1.0",
          "campaignId": "demo_2026-05-08_12-00-00",
          "name": "demo",
          "startedAt": "2026-05-08T12:00:00Z",
          "targets": [{ "kind": "agent", "name": "TravelAgent" }],
          "mode": "lite"
        }
        """;
        var schema = LoadEmbeddedSchema("red-team-manifest.schema.json");
        var result = schema.Evaluate(
            JsonNode.Parse(json),
            new SchemaEvalOpts { OutputFormat = OutputFormat.List });
        Assert.True(result.IsValid, FormatErrors(result));
    }

    private static JsonSchema LoadEmbeddedSchema(string fileName)
    {
        var asm = typeof(FileSystemOutputStore).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    private static string FormatErrors(EvaluationResults r)
        => string.Join("\n", r.Details
            .Where(d => !d.IsValid)
            .Select(d => $"{d.EvaluationPath}: {string.Join(", ", d.Errors?.Select(e => $"{e.Key}={e.Value}") ?? [])}"));

    private static RunSummary MakeSummaryFixture(string verdict, string runId) =>
        new(
            SchemaVersion: "1.0",
            RunId: runId,
            Verdict: verdict,
            Stats: new RunStats(1, 1, 0, 0),
            Metrics: new Dictionary<string, double> { ["accuracy"] = 1.0 });
}
