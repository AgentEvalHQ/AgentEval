// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Indexing;
using AgentEval.MissionControl.GraphQL;
using AgentEval.Results;
using AgentEval.Results.Suites;
using AgentEval.Results.Writing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AgentEval.Tests.MissionControl;

public sealed class SuiteQueryGraphQLTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-suite-http-" + Guid.NewGuid().ToString("N"));
    private EvidenceIndexRoot Root => new("root", Path.Combine(_root, "evidence"), IndexedEvidenceKind.Aef);
    private string Database => Path.Combine(_root, "index.sqlite");
    public SuiteQueryGraphQLTests() => Directory.CreateDirectory(Root.Directory);
    private WebApplicationFactory<Query> Factory(bool enabled = true, bool roots = true, string? directory = null) =>
        new WebApplicationFactory<Query>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("AgentEval:Queries:Enabled", enabled.ToString());
            builder.UseSetting("AgentEval:Queries:IndexPath", Database);
            builder.UseSetting("AgentEval:Root", _root);
            if (roots)
            {
                builder.UseSetting("AgentEval:Queries:AefRoots:0:RootId", "root");
                builder.UseSetting("AgentEval:Queries:AefRoots:0:Directory", directory ?? Root.Directory);
            }
        });
    private static async Task<JsonDocument> Read(HttpClient client, string query)
    {
        using var response = await client.PostAsJsonAsync("/graphql", new { query });
        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.TryGetProperty("errors", out _), json.RootElement.ToString());
        return json;
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SuiteQueries_DisabledOrWithoutRoots_ReturnNotConfigured(bool enabled, bool roots)
    {
        using var factory = Factory(enabled, roots); using var client = factory.CreateClient();
        using var json = await Read(client, "{ evidenceSuite(sourceId:\"none\") { state code } evidenceSuiteComparison(baselineSourceId:\"a\",candidateSourceId:\"b\") { state code } }");
        foreach (var field in json.RootElement.GetProperty("data").EnumerateObject())
        {
            Assert.Equal("NOT_CONFIGURED", field.Value.GetProperty("state").GetString());
            Assert.Equal("not-configured", field.Value.GetProperty("code").GetString());
        }
        Assert.False(File.Exists(Database));
    }

    [Theory]
    [InlineData(true, true, 0.8, true, "same-definition")]
    [InlineData(true, false, 0.9, false, "definition-mismatch")]
    [InlineData(false, false, 0.8, false, "generated-content")]
    public async Task SuiteComparison_SealedEvidence_PreservesIdentitySemantics(bool frozen, bool aborted, double threshold, bool compatible, string reason)
    {
        using var writer = new SqliteEvidenceIndexWriter(new() { DatabasePath = Database }, new SqliteEvidenceIndexSchema());
        writer.BindRoots([Root]);
        var baseline = WriteDefinition(writer, "baseline", true, false);
        var candidate = WriteDefinition(writer, "candidate", frozen, aborted, threshold);
        using var factory = Factory(); using var client = factory.CreateClient();
        using var json = await Read(client, $$"""
            { evidenceSuiteComparison(baselineSourceId:"{{baseline}}",candidateSourceId:"{{candidate}}") {
              state code data { compatible reason description
                baseline { sourceId indexGeneration runHash runStatus scope definitionDigest frozen }
                candidate { sourceId indexGeneration runHash runStatus scope definitionDigest frozen } } } }
            """);
        var outcome = json.RootElement.GetProperty("data").GetProperty("evidenceSuiteComparison");
        Assert.Equal("AVAILABLE", outcome.GetProperty("state").GetString());
        var data = outcome.GetProperty("data");
        Assert.Equal(compatible, data.GetProperty("compatible").GetBoolean());
        Assert.Equal(reason, data.GetProperty("reason").GetString());
        Assert.Equal("completed", data.GetProperty("baseline").GetProperty("runStatus").GetString());
        Assert.Equal(aborted ? "aborted" : "completed", data.GetProperty("candidate").GetProperty("runStatus").GetString());
        Assert.Equal(data.GetProperty("baseline").GetProperty("indexGeneration").GetString(), data.GetProperty("candidate").GetProperty("indexGeneration").GetString());
        using var schema = await Read(client, "{ __type(name:\"SuiteIdentityView\") { fields { name } } }");
        var fields = schema.RootElement.GetProperty("data").GetProperty("__type").GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToArray();
        Assert.DoesNotContain("directory", fields); Assert.DoesNotContain("definition", fields);
    }

    [Fact]
    public async Task SuiteIdentity_ChangedBindingOrTamperedEvidence_RefusesWithoutPhysicalPaths()
    {
        using var writer = new SqliteEvidenceIndexWriter(new() { DatabasePath = Database }, new SqliteEvidenceIndexSchema());
        writer.BindRoots([Root]); var id = WriteDefinition(writer, "run", true, false);
        var alternate = Path.Combine(_root, "alternate"); Directory.CreateDirectory(alternate);
        using (var factory = Factory(directory: alternate))
        using (var client = factory.CreateClient())
        using (var json = await Read(client, $$"""{ evidenceSuite(sourceId:"{{id}}") { state code data { runHash } } }"""))
        {
            var outcome = json.RootElement.GetProperty("data").GetProperty("evidenceSuite");
            Assert.Equal("UNAVAILABLE", outcome.GetProperty("state").GetString());
            Assert.Equal("root-binding-mismatch", outcome.GetProperty("code").GetString());
            Assert.Equal(JsonValueKind.Null, outcome.GetProperty("data").ValueKind);
        }
        File.AppendAllText(Path.Combine(Root.Directory, "run", "run.json"), " ");
        using var configured = Factory(); using var http = configured.CreateClient();
        using var tampered = await Read(http, $$"""{ evidenceSuite(sourceId:"{{id}}") { state code data { runHash } } }""");
        var refusal = tampered.RootElement.GetProperty("data").GetProperty("evidenceSuite");
        Assert.Equal("UNAVAILABLE", refusal.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, refusal.GetProperty("data").ValueKind);
        Assert.DoesNotContain(_root, tampered.RootElement.ToString());
    }

    private string WriteDefinition(SqliteEvidenceIndexWriter index, string name, bool frozen, bool aborted, double threshold = 0.8, string? padding = null)
    {
        var manifest = new SuiteDefinitionReader().Read(Encoding.UTF8.GetBytes(new JsonObject
        {
            ["schemaVersion"] = "1.0", ["kind"] = "composite", ["suiteRef"] = "bench:query",
            ["definitionVersion"] = "1", ["frozen"] = frozen,
            ["inputs"] = new JsonObject { ["rubric"] = SuiteDefinitionManifest.Hash("fixture"u8) },
            ["definition"] = new JsonObject { ["threshold"] = threshold }, ["caseIds"] = new JsonArray("case-a")
        }.ToJsonString()));
        var directory = Path.Combine(Root.Directory, name);
        var started = new DateTimeOffset(2026, 10, 1, 10, 0, 0, TimeSpan.Zero);
        var run = AefRunWriter.Create(directory, new AefRunHeader
        {
            RunId = name, Producer = new() { Name = "query-fixture", Version = "1" },
            Subject = new() { Ref = "agent:query", Kind = AefSubjectKind.Agent, Version = "1" },
            StartedAt = started, ContentCapture = AefContentCapture.Off,
            Execution = new() { TargetMode = AefTargetMode.Live, Stimulus = frozen ? AefStimulus.Suite : AefStimulus.Generated },
            Suite = new() { Ref = manifest.SuiteRef, Version = manifest.DefinitionVersion, Frozen = frozen, Digest = manifest.SuiteContentDigest },
            Ext = new JsonObject { ["agenteval.suiteIdentity"] = manifest.Metadata() }
        });
        run.SetMetrics([new() { Id = "quality", Kind = AefMetricKind.Score, Direction = AefMetricDirection.HigherBetter, Scale = AefScale.Between(0, 1) }]);
        run.PutExtFile(SuiteDefinitionManifest.ExtensionFile, manifest.ToBytes());
        if (!aborted) run.AddResult(new() { CaseId = "case-a", Path = "quality", Evaluator = new("code:quality"),
            State = AefState.Passed, Ext = padding is null ? null : new JsonObject { ["fixture.padding"] = padding }, Scores = [new() { Metric = "quality", Value = 0.9 }] });
        if (!aborted) run.SetSummary(new() { Lanes = [new AefSummaryLane("main", [new() { Metric = "quality", Path = "quality" }])] });
        run.Close(aborted ? AefRunStatus.Aborted : AefRunStatus.Completed, started.AddSeconds(1), aborted ? "cancelled" : null);
        AefSealer.Seal(directory);
        return index.Upsert(new AefIndexSourceReader(new SuiteIdentityReader()).Read("root", name, directory)).SourceId;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
#endif
