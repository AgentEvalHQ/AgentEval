// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

#if NET10_0_OR_GREATER
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Evals;
using AgentEval.MissionControl.Services;
using AgentEval.Output;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Runs;
using AgentEval.Results.Writing;

namespace AgentEval.Tests.MissionControl;

public sealed class AefCapturedLegacyTreeTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("ae-captured-tree-");
    public void Dispose() => _root.Delete(true);
    private static EvalResult Node(string key, params EvalResult[] children) => new(
        new(key, key, "test", "1.0"), new(0.8, null, "pass", true, 0.5, "none", null),
        new(new Dictionary<string, double> { ["dimension"] = 0.7 }, null, ["Recorded recommendation"], children, "Own"),
        new("atomic-llm", "recorded-judge", "prompt", "short-hash", 4, 0.01, false),
        DateTimeOffset.Parse("2026-10-23T10:00:00Z"));

    private async Task<string> Fixture(AefContentCapture capture = AefContentCapture.On, EvaluationInstrumentKind? aggregateInstrument = null)
    {
        var store = new FileSystemOutputStore(Path.Combine(_root.FullName, "store"));
        await store.InitializeSolutionAsync("captured-tree");
        var manifest = await store.StartRunAsync(new(SubjectKind.Agent, "Agent"), new("tests", ".", "offline", null, null, "eval"));
        var tree = Node("root", Node("child", Node("leaf")));
        if (aggregateInstrument is { } instrument)
            tree = tree with
            {
                Provenance = tree.Provenance with { Type = "composite", JudgeModel = null },
                ComparisonObservation = new("root", "1.0") { EffectiveBar = 0.5, InstrumentKind = instrument },
            };
        var scenario = EvalResultPersistence.ToScenarioResult(tree, "case", "Case", []);
        var directory = Path.Combine(_root.FullName, "aef");
        StoreV1Exporter.Export(new(manifest, null, [scenario], null, []), directory, new() { ContentCapture = capture });
        return directory;
    }

    private static EvalResult? Read(string directory)
    {
        var evidence = new AefRunEvidenceReader().Read(directory);
        var root = evidence.Results.Single(r => !r.TryGetProperty("parentResultId", out _));
        return WorkspaceRunProjector.CapturedLegacyTree(new("selected-source", evidence, []), root.GetProperty("resultId").GetString()!);
    }

    private static void MutateCapture(string directory, Action<JsonNode> change)
    {
        var path = Path.Combine(directory, "evidence.ndjson");
        var lines = File.ReadAllLines(path).Select(line => JsonNode.Parse(line)!).ToArray();
        var artifact = lines.Single(n => ((string)n["evidenceId"]!).EndsWith("-eval", StringComparison.Ordinal));
        var oldDigest = ((string)artifact["link"]!["blob"]!)["sha256:".Length..];
        var oldPath = Path.Combine(directory, AefRunFolder.BlobPath(oldDigest));
        var tree = JsonNode.Parse(File.ReadAllBytes(oldPath))!;
        change(tree);
        var bytes = Encoding.UTF8.GetBytes(tree.ToJsonString());
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var newPath = Path.Combine(directory, AefRunFolder.BlobPath(digest));
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        File.WriteAllBytes(newPath, bytes);
        artifact["link"]!["blob"] = "sha256:" + digest;
        artifact["digest"] = "sha256:" + digest;
        File.WriteAllText(path, string.Join("\n", lines.Select(n => n.ToJsonString())) + "\n");
        if (oldPath != newPath) File.Delete(oldPath);
        Reseal(directory);
    }

    private static void Reseal(string directory)
    {
        File.Delete(Path.Combine(directory, "seal.json"));
        AefSealer.Seal(directory, new() { SealedBy = AefSealedBy.Ingest });
        _ = new AefRunEvidenceReader().Read(directory); // Container integrity alone cannot prove semantic agreement.
    }

    [Theory]
    [InlineData("score")]
    [InlineData("state")]
    [InlineData("version")]
    [InlineData("threshold")]
    [InlineData("cost")]
    [InlineData("model")]
    [InlineData("dimension")]
    [InlineData("child-score")]
    [InlineData("child-cost")]
    [InlineData("child-time")]
    [InlineData("missing-child")]
    [InlineData("extra-child")]
    [InlineData("missing-score-fact")]
    [InlineData("missing-name")]
    public async Task Read_ResealedConflictingCapture_RefusesRichPresentation(string mutation)
    {
        var directory = await Fixture();
        MutateCapture(directory, tree =>
        {
            switch (mutation)
            {
                case "score": tree["score"]!["value"] = 0.2; break;
                case "state": tree["score"]!["label"] = "scored"; break;
                case "version": tree["metric"]!["version"] = "2.0"; break;
                case "threshold": tree["score"]!["threshold"] = 0.6; break;
                case "cost": tree["provenance"]!["estimatedCost"] = 0.3; break;
                case "model": tree["provenance"]!["judgeModel"] = "other-judge"; break;
                case "dimension": tree["details"]!["dimensions"]!["dimension"] = 0.3; break;
                case "child-score": tree["details"]!["subResults"]![0]!["score"]!["value"] = 0.3; break;
                case "child-cost": tree["details"]!["subResults"]![0]!["provenance"]!["estimatedCost"] = 0.3; break;
                case "child-time": tree["details"]!["subResults"]![0]!["evaluatedAt"] = "2026-10-23T11:00:00Z"; break;
                case "missing-child": tree["details"]!["subResults"]!.AsArray().Clear(); break;
                case "extra-child": tree["details"]!["subResults"]!.AsArray().Add(tree["details"]!["subResults"]![0]!.DeepClone()); break;
                case "missing-score-fact": tree["score"]!.AsObject().Remove("passed"); break;
                case "missing-name": tree["metric"]!.AsObject().Remove("name"); break;
            }
        });
        Assert.Throws<InvalidDataException>(() => Read(directory));
    }

    [Fact]
    public async Task Read_UnmodifiedCapturedTree_RetainsNestedDetails()
    {
        var result = Assert.IsType<EvalResult>(Read(await Fixture()));
        Assert.Equal("Recorded recommendation", Assert.Single(result.Details.Recommendations!));
        Assert.Equal("leaf", Assert.Single(Assert.Single(result.Details.SubResults!).Details.SubResults!).Metric.Key);
    }

    [Theory]
    [InlineData(EvaluationInstrumentKind.Deterministic)]
    [InlineData(EvaluationInstrumentKind.Model)]
    [InlineData(EvaluationInstrumentKind.Unknown)]
    public async Task Read_AggregateInstrumentRecordedOutsideCapture_RetainsTree(EvaluationInstrumentKind instrument)
    {
        var directory = await Fixture(aggregateInstrument: instrument);
        var result = Assert.IsType<EvalResult>(Read(directory));
        Assert.Equal("composite", result.Provenance.Type);
        Assert.Null(result.Provenance.JudgeModel);
        Assert.Equal("child", Assert.Single(result.Details.SubResults!).Metric.Key);
    }

    [Theory]
    [InlineData("instrumentKind", "model")]
    [InlineData("instrumentKind", "unsupported")]
    [InlineData("evalKey", "other")]
    [InlineData("evalVersion", "2.0")]
    [InlineData("effectiveBar", "0.6")]
    public async Task Read_ConflictingRecordedAggregateFacts_RefusesRichPresentation(string field, string value)
    {
        var directory = await Fixture(aggregateInstrument: EvaluationInstrumentKind.Deterministic);
        var path = Path.Combine(directory, "results.ndjson");
        var lines = File.ReadAllLines(path).Select(line => JsonNode.Parse(line)!).ToArray();
        var facts = lines.Single(n => n["parentResultId"] is null)["ext"]![StoreV1Exporter.ExtName]!["comparability"]!;
        facts[field] = field == "effectiveBar" ? JsonValue.Create(0.6) : JsonValue.Create(value);
        File.WriteAllText(path, string.Join("\n", lines.Select(n => n.ToJsonString())) + "\n");
        Reseal(directory);
        Assert.Throws<InvalidDataException>(() => Read(directory));
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("direction")]
    [InlineData("scale")]
    [InlineData("unit")]
    public async Task Read_ConflictingDescendantMetricDefinition_RefusesRichPresentation(string field)
    {
        var directory = await Fixture();
        var path = Path.Combine(directory, "metrics.json");
        var metrics = JsonNode.Parse(File.ReadAllText(path))!;
        var definition = metrics["metrics"]!.AsArray().Single(m => ((string)m!["id"]!).StartsWith("agenteval.eval.score", StringComparison.Ordinal))!;
        switch (field)
        {
            case "kind": definition["kind"] = "rate"; break;
            case "direction": definition["direction"] = "lower_better"; break;
            case "scale": definition["scale"]!["max"] = 100; break;
            case "unit": definition["unit"] = "ms"; break;
        }
        File.WriteAllText(path, metrics.ToJsonString());
        Reseal(directory);
        Assert.Throws<InvalidDataException>(() => Read(directory));
    }

    [Fact]
    public async Task Read_CaptureOff_ReturnsNoCapturedTree() => Assert.Null(Read(await Fixture(AefContentCapture.Off)));

    [Fact]
    public async Task Read_OlderImportWithoutCanonicalDescendants_RetainsCapturedStructure()
    {
        var directory = await Fixture();
        var path = Path.Combine(directory, "results.ndjson");
        var root = File.ReadAllLines(path).Select(line => JsonNode.Parse(line)!).Single(n => n["parentResultId"] is null);
        root["ext"]![StoreV1Exporter.ExtName]!.AsObject().Remove("evalTreeProjectionVersion");
        root.AsObject().Remove("aggregation");
        File.WriteAllText(path, root.ToJsonString() + "\n");
        Reseal(directory);
        Assert.Equal("child", Assert.Single(Read(directory)!.Details.SubResults!).Metric.Key);
    }

    [Fact]
    public async Task Read_UnknownProjectionVersion_RefusesUnsupportedInterpretation()
    {
        var directory = await Fixture();
        var path = Path.Combine(directory, "results.ndjson");
        var lines = File.ReadAllLines(path).Select(line => JsonNode.Parse(line)!).ToArray();
        lines.Single(n => n["parentResultId"] is null)["ext"]![StoreV1Exporter.ExtName]!["evalTreeProjectionVersion"] = 2;
        File.WriteAllText(path, string.Join("\n", lines.Select(n => n.ToJsonString())) + "\n");
        Reseal(directory);
        Assert.Throws<InvalidDataException>(() => Read(directory));
    }
}
#endif
