// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER

using System.Net.Http.Json;
using System.Text.Json;
using AgentEval.MissionControl.GraphQL;
using AgentEval.Output;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AgentEval.Tests.MissionControl;

/// <summary>
/// Locks down the MC1.2.2 + MC1.4.2 contract: Query.solution / subjects / subject /
/// recentRuns / run resolvers read through <see cref="IOutputStoreReader"/>; tests
/// inject an <see cref="InMemoryOutputStore"/> seeded with sample subjects + a run.
/// </summary>
public class GraphQLReadResolversTests : IClassFixture<SeededMissionControlFactory>
{
    private readonly SeededMissionControlFactory _factory;

    public GraphQLReadResolversTests(SeededMissionControlFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Solution_ReturnsSeededSolutionInfo()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/graphql",
            new { query = "{ solution { name } }" });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var solution = doc.RootElement.GetProperty("data").GetProperty("solution");

        Assert.Equal(JsonValueKind.Object, solution.ValueKind);
        Assert.Equal("test-solution", solution.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Subjects_ReturnsSeededSubjects()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/graphql",
            new { query = "{ subjects { identity { kind name } } }" });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var subjects = doc.RootElement.GetProperty("data").GetProperty("subjects");

        Assert.True(subjects.GetArrayLength() >= 2,
            $"Expected at least 2 seeded subjects (1 agent + 1 workflow); body was: {body}");

