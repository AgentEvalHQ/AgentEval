// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER
using System.Net.Http.Json;
using System.Text.Json;
using AgentEval.Indexing;
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
        using var json = await QueryAsync(client, "{ evidenceSources(pageSize:1) { state data { generation nextCursor items { relativePath indexedGeneration verification } } } }");
        var result = json.RootElement.GetProperty("data").GetProperty("evidenceSources");
        Assert.Equal("AVAILABLE", result.GetProperty("state").GetString()); var page = result.GetProperty("data");
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
        Assert.Equal("UNAVAILABLE", result.GetProperty("state").GetString()); Assert.Equal("missing-index", result.GetProperty("code").GetString());
        using var invalid = await QueryAsync(client, "{ evidenceSources(pageSize:501) { state code } }");
        Assert.Equal("INVALID_REQUEST", invalid.RootElement.GetProperty("data").GetProperty("evidenceSources").GetProperty("state").GetString());
        Assert.False(File.Exists(Database));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
#endif
