// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Globalization;
using AgentEval.MissionControl.Core;
using Microsoft.Extensions.DependencyInjection;
using AgentEval.Indexing;
using AgentEval.Results.Observations;
using AgentEval.MissionControl.GraphQL;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Hosting;

namespace AgentEval.Tests.MissionControl;

public sealed class EvidenceQueryGraphQLTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ae-query-http-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_root, "index.sqlite");
    public EvidenceQueryGraphQLTests() => Directory.CreateDirectory(_root);

    private WebApplicationFactory<Query> Factory(bool enabled) => new WebApplicationFactory<Query>().WithWebHostBuilder(builder =>
        {
            LoopbackTestConnection.Configure(builder);
            builder.UseSetting("AgentEval:Queries:Enabled", enabled.ToString());
            builder.UseSetting("AgentEval:Queries:IndexPath", Database);
            builder.UseSetting("AgentEval:Root", _root);
        });

    private static async Task<JsonDocument> QueryAsync(HttpClient client, string query)
    {
        using var response = await client.PostAsJsonAsync("/graphql", new { query });
        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.TryGetProperty("errors", out _), json.RootElement.ToString());
        return json;
    }

    [Fact]
    public async Task EvidenceSources_Disabled_ReturnsNotConfiguredAndKeepsLegacyPing()
    {
        using var factory = Factory(false); using var client = factory.CreateClient();
        using var json = await QueryAsync(client, "{ ping evidenceSources { state code data { generation } } }");
        var data = json.RootElement.GetProperty("data"); Assert.Equal("pong", data.GetProperty("ping").GetString());
        var result = data.GetProperty("evidenceSources"); Assert.Equal("NOT_CONFIGURED", result.GetProperty("state").GetString());
        Assert.Equal("not-configured", result.GetProperty("code").GetString()); Assert.Equal(JsonValueKind.Null, result.GetProperty("data").ValueKind);
        AssertOutcomeParity(factory.Services.GetRequiredService<IEvidenceQueryService>().Sources(new()), result);
        Assert.False(File.Exists(Database));
    }

    [Fact]
    public async Task EvidenceSources_ActualIndex_TransportsExactInt64AsTextAndBoundedContinuation()
    {
        using (var writer = new SqliteEvidenceIndexWriter(new() { DatabasePath = Database }, new SqliteEvidenceIndexSchema()))
        {
            foreach (var path in new[] { "a", "b" }) writer.Upsert(new("root", path, new string('a', 64), IndexedEvidenceKind.Legacy,
                IndexedVerification.Unverified, new(path, null, null, null, null, null, null, "unknown"), [], [], null));
        }
        using (var connection = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE index_metadata SET generation=9007199254740993; UPDATE sources SET indexed_generation=9007199254740993;";
            command.ExecuteNonQuery();
        }
        using var factory = Factory(true); using var client = factory.CreateClient();
        using var json = await QueryAsync(client, "{ evidenceSources(pageSize:1) { state data { indexInstanceId generation nextCursor items { sourceId rootId relativePath kind fingerprint indexedGeneration verification } } } }");
        var result = json.RootElement.GetProperty("data").GetProperty("evidenceSources");
        Assert.Equal("AVAILABLE", result.GetProperty("state").GetString()); var page = result.GetProperty("data");
        AssertPageParity(factory.Services.GetRequiredService<IEvidenceQueryService>().Sources(new(PageSize: 1)).Data!, page);
        Assert.Equal("9007199254740993", page.GetProperty("generation").GetString());
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());
        Assert.Equal("9007199254740993", item.GetProperty("indexedGeneration").GetString());
        Assert.Equal("a", item.GetProperty("relativePath").GetString()); Assert.Equal("unverified", item.GetProperty("verification").GetString());
        Assert.False(string.IsNullOrEmpty(page.GetProperty("nextCursor").GetString()));
        var cursor = JsonSerializer.Serialize(page.GetProperty("nextCursor").GetString());
        using var next = await QueryAsync(client, "{ evidenceSources(pageSize:1,cursor:" + cursor + ") { state data { nextCursor items { relativePath } } } }");
        var nextPage = next.RootElement.GetProperty("data").GetProperty("evidenceSources").GetProperty("data");
        Assert.Equal("b", Assert.Single(nextPage.GetProperty("items").EnumerateArray()).GetProperty("relativePath").GetString());
        Assert.Equal(JsonValueKind.Null, nextPage.GetProperty("nextCursor").ValueKind);
        using var changedShape = await QueryAsync(client, "{ evidenceSources(pageSize:2,cursor:" + cursor + ") { state code data { generation } } }");
        var refusal = changedShape.RootElement.GetProperty("data").GetProperty("evidenceSources");
        Assert.Equal("INVALID_REQUEST", refusal.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, refusal.GetProperty("data").ValueKind);
        using var schema = await QueryAsync(client, "{ __type(name:\"EvidenceSourceView\") { fields { name } } }");
        var fields = schema.RootElement.GetProperty("data").GetProperty("__type").GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToArray();
        Assert.DoesNotContain("directory", fields); Assert.DoesNotContain("binding", fields);
    }

    [Fact]
    public async Task EvidenceSources_MissingIndexAndInvalidPage_AreExplicitAndDoNotCreateIndex()
    {
        using var factory = Factory(true); using var client = factory.CreateClient();
        using var missing = await QueryAsync(client, "{ evidenceSources { state code } }");
        var result = missing.RootElement.GetProperty("data").GetProperty("evidenceSources");
        var queries = factory.Services.GetRequiredService<IEvidenceQueryService>();
        AssertOutcomeParity(queries.Sources(new()), result);
        Assert.Equal("UNAVAILABLE", result.GetProperty("state").GetString()); Assert.Equal("missing-index", result.GetProperty("code").GetString());
        using var invalid = await QueryAsync(client, "{ evidenceSources(pageSize:501) { state code } }");
        Assert.Equal("INVALID_REQUEST", invalid.RootElement.GetProperty("data").GetProperty("evidenceSources").GetProperty("state").GetString());
        AssertOutcomeParity(queries.Sources(new(PageSize: 501)), invalid.RootElement.GetProperty("data").GetProperty("evidenceSources"));
        Assert.False(File.Exists(Database));
    }

    [Fact]
    public async Task EvidencePages_ActualEvaluation_PreserveValuesAndRejectStaleOrWrongFamilyCursors()
    {
        using var writer = new SqliteEvidenceIndexWriter(new() { DatabasePath = Database }, new SqliteEvidenceIndexSchema());
        var source = new EvidenceIndexSource("root", "run", new string('a', 64), IndexedEvidenceKind.Legacy,
            IndexedVerification.Unverified, new("run", null, null, null, null, null, "PASS", "unknown"),
            [new("a", "case-a", 9007199254740993L, "PASS"), new("legacy:summary", null, null, "PASS")],
            [new("a", "quality", 2, .5, null, false, "reported"), new("a", "quality", 10, null, "NaN", null, "reported")], null);
        var id = writer.Upsert(source).SourceId;
        using var factory = Factory(true); using var client = factory.CreateClient();
        using var first = await QueryAsync(client, $$"""
            { evidenceResults(sourceId:"{{id}}",pageSize:1) { state data {
              indexInstanceId generation sourceKind verification fingerprint nextCursor items { resultId caseId trial outcome } } }
              evidenceMetrics(sourceId:"{{id}}") { data { indexInstanceId generation sourceKind verification fingerprint nextCursor
                items { resultId metricId ordinal score scoreText passed provenance } } } }
            """);
        var data = first.RootElement.GetProperty("data");
        var page = data.GetProperty("evidenceResults").GetProperty("data");
        var queries = factory.Services.GetRequiredService<IEvidenceQueryService>();
        AssertPageParity(queries.Results(new(id, 1)).Data!, page);
        AssertPageParity(queries.Metrics(new(id)).Data!, data.GetProperty("evidenceMetrics").GetProperty("data"));
        Assert.Equal("legacy", page.GetProperty("sourceKind").GetString());
        Assert.Equal("unverified", page.GetProperty("verification").GetString());
        Assert.Equal(writer.Find("root", "run")!.Fingerprint, page.GetProperty("fingerprint").GetString());
        Assert.Equal("9007199254740993", Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("trial").GetString());
        var metrics = data.GetProperty("evidenceMetrics").GetProperty("data").GetProperty("items");
        Assert.Equal(new[] { 2, 10 }, metrics.EnumerateArray().Select(m => m.GetProperty("ordinal").GetInt32()));
        Assert.Equal(.5, metrics[0].GetProperty("score").GetDouble());
        Assert.False(metrics[0].GetProperty("passed").GetBoolean());
        Assert.Equal("NaN", metrics[1].GetProperty("scoreText").GetString());
        Assert.Equal(JsonValueKind.Null, metrics[1].GetProperty("score").ValueKind);
        Assert.Equal(JsonValueKind.Null, metrics[1].GetProperty("passed").ValueKind);
        var cursor = JsonSerializer.Serialize(page.GetProperty("nextCursor").GetString());
        var nextQuery = $$"""
            { evidenceResults(sourceId:"{{id}}",pageSize:1,cursor:{{cursor}}) { state code data { items { caseId trial } nextCursor } } }
            """;
        using var next = await QueryAsync(client, nextQuery);
        var last = next.RootElement.GetProperty("data").GetProperty("evidenceResults").GetProperty("data");
        var aggregate = Assert.Single(last.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, aggregate.GetProperty("caseId").ValueKind);
        Assert.Equal(JsonValueKind.Null, aggregate.GetProperty("trial").ValueKind);
        Assert.Equal(JsonValueKind.Null, last.GetProperty("nextCursor").ValueKind);
        using var wrong = await QueryAsync(client, $$"""
            { evidenceMetrics(sourceId:"{{id}}",pageSize:1,cursor:{{cursor}}) { state code }
              evidenceSpans(sourceId:"{{id}}") { state code } }
            """);
        Assert.Equal("invalid-cursor", wrong.RootElement.GetProperty("data").GetProperty("evidenceMetrics").GetProperty("code").GetString());
        Assert.Equal("wrong-source-kind", wrong.RootElement.GetProperty("data").GetProperty("evidenceSpans").GetProperty("code").GetString());
        writer.Upsert(source with { Run = source.Run! with { Outcome = "FAIL" } });
        using var stale = await QueryAsync(client, nextQuery);
        var refusal = stale.RootElement.GetProperty("data").GetProperty("evidenceResults");
        AssertOutcomeParity(queries.Results(new(id, 1, page.GetProperty("nextCursor").GetString())), refusal);
        Assert.Equal("STALE_CURSOR", refusal.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, refusal.GetProperty("data").ValueKind);
    }

    [Fact]
    public async Task EvidencePages_EmptyMissingAndOversized_AreDistinctTypedOutcomes()
    {
        using var writer = new SqliteEvidenceIndexWriter(new() { DatabasePath = Database }, new SqliteEvidenceIndexSchema());
        var source = new EvidenceIndexSource("root", "run", new string('a', 64), IndexedEvidenceKind.Legacy,
            IndexedVerification.Unverified, new("run", null, null, null, null, null, null, "unknown"), [], [], null);
        var emptyId = writer.Upsert(source).SourceId;
        var largeId = writer.Upsert(source with { RelativePath = "large", Results = [new("a", null, null, null)],
            Metrics = [new("a", "quality", 0, .5, null, null, new string('x', (2 << 20) + 1))] }).SourceId;
        using var factory = Factory(true); using var client = factory.CreateClient();
        using var json = await QueryAsync(client, $$"""
            { empty:evidenceResults(sourceId:"{{emptyId}}") { state data { items { resultId } } }
              missing:evidenceResults(sourceId:"{{new string('0', 64)}}") { state code data { generation } }
              large:evidenceMetrics(sourceId:"{{largeId}}") { state code data { generation } } }
            """);
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("EMPTY", data.GetProperty("empty").GetProperty("state").GetString());
        Assert.Empty(data.GetProperty("empty").GetProperty("data").GetProperty("items").EnumerateArray());
        Assert.Equal("NOT_FOUND", data.GetProperty("missing").GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("missing").GetProperty("data").ValueKind);
        Assert.Equal("RESOURCE_LIMIT", data.GetProperty("large").GetProperty("state").GetString());
        Assert.Equal("page-byte-limit", data.GetProperty("large").GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("large").GetProperty("data").ValueKind);
        var queries = factory.Services.GetRequiredService<IEvidenceQueryService>();
        AssertOutcomeParity(queries.Results(new(emptyId)), data.GetProperty("empty"));
        AssertOutcomeParity(queries.Results(new(new string('0', 64))), data.GetProperty("missing"));
        AssertOutcomeParity(queries.Metrics(new(largeId)), data.GetProperty("large"));
    }

    [Fact]
    public async Task EvidencePages_ActualObservations_PreserveDuplicatesExactTimesAndConflictProvenance()
    {
        const string traceId = "0123456789abcdef0123456789abcdef"; // DevSkim: ignore DS173237 - synthetic OTLP trace identity, not a credential
        const string span = $$$"""
            {"traceId":"{{{traceId}}}","spanId":"0123456789abcdef","startTimeUnixNano":"18446744073709551615",
             "attributes":[{"key":"gen_ai.usage.input_tokens","value":{"intValue":"9007199254740993"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"12"}},
             {"key":"gen_ai.usage.completion_tokens","value":{"intValue":"13"}}]}
            """;
        var batch = new OtlpJsonTraceDecoder().Decode(Encoding.UTF8.GetBytes("{\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[" + span + "," + span + "]}]}]}"));
        using var store = new OtlpObservationStore(new() { Root = Path.Combine(_root, "observations"), Source = "receiver" },
            new OtlpContentPolicy(), new OtlpSessionGrouper(new OtlpTraceNormalizer()));
        store.Accept(batch);
        using var writer = new SqliteEvidenceIndexWriter(new() { DatabasePath = Database }, new SqliteEvidenceIndexSchema());
        Assert.True(new OtlpObservationIndexer(writer).Import("observations", store).Complete);
        var id = writer.ListSources("observations").Single().Checkpoint.SourceId;
        using var factory = Factory(true); using var client = factory.CreateClient();
        using var json = await QueryAsync(client, $$"""
            { evidenceSpans(sourceId:"{{id}}") { data { indexInstanceId generation sourceKind verification fingerprint nextCursor
                items { reportOrdinal groupId groupingRevision groupingBasis mappingRevision traceId spanId parentSpanId startNanos endNanos resourceIndex scopeIndex spanIndex } } }
              evidenceFields(sourceId:"{{id}}") { data { indexInstanceId generation sourceKind verification fingerprint nextCursor
                items { reportOrdinal fieldName state integerValue textValue sourcesJson } } }
              evidenceResults(sourceId:"{{id}}") { state code } }
            """);
        var data = json.RootElement.GetProperty("data");
        var spans = data.GetProperty("evidenceSpans").GetProperty("data");
        var queries = factory.Services.GetRequiredService<IEvidenceQueryService>();
        AssertPageParity(queries.Spans(new(id)).Data!, spans);
        AssertPageParity(queries.Fields(new(id)).Data!, data.GetProperty("evidenceFields").GetProperty("data"));
        Assert.Equal("observation", spans.GetProperty("sourceKind").GetString());
        var reports = spans.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { "0", "1" }, reports.Select(s => s.GetProperty("reportOrdinal").GetString()));
        Assert.All(reports, s => Assert.Equal("18446744073709551615", s.GetProperty("startNanos").GetString()));
        Assert.Equal(reports[0].GetProperty("spanId").GetString(), reports[1].GetProperty("spanId").GetString());
        var fields = data.GetProperty("evidenceFields").GetProperty("data").GetProperty("items").EnumerateArray().ToArray();
        var inputs = fields.Where(f => f.GetProperty("fieldName").GetString() == "tokens.input").ToArray();
        Assert.Equal(2, inputs.Length);
        Assert.All(inputs, f => Assert.Equal("9007199254740993", f.GetProperty("integerValue").GetString()));
        var conflicts = fields.Where(f => f.GetProperty("fieldName").GetString() == "tokens.output").ToArray();
        Assert.Equal(2, conflicts.Length);
        foreach (var field in conflicts)
        {
            Assert.Equal("Conflict", field.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, field.GetProperty("integerValue").ValueKind);
            using var provenance = JsonDocument.Parse(field.GetProperty("sourcesJson").GetString()!);
            Assert.Equal(new long[] { 12, 13 }, provenance.RootElement.EnumerateArray().Select(p => p.GetProperty("Integer").GetInt64()).Order());
        }
        Assert.Equal("wrong-source-kind", data.GetProperty("evidenceResults").GetProperty("code").GetString());
    }

    private static void AssertOutcomeParity<T>(QueryResult<T> direct, JsonElement transported) where T : class
    {
        Assert.Equal(JsonNamingPolicy.SnakeCaseUpper.ConvertName(direct.State.ToString()), transported.GetProperty("state").GetString());
        if (transported.TryGetProperty("code", out var code)) Assert.Equal(direct.Code, code.GetString());
        if (direct.Data is null && transported.TryGetProperty("data", out var data)) Assert.Equal(JsonValueKind.Null, data.ValueKind);
    }

    private static void AssertPageParity(object directPage, JsonElement transported)
    {
        var expected = JsonSerializer.SerializeToNode(directPage, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        expected["indexInstanceId"] = expected["instanceId"]!.DeepClone(); expected.Remove("instanceId");
        // These are the documented wire adaptations. All other page content must survive unchanged.
        var integers = new HashSet<string>(StringComparer.Ordinal)
        { "generation", "indexedGeneration", "trial", "reportOrdinal", "resourceIndex", "scopeIndex", "spanIndex", "integerValue" };
        void Adapt(JsonNode? node)
        {
            if (node is JsonObject obj)
                foreach (var (key, value) in obj.ToArray())
                {
                    if (integers.Contains(key) && value is not null) obj[key] = value.GetValue<long>().ToString(CultureInfo.InvariantCulture);
                    else Adapt(value);
                }
            else if (node is JsonArray array) foreach (var item in array) Adapt(item);
        }
        Adapt(expected);
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(transported.GetRawText())),
            "The full page must match direct service output after documented naming and exact-integer adaptations.");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
#endif
