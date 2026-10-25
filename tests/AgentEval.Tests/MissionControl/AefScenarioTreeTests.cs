// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

#if NET10_0_OR_GREATER
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentEval.Evals;
using AgentEval.MissionControl.GraphQL;
using AgentEval.Output;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Writing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentEval.Tests.MissionControl;

public sealed class AefScenarioTreeTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("ae-mc-tree-");
    public void Dispose() => _root.Delete(recursive: true);
    private string Runs => Path.Combine(_root.FullName, ".agenteval", "aef", "runs");
    private WebApplicationFactory<Query> Factory() => new WebApplicationFactory<Query>().WithWebHostBuilder(b =>
    {
        LoopbackTestConnection.Configure(b);
        b.UseSetting("AgentEval:Root", _root.FullName);
        b.UseSetting("AgentEval:Queries:Enabled", "false");
    });
    private static EvalResult Node(string key, params EvalResult[] children) => new(
        new(key, key, "test", "1.0"), new(0.8, null, "pass", true, 0.5, "none", null),
        new(null, null, null, children.Length == 0 ? null : children, null),
        new("code", null, null, null, null, 0, false), DateTimeOffset.Parse("2026-10-23T10:00:00Z"));
    private string Native(string subjectRef = "agent:tree")
    {
        var tree = Node("preset", Node("case", Node("repeated"), Node("repeated", Node("deep", Node("deeper", Node("leaf"))))));
        return AefCompositeRunWriter.WriteTrials(Runs, tree, [tree, tree], AefCompositeRunWriter.Undescribed,
            new() { SubjectRef = subjectRef, TargetMode = AefTargetMode.Mocked, Lane = "test", SuiteRef = "bench:tree", ContentCapture = AefContentCapture.Off },
            tree.EvaluatedAt, tree.EvaluatedAt.AddSeconds(1)).Directory;
    }
    private static async Task<JsonDocument> Send(HttpClient client, string query, object? variables = null)
    {
        using var response = await client.PostAsJsonAsync("/graphql", new { query, variables });
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
    private const string TreeQuery = """
        query Tree($runId: String!, $sourceId: String, $resultId: String) {
          scenarioResultTree(runId:$runId,scenarioId:"case",sourceId:$sourceId,resultId:$resultId) {
            sourceId runId caseId selectedResultId contentCapture rootResultIds
            evidence { integrity imported }
            metrics { id kind direction scale minimum maximum unit }
            artifacts { evidenceId captured }
            nodes { resultId parentResultId caseId path trial evaluatorId evaluatorVersion state
              startedAt endedAt durationMs reason severity
              scores { metricId value normalized label }
              trials { n passed k }
              aggregation { measured total unmeasured { state count } }
              usage { inputTokens outputTokens }
              recordedJson capturedLegacyJson
            }
          }
        }
        """;

    [Fact]
    public async Task Timeline_NativeCaptureOff_RetainsAllRecordedOccurrencesAndTrialIdentity()
    {
        var directory = Native();
        var read = new AefRunEvidenceReader().Read(directory);
        var matches = read.Results.Where(r => r.GetProperty("evaluator").GetProperty("id").GetString() == "repeated").ToArray();
        Assert.True(matches.Length > 1);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var compatible = await Send(client, "{ evaluatorTimeline(evaluatorKey:\"repeated\",count:100) { runId score } }");
        Assert.False(compatible.RootElement.TryGetProperty("errors", out _), compatible.RootElement.ToString());
        Assert.Equal(matches.Length, compatible.RootElement.GetProperty("data").GetProperty("evaluatorTimeline").GetArrayLength());
        using var json = await Send(client, """
            { evaluatorTimeline(evaluatorKey:"repeated",count:100) {
              sourceId runId resultId caseId path trial evaluatorVersion state timestamp passed score scoreMetricId
              scores { metricId value } metrics { id kind direction scale minimum maximum unit }
              evidence { integrity imported }
            } }
            """);
        Assert.False(json.RootElement.TryGetProperty("errors", out _), json.RootElement.ToString());
        var points = json.RootElement.GetProperty("data").GetProperty("evaluatorTimeline").EnumerateArray().ToArray();
        Assert.Equal(matches.Length, points.Length);
        Assert.Contains(points, p => p.GetProperty("trial").ValueKind == JsonValueKind.Number);
        foreach (var point in points)
        {
            Assert.Equal(read.Info.RunId, point.GetProperty("runId").GetString());
            Assert.StartsWith("source-", point.GetProperty("sourceId").GetString());
            Assert.Contains(matches, r => r.GetProperty("resultId").GetString() == point.GetProperty("resultId").GetString());
            Assert.Equal("intact", point.GetProperty("evidence").GetProperty("integrity").GetString());
        }
    }

    private static void Reseal(string directory)
    {
        File.Delete(Path.Combine(directory, "seal.json"));
        AefSealer.Seal(directory, new() { SealedBy = AefSealedBy.Ingest });
        _ = new AefRunEvidenceReader().Read(directory);
    }

    [Fact]
    public async Task Timeline_UnboundedMetricAndModelSubject_KeepsRawMeaning()
    {
        var directory = Native("model:tree");
        var path = Path.Combine(directory, "metrics.json");
        var metrics = JsonNode.Parse(File.ReadAllText(path))!;
        var score = metrics["metrics"]!.AsArray().Single(m => (string?)m!["id"] == "score")!;
        score["scale"] = "unbounded";
        score["direction"] = "none";
        score["unit"] = "ms";
        File.WriteAllText(path, metrics.ToJsonString());
        Reseal(directory);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var json = await Send(client, """
            { evaluatorTimeline(evaluatorKey:"repeated") { score subjectKind subjectRef scores { value metricId } metrics { id scale unit } } }
            """);
        Assert.False(json.RootElement.TryGetProperty("errors", out _), json.RootElement.ToString());
        var points = json.RootElement.GetProperty("data").GetProperty("evaluatorTimeline").EnumerateArray().ToArray();
        Assert.NotEmpty(points);
        foreach (var point in points)
        {
            Assert.Equal(JsonValueKind.Null, point.GetProperty("score").ValueKind);
            Assert.Equal("MODEL", point.GetProperty("subjectKind").GetString());
            Assert.Equal("model:tree", point.GetProperty("subjectRef").GetString());
            Assert.Equal(0.8, Assert.Single(point.GetProperty("scores").EnumerateArray()).GetProperty("value").GetDouble());
            Assert.Equal("ms", Assert.Single(point.GetProperty("metrics").EnumerateArray()).GetProperty("unit").GetString());
        }
    }

    [Fact]
    public async Task Timeline_ImporterAssertedStart_RemainsUnknown()
    {
        var directory = Native();
        var path = Path.Combine(directory, "run.json");
        var header = JsonNode.Parse(File.ReadAllText(path))!;
        header["imported"] = new JsonObject { ["from"] = "test importer", ["asserted"] = new JsonArray("startedAt") };
        File.WriteAllText(path, header.ToJsonString());
        Reseal(directory);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var json = await Send(client, "{ evaluatorTimeline(evaluatorKey:\"repeated\") { timestamp evidence { imported } } }");
        Assert.False(json.RootElement.TryGetProperty("errors", out _), json.RootElement.ToString());
        var points = json.RootElement.GetProperty("data").GetProperty("evaluatorTimeline").EnumerateArray().ToArray();
        Assert.NotEmpty(points);
        Assert.All(points, p => Assert.Equal(JsonValueKind.Null, p.GetProperty("timestamp").ValueKind));
    }

    [Fact]
    public async Task Timeline_TamperedSource_RefusesBeforeSelection()
    {
        var directory = Native();
        File.AppendAllText(Path.Combine(directory, "run.json"), " ");
        using var factory = Factory(); using var client = factory.CreateClient();
        using var json = await Send(client, "{ evaluatorTimeline(evaluatorKey:\"repeated\") { runId } }");
        Assert.True(json.RootElement.TryGetProperty("errors", out _));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 0)]
    [InlineData(1, 1)]
    [InlineData(int.MaxValue, 200)]
    public async Task Timeline_CountBounds_ApplyToRecordedOccurrences(int count, int expected)
    {
        var tree = Node("preset", Node("case", Enumerable.Range(0, 201).Select(_ => Node("many")).ToArray()));
        AefCompositeRunWriter.Write(Runs, tree, AefCompositeRunWriter.Undescribed,
            new() { SubjectRef = "agent:many", TargetMode = AefTargetMode.Mocked, Lane = "test", SuiteRef = "bench:test", ContentCapture = AefContentCapture.Off },
            tree.EvaluatedAt, tree.EvaluatedAt.AddSeconds(1));
        using var factory = Factory(); using var client = factory.CreateClient();
        using var json = await Send(client, "query T($n:Int!) { evaluatorTimeline(evaluatorKey:\"many\",count:$n) { resultId } }", new { n = count });
        Assert.False(json.RootElement.TryGetProperty("errors", out _), json.RootElement.ToString());
        Assert.Equal(expected, json.RootElement.GetProperty("data").GetProperty("evaluatorTimeline").GetArrayLength());
    }

    [Theory]
    [InlineData(AefContentCapture.Off)]
    [InlineData(AefContentCapture.On)]
    public async Task Tree_ConvertedLegacy_DisplaysCanonicalDescendantsUnderEitherCapturePolicy(AefContentCapture capture)
    {
        var store = new FileSystemOutputStore(Path.Combine(_root.FullName, "fixture-store"));
        await store.InitializeSolutionAsync("tree-fixture");
        var manifest = await store.StartRunAsync(new(SubjectKind.Agent, "TreeAgent"), new("tests", ".", "offline", null, null, "eval"));
        var absent = Node("absent") with { Score = EvalScore.NotApplicable() };
        var tree = Node("root", Node("branch", Node("repeated"), absent));
        var scenario = EvalResultPersistence.ToScenarioResult(tree, "case", "Legacy case", []);
        var directory = Path.Combine(Runs, "legacy");
        StoreV1Exporter.Export(new(manifest, null, [scenario], null, []), directory, new() { ContentCapture = capture });
        var read = new AefRunEvidenceReader().Read(directory);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var json = await Send(client, TreeQuery, new { runId = read.Info.RunId });
        Assert.False(json.RootElement.TryGetProperty("errors", out _), json.RootElement.ToString());
        var result = json.RootElement.GetProperty("data").GetProperty("scenarioResultTree");
        Assert.True(result.GetProperty("evidence").GetProperty("imported").GetBoolean());
        var nodes = result.GetProperty("nodes").EnumerateArray().ToArray();
        Assert.Equal(4, nodes.Length);
        var capturedRoot = Assert.Single(nodes, n => n.GetProperty("parentResultId").ValueKind == JsonValueKind.Null).GetProperty("capturedLegacyJson");
        Assert.Equal(capture == AefContentCapture.On ? JsonValueKind.String : JsonValueKind.Null, capturedRoot.ValueKind);
        if (capture == AefContentCapture.On)
        {
            using var rich = JsonDocument.Parse(capturedRoot.GetString()!);
            Assert.Equal("root", rich.RootElement.GetProperty("metric").GetProperty("key").GetString());
            Assert.Equal("branch", rich.RootElement.GetProperty("details").GetProperty("subResults")[0].GetProperty("metric").GetProperty("key").GetString());
        }
        var missing = Assert.Single(nodes, n => n.GetProperty("evaluatorId").GetString() == "absent");
        Assert.Equal("not_applicable", missing.GetProperty("state").GetString());
        Assert.Empty(missing.GetProperty("scores").EnumerateArray());
        Assert.Equal("scenario/eval/1/2", missing.GetProperty("path").GetString());
        var captured = result.GetProperty("artifacts").EnumerateArray().Where(a => a.GetProperty("captured").GetBoolean()).ToArray();
        Assert.Equal(capture == AefContentCapture.On, captured.Length > 0);
        foreach (var node in nodes)
            Assert.Contains(read.Results, original => original.GetProperty("resultId").GetString() == node.GetProperty("resultId").GetString());
        const string legacyQuery = """
            query Legacy($runId:String!, $sourceId:String!, $resultId:String!) {
              scenarioTree(runId:$runId,scenarioId:"case",sourceId:$sourceId,resultId:$resultId) { metric { key } }
            }
            """;
        var sourceId = result.GetProperty("sourceId").GetString()!;
        var resultId = Assert.Single(nodes, n => n.GetProperty("parentResultId").ValueKind == JsonValueKind.Null).GetProperty("resultId").GetString()!;
        using var legacy = await Send(client, legacyQuery, new { runId = read.Info.RunId, sourceId, resultId });
        Assert.False(legacy.RootElement.TryGetProperty("errors", out _), legacy.RootElement.ToString());
        var legacyTree = legacy.RootElement.GetProperty("data").GetProperty("scenarioTree");
        if (capture == AefContentCapture.On) Assert.Equal("root", legacyTree.GetProperty("metric").GetProperty("key").GetString());
        else Assert.Equal(JsonValueKind.Null, legacyTree.ValueKind);
        foreach (var selection in new[] { new { sourceId, resultId = "unknown" }, new { sourceId = "unknown", resultId } })
        {
            using var unknown = await Send(client, legacyQuery, new { runId = read.Info.RunId, selection.sourceId, selection.resultId });
            Assert.False(unknown.RootElement.TryGetProperty("errors", out _), unknown.RootElement.ToString());
            Assert.Equal(JsonValueKind.Null, unknown.RootElement.GetProperty("data").GetProperty("scenarioTree").ValueKind);
        }
    }

    [Fact]
    public async Task Tree_ModifiedSealedSource_RefusesInsteadOfDisplayingUnverifiedNodes()
    {
        var directory = Native(); var read = new AefRunEvidenceReader().Read(directory);
        File.AppendAllText(Path.Combine(directory, "run.json"), " ");
        using var factory = Factory(); using var client = factory.CreateClient();
        using var json = await Send(client, TreeQuery, new { runId = read.Info.RunId });
        Assert.True(json.RootElement.TryGetProperty("errors", out _));
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("data").GetProperty("scenarioResultTree").ValueKind);
    }

    [Theory]
    [InlineData(".0")]
    [InlineData("e0")]
    public async Task Tree_IntegralJsonNumberSpellings_RetainsVerifiedCounts(string suffix)
    {
        var directory = Native();
        var path = Path.Combine(directory, "results.ndjson");
        var lines = File.ReadAllLines(path).Select(line =>
        {
            var node = JsonNode.Parse(line)!;
            node["usage"] = new JsonArray(new JsonObject
            {
                ["role"] = "judge", ["gen_ai.usage.input_tokens"] = 9007199254740991L,
                ["gen_ai.usage.output_tokens"] = 2
            });
            return Regex.Replace(node.ToJsonString(),
                "(\\\"(?:trial|n|passed|measured|total|gen_ai\\.usage\\.(?:input|output)_tokens)\\\"\\s*:\\s*)([0-9]+)(?=[,}])",
                match => match.Groups[1].Value + match.Groups[2].Value + suffix);
        }).ToArray();
        Assert.Contains("9007199254740991" + suffix, lines[0]);
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        File.Delete(Path.Combine(directory, "seal.json"));
        AefSealer.Seal(directory);
        var read = new AefRunEvidenceReader().Read(directory);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var json = await Send(client, TreeQuery, new { runId = read.Info.RunId });
        Assert.False(json.RootElement.TryGetProperty("errors", out _), json.RootElement.ToString());
        var nodes = json.RootElement.GetProperty("data").GetProperty("scenarioResultTree").GetProperty("nodes");
        foreach (var node in nodes.EnumerateArray())
        {
            Assert.Equal(9007199254740991L, node.GetProperty("usage")[0].GetProperty("inputTokens").GetInt64());
            Assert.Equal(2, node.GetProperty("usage")[0].GetProperty("outputTokens").GetInt64());
        }
        Assert.Contains(nodes.EnumerateArray(), node => node.GetProperty("trial").ValueKind == JsonValueKind.Number);
        Assert.Contains(nodes.EnumerateArray(), node => node.GetProperty("trials").ValueKind == JsonValueKind.Object);
    }

    [Fact]
    public async Task Tree_UnfamiliarAbsenceMetadata_PreservesRawFieldWithoutInventingCount()
    {
        var directory = Native();
        var path = Path.Combine(directory, "results.ndjson");
        var changed = 0;
        var lines = File.ReadAllLines(path).Select(line =>
        {
            var node = JsonNode.Parse(line)!;
            if (node["aggregation"] is JsonObject aggregation)
            {
                aggregation["unmeasured"] = new JsonObject { ["not_measured"] = 0, ["futureNote"] = "x" };
                changed++;
            }
            return node.ToJsonString();
        }).ToArray();
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        Assert.True(changed > 0);
        File.Delete(Path.Combine(directory, "seal.json"));
        AefSealer.Seal(directory);
        var read = new AefRunEvidenceReader().Read(directory);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var json = await Send(client, TreeQuery, new { runId = read.Info.RunId });
        Assert.False(json.RootElement.TryGetProperty("errors", out _), json.RootElement.ToString());
        foreach (var node in json.RootElement.GetProperty("data").GetProperty("scenarioResultTree").GetProperty("nodes").EnumerateArray())
        {
            if (node.GetProperty("aggregation").ValueKind == JsonValueKind.Null) continue;
            var counts = node.GetProperty("aggregation").GetProperty("unmeasured").EnumerateArray().ToArray();
            Assert.Single(counts);
            Assert.Equal("not_measured", counts[0].GetProperty("state").GetString());
            Assert.Equal(0, counts[0].GetProperty("count").GetInt64());
            using var raw = JsonDocument.Parse(node.GetProperty("recordedJson").GetString()!);
            Assert.Equal("x", raw.RootElement.GetProperty("aggregation").GetProperty("unmeasured").GetProperty("futureNote").GetString());
        }
    }

    [Fact]
    public async Task Tree_NativeTrialsAndDeepHierarchy_PreservesEveryRecordedNode()
    {
        var read = new AefRunEvidenceReader().Read(Native());
        using var factory = Factory(); using var client = factory.CreateClient();
        using var json = await Send(client, TreeQuery, new { runId = read.Info.RunId });
        Assert.False(json.RootElement.TryGetProperty("errors", out _), json.RootElement.ToString());
        var tree = json.RootElement.GetProperty("data").GetProperty("scenarioResultTree");
        Assert.Equal("off", tree.GetProperty("contentCapture").GetString());
        Assert.Equal(3, tree.GetProperty("rootResultIds").GetArrayLength());
        var nodes = tree.GetProperty("nodes").EnumerateArray().ToArray();
        Assert.Equal(read.Results.Count, nodes.Length);
        for (var i = 0; i < nodes.Length; i++)
        {
            var original = read.Results[i];
            var node = nodes[i];
            Assert.Equal(original.GetProperty("resultId").GetString(), node.GetProperty("resultId").GetString());
            Assert.Equal(original.GetProperty("path").GetString(), node.GetProperty("path").GetString());
            Assert.Equal(original.TryGetProperty("parentResultId", out var parent) ? parent.GetString() : null,
                node.GetProperty("parentResultId").GetString());
            Assert.Equal(original.TryGetProperty("trial", out var trial) ? trial.GetInt32() : (int?)null,
                node.GetProperty("trial").ValueKind == JsonValueKind.Null ? null : node.GetProperty("trial").GetInt32());
            using var recorded = JsonDocument.Parse(node.GetProperty("recordedJson").GetString()!);
            Assert.Equal(original.GetRawText(), recorded.RootElement.GetRawText());
            Assert.Equal(JsonValueKind.Null, node.GetProperty("durationMs").ValueKind);
        }
        Assert.Equal(6, nodes.Count(n => n.GetProperty("evaluatorId").GetString() == "repeated"));
    }

    [Fact]
    public async Task Tree_DuplicateRunIds_RequiresSourceAndValidatesSelectedResult()
    {
        var directory = Native(); var read = new AefRunEvidenceReader().Read(directory);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(Runs, "duplicate", Path.GetRelativePath(directory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
        using var factory = Factory(); using var client = factory.CreateClient();
        using var ambiguous = await Send(client, TreeQuery, new { runId = read.Info.RunId });
        Assert.True(ambiguous.RootElement.TryGetProperty("errors", out _));
        using var unknownSource = await Send(client, TreeQuery, new { runId = read.Info.RunId, sourceId = "unknown-source" });
        Assert.Equal(JsonValueKind.Null, unknownSource.RootElement.GetProperty("data").GetProperty("scenarioResultTree").ValueKind);
        using var list = await Send(client, "{ recentRuns { sourceId } }");
        foreach (var item in list.RootElement.GetProperty("data").GetProperty("recentRuns").EnumerateArray())
        {
            var sourceId = item.GetProperty("sourceId").GetString();
            var resultId = read.Results[0].GetProperty("resultId").GetString();
            using var selected = await Send(client, TreeQuery, new { runId = read.Info.RunId, sourceId, resultId });
            Assert.False(selected.RootElement.TryGetProperty("errors", out _), selected.RootElement.ToString());
            var tree = selected.RootElement.GetProperty("data").GetProperty("scenarioResultTree");
            Assert.Equal(sourceId, tree.GetProperty("sourceId").GetString());
            Assert.Equal(resultId, tree.GetProperty("selectedResultId").GetString());
            using var absent = await Send(client, TreeQuery, new { runId = read.Info.RunId, sourceId, resultId = "not-recorded" });
            Assert.Equal(JsonValueKind.Null, absent.RootElement.GetProperty("data").GetProperty("scenarioResultTree").ValueKind);
        }
    }
}
#endif
