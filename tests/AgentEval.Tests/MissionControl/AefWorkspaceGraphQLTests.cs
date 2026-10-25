// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

#if NET10_0_OR_GREATER
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentEval.MissionControl.GraphQL;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Adapters.StoreV1;
using AgentEval.Results.Writing;
using AgentEval.Tests.Cli;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using AgentEval.Results.Adapters.Reading;

namespace AgentEval.Tests.MissionControl;

public sealed class AefWorkspaceGraphQLTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("ae-mc-aef-workspace-");
    private string Runs => Path.Combine(_root.FullName, ".agenteval", "aef", "runs");
    public void Dispose() => _root.Delete(recursive: true);

    private WebApplicationFactory<Query> Factory() => new WebApplicationFactory<Query>().WithWebHostBuilder(builder =>
    {
        LoopbackTestConnection.Configure(builder);
        builder.UseSetting("AgentEval:Root", _root.FullName);
        builder.UseSetting("AgentEval:Queries:Enabled", "false");
    });

    private string Native(string subjectRef = "agent:NativeAgent")
    {
        var evidence = ComplianceRenderCommandTests.MakeFixtureEvidence("NativeAgent");
        return AefCompositeRunWriter.Write(Runs, evidence.CompositeTree, AefCompositeRunWriter.Undescribed, new()
        {
            SubjectRef = subjectRef, TargetMode = AefTargetMode.Mocked,
            Lane = "gdpr", SuiteRef = "bench:gdpr/smoke", ContentCapture = AefContentCapture.Off,
        }, evidence.Base.GeneratedAt.AddSeconds(-1), evidence.Base.GeneratedAt.AddSeconds(-1).AddMilliseconds(25)).Directory;
    }

    private static string RunId(string directory) => JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "run.json")))!["runId"]!.GetValue<string>();

    [Fact]
    public async Task Compliance_NativeOnlyWithoutSolution_ListsAndSelectsCapturedEvidence()
    {
        var evidence = ComplianceRenderCommandTests.MakeFixtureEvidence("NativeAgent");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(evidence.Base with
        {
            Controls = [new AgentEval.Output.EvidenceControl("Art17", "Erasure", "pass", 0.9, ["gdpr.art17.erasure"], null)]
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        });
        var directory = AefCompositeRunWriter.Write(Runs, evidence.CompositeTree, AefCompositeRunWriter.Undescribed, new()
        {
            SubjectRef = "agent:NativeAgent", TargetMode = AefTargetMode.Mocked, Lane = "gdpr",
            SuiteRef = "bench:gdpr/smoke", ContentCapture = AefContentCapture.On,
            ComplianceArtifacts = [new() { Bytes = bytes, CaseIds = ["gdpr.art17.erasure"] }]
        }, evidence.Base.GeneratedAt, evidence.Base.GeneratedAt).Directory;
        using var factory = Factory(); using var client = factory.CreateClient();
        using var json = await Send(client, """
            { compliance { regulation evidenceCount subjectCount }
              complianceMatrix(regulation:"gdpr") { subjects { name kind } cells { timestamp controlId passRate } } }
            """);
        Assert.False(json.RootElement.TryGetProperty("errors", out _), json.RootElement.ToString());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal(1, Assert.Single(data.GetProperty("compliance").EnumerateArray()).GetProperty("evidenceCount").GetInt32());
        var matrix = data.GetProperty("complianceMatrix");
        Assert.Equal("NativeAgent", Assert.Single(matrix.GetProperty("subjects").EnumerateArray()).GetProperty("name").GetString());
        var timestamp = matrix.GetProperty("cells")[0].GetProperty("timestamp").GetString();
        using var detail = await Send(client, """
            query Detail($ts:String!) { complianceEvidence(regulation:"gdpr",subjectKind:AGENT,subjectName:"NativeAgent",timestamp:$ts) {
              sourceId containerRunId evidence { regulation subject { name } controls { id passRate } } } }
            """, new { ts = timestamp });
        Assert.False(detail.RootElement.TryGetProperty("errors", out _), detail.RootElement.ToString());
        Assert.Equal(RunId(directory), detail.RootElement.GetProperty("data").GetProperty("complianceEvidence").GetProperty("containerRunId").GetString());
        Assert.StartsWith("source-", detail.RootElement.GetProperty("data").GetProperty("complianceEvidence").GetProperty("sourceId").GetString());
        Assert.Equal("NativeAgent", detail.RootElement.GetProperty("data").GetProperty("complianceEvidence")
            .GetProperty("evidence").GetProperty("subject").GetProperty("name").GetString());
    }
    private static async Task<JsonDocument> Send(HttpClient client, string query, object? variables = null)
    {
        using var response = await client.PostAsJsonAsync("/graphql", new { query, variables });
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Dashboard_NativeOnlyWithoutSolution_ReturnsSubjectsAndRecentRuns()
    {
        var directory = Native();
        using var factory = Factory(); using var client = factory.CreateClient();
        using var json = await Send(client, "{ hasRunEvidence workspace { initialized } subjects { identity { kind name } } recentRuns { runId subjectName timestamp } }");
        Assert.False(json.RootElement.TryGetProperty("errors", out _), json.RootElement.ToString());
        var data = json.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("hasRunEvidence").GetBoolean());
        Assert.False(data.GetProperty("workspace").GetProperty("initialized").GetBoolean());
        Assert.Equal("NativeAgent", Assert.Single(data.GetProperty("subjects").EnumerateArray()).GetProperty("identity").GetProperty("name").GetString());
        Assert.Equal(RunId(directory), Assert.Single(data.GetProperty("recentRuns").EnumerateArray()).GetProperty("runId").GetString());
        Assert.False(File.Exists(Path.Combine(_root.FullName, ".agenteval", "solution.json")));
    }

    [Fact]
    public async Task Run_NativeOnly_ExposesResultsWithoutInventingLegacyMetadata()
    {
        var directory = Native(); var id = RunId(directory);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var json = await Send(client, $$"""
            { run(runId:"{{id}}") { run { runId duration } git { dirty } environment { ci } solution { name } }
              scenarios(runId:"{{id}}") { id score passed duration estimatedCost } }
            """);
        Assert.False(json.RootElement.TryGetProperty("errors", out _), json.RootElement.ToString());
        var data = json.RootElement.GetProperty("data"); var run = data.GetProperty("run");
        Assert.Equal(JsonValueKind.Object, run.ValueKind);
        Assert.Equal(id, run.GetProperty("run").GetProperty("runId").GetString());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("git").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("environment").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("solution").ValueKind);
        var scenario = Assert.Single(data.GetProperty("scenarios").EnumerateArray());
        Assert.Equal("gdpr.art17.erasure", scenario.GetProperty("id").GetString());
        Assert.Equal(0.9, scenario.GetProperty("score").GetDouble());
        Assert.Equal(JsonValueKind.Null, scenario.GetProperty("duration").ValueKind);
    }

    [Theory]
    [InlineData("recorded")]
    [InlineData("absent")]
    [InlineData("future")]
    [InlineData("number")]
    public async Task Scenarios_NativeName_UsesRecordedV1OrHonestFallback(string shape)
    {
        var directory = Native();
        var lines = File.ReadAllLines(Path.Combine(directory, "results.ndjson")).Select(l => JsonNode.Parse(l)!.AsObject()).ToArray();
        var root = Assert.Single(lines, l => l["parentResultId"] is null && l["trial"] is null);
        var metadata = root["ext"]!["agenteval.comparison"]!.AsObject();
        if (shape == "absent") metadata.Remove("name");
        else if (shape == "number") metadata["name"] = 42;
        else metadata["name"] = "Recorded native scenario";
        if (shape == "future") metadata["version"] = 2;
        File.WriteAllText(Path.Combine(directory, "results.ndjson"), string.Join("\n", lines.Select(l => l.ToJsonString(new() { WriteIndented = false }))) + "\n");
        File.Delete(Path.Combine(directory, "seal.json"));
        AefSealer.Seal(directory);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var json = await Send(client, $$"""{ scenarios(runId:"{{RunId(directory)}}") { id name } }""");
        if (shape == "number")
        {
            Assert.True(json.RootElement.TryGetProperty("errors", out _));
            return;
        }
        Assert.False(json.RootElement.TryGetProperty("errors", out _), json.RootElement.ToString());
        var scenario = Assert.Single(json.RootElement.GetProperty("data").GetProperty("scenarios").EnumerateArray());
        Assert.Equal(shape == "recorded" ? "Recorded native scenario" : scenario.GetProperty("id").GetString(), scenario.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Run_ImportedPlaceholderTimes_RemainUnknownExecutionTimes()
    {
        var legacy = Directory.CreateDirectory(Path.Combine(_root.FullName, "legacy-report"));
        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
        File.WriteAllBytes(Path.Combine(legacy.FullName, "evidence.json"), JsonSerializer.SerializeToUtf8Bytes(
            ComplianceRenderCommandTests.MakeFixtureEvidence("ImportedAgent").Base, jsonOptions));
        var directory = Path.Combine(Runs, "imported");
        StoreV1Exporter.ExportComplianceDirectory(legacy.FullName, directory);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var json = await Send(client, $$"""{ run(runId:"{{RunId(directory)}}") { run { timestamp duration } } }""");
        Assert.False(json.RootElement.TryGetProperty("errors", out _), json.RootElement.ToString());
        var run = json.RootElement.GetProperty("data").GetProperty("run");
        Assert.Equal(JsonValueKind.Object, run.ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("run").GetProperty("timestamp").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("run").GetProperty("duration").ValueKind);
    }

    [Fact]
    public async Task Dashboard_TamperedNativeEvidence_RefusesInsteadOfReportingEmptyWorkspace()
    {
        var directory = Native();
        var file = Path.Combine(directory, "run.json");
        var header = JsonNode.Parse(File.ReadAllText(file))!;
        header["subject"]!["ref"] = "agent:Changed";
        File.WriteAllText(file, header.ToJsonString());
        using var factory = Factory(); using var client = factory.CreateClient();
        using var json = await Send(client, "{ recentRuns { runId } }");
        Assert.True(json.RootElement.TryGetProperty("errors", out _), json.RootElement.ToString());
    }

    [Theory]
    [InlineData("model:x", "MODEL")]
    [InlineData("endpoint:x", "ENDPOINT")]
    [InlineData("mcp-server:x", "MCP_SERVER")]
    [InlineData("other:x", "OTHER")]
    public async Task Subjects_NativeKinds_AreNotCoercedToAgent(string subjectRef, string kind)
    {
        Native(subjectRef);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var json = await Send(client, $$"""{ subject(kind:{{kind}}, name:"x") { identity { kind name } } }""");
        Assert.False(json.RootElement.TryGetProperty("errors", out _), json.RootElement.ToString());
        Assert.Equal(kind, json.RootElement.GetProperty("data").GetProperty("subject").GetProperty("identity").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Run_DuplicateIDs_RequireSourceAndDoNotMixReadModels()
    {
        var directory = Native(); var id = RunId(directory);
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(Runs, "duplicate", Path.GetRelativePath(directory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
        using var factory = Factory(); using var client = factory.CreateClient();
        using var ambiguous = await Send(client, $$"""{ run(runId:"{{id}}") { sourceId } }""");
        Assert.True(ambiguous.RootElement.TryGetProperty("errors", out _));
        using var list = await Send(client, "{ recentRuns { runId sourceId } }");
        foreach (var pointer in list.RootElement.GetProperty("data").GetProperty("recentRuns").EnumerateArray())
        {
            var sourceId = pointer.GetProperty("sourceId").GetString();
            using var selected = await Send(client, $$"""
                { run(runId:"{{id}}",sourceId:"{{sourceId}}") { sourceId evidence { integrity } }
                  scenarios(runId:"{{id}}",sourceId:"{{sourceId}}") { id }
                  runCostBreakdown(runId:"{{id}}",sourceId:"{{sourceId}}") { isComplete unpricedCount } }
                """);
            Assert.False(selected.RootElement.TryGetProperty("errors", out _), selected.RootElement.ToString());
            var data = selected.RootElement.GetProperty("data");
            Assert.Equal(sourceId, data.GetProperty("run").GetProperty("sourceId").GetString());
            Assert.Single(data.GetProperty("scenarios").EnumerateArray());
            Assert.False(data.GetProperty("runCostBreakdown").GetProperty("isComplete").GetBoolean());
        }
    }

    [Fact]
    public async Task Read_MultipleFieldsShareOneSnapshotButNextRequestReverifies()
    {
        var directory = Native(); var id = RunId(directory); var reader = new CountingReader();
        using var factory = Factory().WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IAefWorkspaceEvidenceReader>(reader)));
        using var client = factory.CreateClient();
        using var first = await Send(client, $$"""
            { hasRunEvidence recentRuns { runId } run(runId:"{{id}}") { sourceId }
              scenarios(runId:"{{id}}") { id } runSummary(runId:"{{id}}") { runId } }
            """);
        Assert.False(first.RootElement.TryGetProperty("errors", out _), first.RootElement.ToString());
        Assert.Equal(1, reader.Reads);
        File.AppendAllText(Path.Combine(directory, "run.json"), " ");
        using var second = await Send(client, "{ recentRuns { runId } }");
        Assert.True(second.RootElement.TryGetProperty("errors", out _));
        Assert.Equal(2, reader.Reads);
    }

    [Fact]
    public async Task Subjects_UnknownKind_DoesNotBreakTheWorkspace()
    {
        var directory = Native("other:x");
        var path = Path.Combine(directory, "run.json");
        var header = JsonNode.Parse(File.ReadAllText(path))!;
        header["subject"]!["kind"] = "future-agent";
        File.WriteAllText(path, header.ToJsonString());
        File.Delete(Path.Combine(directory, "seal.json"));
        AefSealer.Seal(directory);
        // Confirm this is valid evidence independently of the presentation projection.
        Assert.Equal("future-agent", new AefRunEvidenceReader().Read(directory).Header.GetProperty("subject").GetProperty("kind").GetString());
        using var factory = Factory(); using var client = factory.CreateClient();
        using var json = await Send(client, "{ subjects { identity { kind rawKind name } } recentRuns { kind rawKind } }");
        Assert.False(json.RootElement.TryGetProperty("errors", out _), json.RootElement.ToString());
        Assert.Equal("OTHER", json.RootElement.GetProperty("data").GetProperty("subjects")[0].GetProperty("identity").GetProperty("kind").GetString());
        Assert.Equal("future-agent", json.RootElement.GetProperty("data").GetProperty("recentRuns")[0].GetProperty("rawKind").GetString());
    }

    [Fact]
    public async Task Subject_DistinctRefsWithSameDisplayName_CanSelectExactRef()
    {
        Native("agent:A"); Native("agent:%41");
        using var factory = Factory(); using var client = factory.CreateClient();
        using var json = await Send(client, "{ subject(kind:AGENT,name:\"A\",subjectRef:\"agent:%41\") { identity { ref } } }");
        Assert.False(json.RootElement.TryGetProperty("errors", out _), json.RootElement.ToString());
        Assert.Equal("agent:%41", json.RootElement.GetProperty("data").GetProperty("subject").GetProperty("identity").GetProperty("ref").GetString());
    }

    [Theory]
    [InlineData("error")]
    [InlineData("inapplicable")]
    public async Task Summary_NativeAbsence_DoesNotRelabelAsSkipped(string label)
    {
        var original = ComplianceRenderCommandTests.MakeFixtureEvidence("NativeAgent").CompositeTree;
        var tree = original with { Score = new(0, null, label, false, null, "none", null),
            Details = new(null, null, ["Recorded absence"], null, null) };
        var directory = AefCompositeRunWriter.Write(Runs, tree, AefCompositeRunWriter.Undescribed,
            new() { SubjectRef = "agent:NativeAgent", TargetMode = AefTargetMode.Mocked, Lane = "test", SuiteRef = "bench:test" },
            original.EvaluatedAt, original.EvaluatedAt.AddMilliseconds(1)).Directory;
        using var factory = Factory(); using var client = factory.CreateClient();
        using var json = await Send(client, $$"""{ runSummary(runId:"{{RunId(directory)}}") { stats { total skipped } states { state count } } }""");
        Assert.False(json.RootElement.TryGetProperty("errors", out _), json.RootElement.ToString());
        var stats = json.RootElement.GetProperty("data").GetProperty("runSummary").GetProperty("stats");
        Assert.Equal(1, stats.GetProperty("total").GetInt32());
        Assert.Equal(0, stats.GetProperty("skipped").GetInt32());
        var state = Assert.Single(json.RootElement.GetProperty("data").GetProperty("runSummary").GetProperty("states").EnumerateArray());
        Assert.Equal(label == "inapplicable" ? "not_applicable" : label, state.GetProperty("state").GetString());
        Assert.Equal(1, state.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task RunDetail_ActualSpaOperation_ReadsPopulatedNativeEvidenceInProduction()
    {
        var directory = Native();
        var query = (string)SpaQueriesValidateTests.SpaOperations().Single(row => (string)row[1] == "RUN_DETAIL_QUERY")[2];
        using var factory = Factory().WithWebHostBuilder(b => b.UseEnvironment("Production"));
        using var client = factory.CreateClient();
        using var json = await Send(client, query, new { runId = RunId(directory) });
        Assert.False(json.RootElement.TryGetProperty("errors", out _), json.RootElement.ToString());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("intact", data.GetProperty("run").GetProperty("evidence").GetProperty("integrity").GetString());
        Assert.Single(data.GetProperty("scenarios").EnumerateArray());
    }

    [Theory]
    [InlineData("known", 15L, 5L)]
    [InlineData("zero", 0L, 0L)]
    [InlineData("partial", null, 5L)]
    [InlineData("integral-spelling", 15L, 5L)]
    public async Task Summary_RecordedNativeUsage_PreservesTotalsAndUnknownDimensions(string shape, long? input, long? output)
    {
        var start = DateTimeOffset.Parse("2026-10-24T00:00:00Z");
        var zero = shape == "zero";
        var directory = AefProbeRunWriter.Write(Runs,
            [new() { CaseId = "probe", State = AefState.Passed, Annotator = AefAnnotatorKind.Code }],
            new()
            {
                SubjectRef = "agent:NativeAgent", TargetMode = AefTargetMode.Mocked, SuiteRef = "redteam:usage",
                Usage = [new() { Role = AefUsageRole.Agent, Model = "agent", InputTokens = zero ? 0 : 10, OutputTokens = zero ? 0 : 3 },
                    new() { Role = AefUsageRole.Judge, Model = "judge", InputTokens = shape == "partial" ? null : zero ? 0 : 5,
                        OutputTokens = zero ? 0 : 2 }],
            }, start, start.AddSeconds(1)).Directory;
        if (shape == "integral-spelling")
        {
            var path = Path.Combine(directory, "summary.json");
            var text = File.ReadAllText(path);
            text = System.Text.RegularExpressions.Regex.Replace(text, "(\"gen_ai\\.usage\\.input_tokens\"\\s*:\\s*)10\\b", "${1}1e1");
            text = System.Text.RegularExpressions.Regex.Replace(text, "(\"gen_ai\\.usage\\.output_tokens\"\\s*:\\s*)3\\b", "${1}3.0");
            Assert.Contains("1e1", text); Assert.Contains("3.0", text);
            File.WriteAllText(path, text);
            File.Delete(Path.Combine(directory, "seal.json")); AefSealer.Seal(directory);
        }
        using var factory = Factory(); using var client = factory.CreateClient();
        using var json = await Send(client, $$"""{ runSummary(runId:"{{RunId(directory)}}") { cost { promptTokens completionTokens } } }""");
        Assert.False(json.RootElement.TryGetProperty("errors", out _), json.RootElement.ToString());
        var cost = json.RootElement.GetProperty("data").GetProperty("runSummary").GetProperty("cost");
        Assert.Equal(input, cost.GetProperty("promptTokens").ValueKind == JsonValueKind.Null ? (long?)null : cost.GetProperty("promptTokens").GetInt64());
        Assert.Equal(output, cost.GetProperty("completionTokens").ValueKind == JsonValueKind.Null ? (long?)null : cost.GetProperty("completionTokens").GetInt64());
    }

    private sealed class CountingReader : IAefWorkspaceEvidenceReader
    {
        public int Reads;
        public async Task<AefWorkspaceEvidence> ReadAsync(AgentEval.Output.IOutputStoreReader store, long maxBytes = 256L << 20, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Reads);
            return await new AefWorkspaceEvidenceReader().ReadAsync(store, maxBytes, ct);
        }

        public AefWorkspaceEvidence Read(string workspaceDirectory, long maxBytes = 256L << 20, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Reads);
            return new AefWorkspaceEvidenceReader().Read(workspaceDirectory, maxBytes, ct);
        }
    }
}
#endif