        var names = subjects.EnumerateArray()
            .Select(s => s.GetProperty("identity").GetProperty("name").GetString())
            .ToList();
        Assert.Contains("TravelAgent", names);
        Assert.Contains("TripPlanner", names);
    }

    [Fact]
    public async Task SubjectsFilteredByKind_ReturnsOnlyAgents()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/graphql",
            new { query = "{ subjects(kind: AGENT) { identity { kind name } } }" });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var subjects = doc.RootElement.GetProperty("data").GetProperty("subjects");

        foreach (var s in subjects.EnumerateArray())
        {
            var kind = s.GetProperty("identity").GetProperty("kind").GetString();
            Assert.Equal("AGENT", kind);
        }
    }

    [Fact]
    public async Task SubjectByKindAndName_ReturnsMatchingSubject()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/graphql",
            new { query = "{ subject(kind: AGENT, name: \"TravelAgent\") { identity { kind name } } }" });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var subject = doc.RootElement.GetProperty("data").GetProperty("subject");

        Assert.Equal(JsonValueKind.Object, subject.ValueKind);
        Assert.Equal("TravelAgent", subject.GetProperty("identity").GetProperty("name").GetString());
    }

    [Fact]
    public async Task SubjectByKindAndName_UnknownName_ReturnsNull()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/graphql",
            new { query = "{ subject(kind: AGENT, name: \"DoesNotExist\") { identity { name } } }" });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var subject = doc.RootElement.GetProperty("data").GetProperty("subject");
        Assert.Equal(JsonValueKind.Null, subject.ValueKind);
    }

    [Fact]
    public async Task RecentRuns_ReturnsSeededRunPointers()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/graphql",
            new { query = "{ recentRuns(count: 10) { runId subjectName verdict timestamp kind } }" });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var runs = doc.RootElement.GetProperty("data").GetProperty("recentRuns");

        Assert.True(runs.GetArrayLength() >= 1,
            $"Expected at least 1 seeded run; body was: {body}");

        var first = runs.EnumerateArray().First();
        Assert.False(string.IsNullOrEmpty(first.GetProperty("runId").GetString()));
        Assert.False(string.IsNullOrEmpty(first.GetProperty("subjectName").GetString()));
        Assert.False(string.IsNullOrEmpty(first.GetProperty("verdict").GetString()));
    }

    [Fact]
    public async Task Run_ByExistingRunId_ReturnsManifest()
    {
        // First, find a real runId from recentRuns; then fetch the manifest.
        using var client = _factory.CreateClient();
        var listResp = await client.PostAsJsonAsync("/graphql",
            new { query = "{ recentRuns(count: 1) { runId } }" });
        listResp.EnsureSuccessStatusCode();
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var runId = listDoc.RootElement.GetProperty("data")
            .GetProperty("recentRuns")[0].GetProperty("runId").GetString();
        Assert.False(string.IsNullOrEmpty(runId));

        var manifestResp = await client.PostAsJsonAsync("/graphql",
            new { query = $"{{ run(runId: \"{runId}\") {{ run {{ runId verdict }} contentHash }} }}" });
        manifestResp.EnsureSuccessStatusCode();

        var body = await manifestResp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var manifest = doc.RootElement.GetProperty("data").GetProperty("run");

        Assert.Equal(JsonValueKind.Object, manifest.ValueKind);
        Assert.Equal(runId, manifest.GetProperty("run").GetProperty("runId").GetString());
        Assert.False(string.IsNullOrEmpty(manifest.GetProperty("contentHash").GetString()));
    }

    [Fact]
    public async Task Run_ByUnknownRunId_ReturnsNull()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/graphql",
            new { query = "{ run(runId: \"definitely-not-a-real-run\") { contentHash } }" });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var run = doc.RootElement.GetProperty("data").GetProperty("run");
        Assert.Equal(JsonValueKind.Null, run.ValueKind);
    }

    [Fact]
    public async Task RunSummary_ReturnsVerdictAndStats()
    {
        using var client = _factory.CreateClient();
        // Locate the seeded run id first.
        var listResp = await client.PostAsJsonAsync("/graphql",
            new { query = "{ recentRuns(count: 1) { runId } }" });
        listResp.EnsureSuccessStatusCode();
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var runId = listDoc.RootElement.GetProperty("data")
            .GetProperty("recentRuns")[0].GetProperty("runId").GetString();
        Assert.False(string.IsNullOrEmpty(runId));

        var summaryResp = await client.PostAsJsonAsync("/graphql",
            new { query = $"{{ runSummary(runId: \"{runId}\") {{ verdict stats {{ total passed failed warnings }} }} }}" });
        summaryResp.EnsureSuccessStatusCode();

        var body = await summaryResp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var summary = doc.RootElement.GetProperty("data").GetProperty("runSummary");

        Assert.Equal("PASS", summary.GetProperty("verdict").GetString());
        var stats = summary.GetProperty("stats");
        Assert.Equal(2, stats.GetProperty("total").GetInt32());
        Assert.Equal(2, stats.GetProperty("passed").GetInt32());
        Assert.Equal(0, stats.GetProperty("failed").GetInt32());
    }

    [Fact]
    public async Task Scenarios_ReturnsSeededScenarioRecords()
    {
        using var client = _factory.CreateClient();
        var listResp = await client.PostAsJsonAsync("/graphql",
            new { query = "{ recentRuns(count: 1) { runId } }" });
        listResp.EnsureSuccessStatusCode();
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var runId = listDoc.RootElement.GetProperty("data")
            .GetProperty("recentRuns")[0].GetProperty("runId").GetString();

        var resp = await client.PostAsJsonAsync("/graphql",
            new { query = $"{{ scenarios(runId: \"{runId}\") {{ id name passed score }} }}" });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var scenarios = doc.RootElement.GetProperty("data").GetProperty("scenarios");

        Assert.Equal(2, scenarios.GetArrayLength());
        var ids = scenarios.EnumerateArray().Select(s => s.GetProperty("id").GetString()).ToList();
        Assert.Contains("scenario-1", ids);
        Assert.Contains("scenario-2", ids);
    }

    [Fact]
    public async Task Scenario_ByRunIdAndScenarioId_ReturnsMatchingRecord()
    {
        using var client = _factory.CreateClient();
        var listResp = await client.PostAsJsonAsync("/graphql",
            new { query = "{ recentRuns(count: 1) { runId } }" });
        listResp.EnsureSuccessStatusCode();
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var runId = listDoc.RootElement.GetProperty("data")
            .GetProperty("recentRuns")[0].GetProperty("runId").GetString();

        var resp = await client.PostAsJsonAsync("/graphql",
            new { query = $"{{ scenario(runId: \"{runId}\", scenarioId: \"scenario-1\") {{ id name passed }} }}" });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var scenario = doc.RootElement.GetProperty("data").GetProperty("scenario");

        Assert.Equal(JsonValueKind.Object, scenario.ValueKind);
        Assert.Equal("scenario-1", scenario.GetProperty("id").GetString());
        Assert.True(scenario.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public async Task Scenario_UnknownScenarioId_ReturnsNull()
    {
        using var client = _factory.CreateClient();
        var listResp = await client.PostAsJsonAsync("/graphql",
            new { query = "{ recentRuns(count: 1) { runId } }" });
        listResp.EnsureSuccessStatusCode();
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var runId = listDoc.RootElement.GetProperty("data")
            .GetProperty("recentRuns")[0].GetProperty("runId").GetString();

        var resp = await client.PostAsJsonAsync("/graphql",
            new { query = $"{{ scenario(runId: \"{runId}\", scenarioId: \"nope\") {{ id }} }}" });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var scenario = doc.RootElement.GetProperty("data").GetProperty("scenario");
        Assert.Equal(JsonValueKind.Null, scenario.ValueKind);
    }
}

/// <summary>
/// Custom <see cref="WebApplicationFactory{TEntryPoint}"/> that swaps
/// <see cref="IOutputStoreReader"/> for a seeded <see cref="InMemoryOutputStore"/>.
/// </summary>
public sealed class SeededMissionControlFactory : WebApplicationFactory<Query>
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IOutputStoreReader>();

            var store = new InMemoryOutputStore();
            store.Initialize("test-solution");

            // Seed: one agent + one workflow + one completed run on the agent.
            SeedAsync(store).GetAwaiter().GetResult();

            services.AddSingleton<IOutputStoreReader>(store);
        });
    }

    private static async Task SeedAsync(InMemoryOutputStore store)
    {
        await store.EnsureSolutionAsync();

        var agent = new SubjectIdentity(SubjectKind.Agent, "TravelAgent");
        var workflow = new SubjectIdentity(SubjectKind.Workflow, "TripPlanner");

        await store.EnsureSubjectAsync(agent);
        await store.EnsureSubjectAsync(workflow);

        var manifest = await store.StartRunAsync(agent, new RunContext(
            EvalProject: "TestProject",
            EvalProjectPath: "samples/Test",
            Harness: "xunit",
            Seed: 42,
            ParentInvocationId: null,
            Kind: "eval"));

        // Seed two scenarios so the scenarios / scenario resolvers have data.
        var emptyMetrics = new Dictionary<string, double>();
        var emptyAssertions = new List<AssertionResult>();
        await store.WriteScenarioResultAsync(manifest.Run.RunId, new ScenarioResult(
            Id: "scenario-1", Name: "Book a flight", Input: "Book ATL→LAX",
            Output: "OK", Passed: true, Score: 0.95,
            Metrics: emptyMetrics, Assertions: emptyAssertions,
            Duration: TimeSpan.FromMilliseconds(1200), EstimatedCost: 0.004));
        await store.WriteScenarioResultAsync(manifest.Run.RunId, new ScenarioResult(
            Id: "scenario-2", Name: "Recommend a hotel", Input: "Hotel near LAX",
            Output: "Hotel X", Passed: true, Score: 0.88,
            Metrics: emptyMetrics, Assertions: emptyAssertions,
            Duration: TimeSpan.FromMilliseconds(900), EstimatedCost: 0.003));

        var summary = new RunSummary(
            SchemaVersion: "1.0",
            RunId: manifest.Run.RunId,
            Verdict: "PASS",
            Stats: new RunStats(Total: 2, Passed: 2, Failed: 0, Warnings: 0),
            Metrics: new Dictionary<string, double> { ["score"] = 0.92 });

        await store.CompleteRunAsync(manifest, summary);
    }
}

#endif
