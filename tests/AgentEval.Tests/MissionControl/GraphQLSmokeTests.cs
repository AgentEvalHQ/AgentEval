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

