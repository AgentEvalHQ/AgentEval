// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER

using System.Net.Http.Json;
using System.Text.Json;
using AgentEval.MissionControl.GraphQL;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

// WebApplicationFactory<TEntryPoint> uses TEntryPoint only to locate the SUT assembly;
// any public type in AgentEval.MissionControl works. We use Query (the GraphQL root
// type) instead of Program because both AgentEval.Cli and AgentEval.MissionControl
// declare a top-level `Program` in the global namespace, causing CS0433.

namespace AgentEval.Tests.MissionControl;

/// <summary>
/// Locks down the MC1.4.0 baseline: the Mission Control server boots, the GraphQL
/// endpoint at <c>/graphql</c> accepts queries, and the version REST endpoint
/// returns the expected shape. Plan-08 acceptance: "POST /graphql accepts
/// {__typename} introspection query and returns the Query root name".
/// </summary>
public class GraphQLSmokeTests : IClassFixture<WebApplicationFactory<Query>>
{
    private readonly WebApplicationFactory<Query> _factory;

    public GraphQLSmokeTests(WebApplicationFactory<Query> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GraphQL_Introspection_ReturnsQueryTypeName()
    {
        using var client = _factory.CreateClient();
        var request = new { query = "{ __schema { queryType { name } } }" };

        var response = await client.PostAsJsonAsync("/graphql", request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        // Hot Chocolate exposes the root query type as the C# class name.
        Assert.Contains("\"name\":\"Query\"", body);
    }

    [Fact]
    public async Task GraphQL_PingResolver_ReturnsPong()
    {
        using var client = _factory.CreateClient();
        var request = new { query = "{ ping }" };

        var response = await client.PostAsJsonAsync("/graphql", request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"ping\":\"pong\"", body);
    }

    [Fact]
    public async Task GraphQL_VersionResolver_ReturnsVersionString()
    {
        using var client = _factory.CreateClient();
        var request = new { query = "{ agentEvalVersion }" };

        var response = await client.PostAsJsonAsync("/graphql", request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        // Hot Chocolate normalises method names to camelCase. Just verify a non-empty
        // version string came back; exact value depends on assembly version.
        using var doc = JsonDocument.Parse(body);
        var version = doc.RootElement.GetProperty("data").GetProperty("agentEvalVersion").GetString();
        Assert.False(string.IsNullOrWhiteSpace(version),
            $"agentEvalVersion should be non-empty; full response was: {body}");
    }

    // NOTE: a meaningful depth-limit test requires recursive types in the schema
    // (e.g. EvalResult.subResults from plan-08 MC1.4.6). The current MC1.4.0 baseline
    // schema has only flat resolvers (ping, agentEvalVersion), so an introspection
    // chain naturally truncates at ~6 levels long before any depth guard fires.
    // The proper depth-limit test (depth=100 → rejected) lands with MC1.4.6.

    [Fact]
    public async Task GraphQL_Evaluators_ReturnsRegisteredCards()
    {
        // MC1.5.3 contract: Query.evaluators returns the cards that ship as embedded
        // resources in AgentEval.Evals.Agentic/EvaluatorCards/*.json. Phase 1 baseline
        // ships 5 representative cards; full 46 follow in a later session.
        using var client = _factory.CreateClient();
        var request = new { query = "{ evaluators { key name category costTier higherIsBetter } }" };

        var response = await client.PostAsJsonAsync("/graphql", request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var evaluators = doc.RootElement.GetProperty("data").GetProperty("evaluators");

        Assert.True(evaluators.GetArrayLength() >= 5,
            $"Expected at least 5 EvaluatorCards from sample set; full response was: {body}");

        // task_completion is one of the sample cards; verify it's queryable.
        var hasTaskCompletion = evaluators.EnumerateArray()
            .Any(e => e.GetProperty("key").GetString() == "task_completion");
        Assert.True(hasTaskCompletion, "Expected task_completion in the registry response.");
    }

    [Fact]
    public async Task GraphQL_EvaluatorByKey_ReturnsSpecificCard()
    {
        using var client = _factory.CreateClient();
        var request = new { query = "{ evaluator(key: \"task_completion\") { key name category costTier description } }" };

        var response = await client.PostAsJsonAsync("/graphql", request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var card = doc.RootElement.GetProperty("data").GetProperty("evaluator");

        Assert.Equal("task_completion", card.GetProperty("key").GetString());
        Assert.Equal("Task Completion", card.GetProperty("name").GetString());
        Assert.Equal("system", card.GetProperty("category").GetString());
    }

    [Fact]
    public async Task GraphQL_EvaluatorByKey_UnknownKey_ReturnsNull()
    {
        using var client = _factory.CreateClient();
        var request = new { query = "{ evaluator(key: \"definitely_not_a_real_key\") { key } }" };

        var response = await client.PostAsJsonAsync("/graphql", request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var evaluator = doc.RootElement.GetProperty("data").GetProperty("evaluator");
        Assert.Equal(JsonValueKind.Null, evaluator.ValueKind);
    }

    [Fact]
    public async Task GraphQL_EvaluatorsByCostTier_FiltersResults()
    {
        // Filter to TRIVIAL: only cost_quality_efficiency from the sample set.
        using var client = _factory.CreateClient();
        var request = new { query = "{ evaluators(costTier: TRIVIAL) { key costTier } }" };

        var response = await client.PostAsJsonAsync("/graphql", request);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var evaluators = doc.RootElement.GetProperty("data").GetProperty("evaluators");

        // Every returned card must have costTier == TRIVIAL.
        foreach (var e in evaluators.EnumerateArray())
        {
            Assert.Equal("TRIVIAL", e.GetProperty("costTier").GetString());
        }
        // At least cost_quality_efficiency should match.
        var keys = evaluators.EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToList();
        Assert.Contains("cost_quality_efficiency", keys);
    }

    [Fact]
    public async Task RestVersion_ReturnsExpectedShape()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/version");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"mode\":\"local\"", body);
        Assert.Contains("\"graphqlEndpoint\":\"/graphql\"", body);
        Assert.Contains("\"agentEvalVersion\"", body);
    }
}

#endif

