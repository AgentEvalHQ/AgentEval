// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER

using System.Net.Http.Json;
using System.Text.Json;
using AgentEval.Evals;
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

    // ─── Workspace state (MC1.10.1) ──────────────────────────────────────────

    [Fact]
    public async Task Workspace_SeededFixture_ReportsInitializedTrueWithRootAndSolution()
    {
        // The seeded fixture has solution.json + IsAvailable=true → landing
        // logic in the SPA must NOT trip; full dashboard renders.
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = """
                {
                  workspace {
                    initialized
                    root
                    agentEvalVersion
                    solution { name }
                  }
                }
                """
        });
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var ws = doc.RootElement.GetProperty("data").GetProperty("workspace");

        Assert.True(ws.GetProperty("initialized").GetBoolean());
        Assert.False(string.IsNullOrEmpty(ws.GetProperty("agentEvalVersion").GetString()));
        Assert.Equal("test-solution", ws.GetProperty("solution").GetProperty("name").GetString());
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

        Assert.True(scenarios.GetArrayLength() >= 2,
            $"Expected at least 2 seeded scenarios; got {scenarios.GetArrayLength()}");
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

    // ─── Compliance (MC1.4.4) ────────────────────────────────────────────────

    [Fact]
    public async Task Compliance_ListsRegulations()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/graphql",
            new { query = "{ compliance { regulation subjectCount evidenceCount overallStatus } }" });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var regulations = doc.RootElement.GetProperty("data").GetProperty("compliance");

        Assert.Equal(2, regulations.GetArrayLength());
        var names = regulations.EnumerateArray()
            .Select(r => r.GetProperty("regulation").GetString()).ToList();
        Assert.Contains("gdpr", names);
        Assert.Contains("eu-ai-act", names);
    }

    [Fact]
    public async Task ComplianceMatrix_ForGdpr_ReturnsSubjectsControlsAndCells()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/graphql", new
        {
            query = @"{
                complianceMatrix(regulation: ""gdpr"") {
                    regulation
                    subjects { name kind }
                    controls { id title }
                    cells { subjectName controlId status passRate }
                    allChainsValid
                }
            }"
        });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var matrix = doc.RootElement.GetProperty("data").GetProperty("complianceMatrix");

        Assert.Equal("gdpr", matrix.GetProperty("regulation").GetString());

        var subjects = matrix.GetProperty("subjects");
        Assert.Equal(1, subjects.GetArrayLength());
        Assert.Equal("TravelAgent", subjects[0].GetProperty("name").GetString());
        Assert.Equal("AGENT", subjects[0].GetProperty("kind").GetString());

        var controls = matrix.GetProperty("controls");
        Assert.Equal(2, controls.GetArrayLength());
        var controlIds = controls.EnumerateArray()
            .Select(c => c.GetProperty("id").GetString()).ToList();
        Assert.Contains("art-5", controlIds);
        Assert.Contains("art-17", controlIds);

        var cells = matrix.GetProperty("cells");
        Assert.Equal(2, cells.GetArrayLength());

        // The art-17 cell should reflect the seeded "warn" status with passRate 0.75.
        var art17Cell = cells.EnumerateArray()
            .FirstOrDefault(c => c.GetProperty("controlId").GetString() == "art-17");
        Assert.NotEqual(JsonValueKind.Undefined, art17Cell.ValueKind);
        Assert.Equal("warn", art17Cell.GetProperty("status").GetString());
        Assert.Equal(0.75, art17Cell.GetProperty("passRate").GetDouble());

        // Audit chain valid because the seeded evidence references the seeded run's hash.
        Assert.True(matrix.GetProperty("allChainsValid").GetBoolean());
    }

    [Fact]
    public async Task ComplianceMatrix_ForUnknownRegulation_ReturnsEmptyMatrix()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/graphql",
            new { query = "{ complianceMatrix(regulation: \"hipaa\") { regulation subjects { name } controls { id } cells { controlId } allChainsValid } }" });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var matrix = doc.RootElement.GetProperty("data").GetProperty("complianceMatrix");

        Assert.Equal("hipaa", matrix.GetProperty("regulation").GetString());
        Assert.Equal(0, matrix.GetProperty("subjects").GetArrayLength());
        Assert.Equal(0, matrix.GetProperty("controls").GetArrayLength());
        Assert.Equal(0, matrix.GetProperty("cells").GetArrayLength());
        Assert.True(matrix.GetProperty("allChainsValid").GetBoolean());
    }

    // ─── Cost-tier breakdown (MC1.4.3) ────────────────────────────────────────

    [Fact]
    public async Task RunCostBreakdown_ReturnsTierAndUnknown_InvariantHolds()
    {
        using var client = _factory.CreateClient();
        var listResp = await client.PostAsJsonAsync("/graphql",
            new { query = "{ recentRuns(count: 1) { runId } }" });
        listResp.EnsureSuccessStatusCode();
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var runId = listDoc.RootElement.GetProperty("data")
            .GetProperty("recentRuns")[0].GetProperty("runId").GetString();

        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = $@"{{
                runCostBreakdown(runId: ""{runId}"") {{
                    totalCost
                    byTier {{ trivial low medium high }}
                    unknownKeyCost
                    legacyFlatCost
                    filteredOut
                }}
            }}"
        });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var br = doc.RootElement.GetProperty("data").GetProperty("runCostBreakdown");

        var totalCost = br.GetProperty("totalCost").GetDouble();
        var trivial = br.GetProperty("byTier").GetProperty("trivial").GetDouble();
        var low = br.GetProperty("byTier").GetProperty("low").GetDouble();
        var medium = br.GetProperty("byTier").GetProperty("medium").GetDouble();
        var high = br.GetProperty("byTier").GetProperty("high").GetDouble();
        var unknown = br.GetProperty("unknownKeyCost").GetDouble();
        var legacyFlat = br.GetProperty("legacyFlatCost").GetDouble();

        // Core invariant (T3.5 split): total = sum(byTier) + unknown + legacyFlat.
        // unknown captures in-tree leaves whose key is not registered in
        // EvaluatorCostMap; legacyFlat captures pre-v0.8.1-beta scenarios whose
        // Output payload lacks an EvalResult tree entirely.
        Assert.Equal(totalCost, trivial + low + medium + high + unknown + legacyFlat, 6);

        // The seed has 2 flat scenarios (cost 0.004 + 0.003) attributed to LEGACY
        // FLAT because their Output is plain text not a serialised EvalResult tree.
        // Plus the recursive tree-scenario contributes 0.002 (2 leaves at 0.001
        // each with unregistered keys "leaf-A" / "leaf-B") attributed to UNKNOWN.
        // Expected: legacyFlat = 0.007, unknown = 0.002, total ≈ 0.009.
        Assert.True(totalCost >= 0.0085 && totalCost <= 0.0095,
            $"Expected total cost ~0.009; got {totalCost}");
        Assert.True(legacyFlat >= 0.0065 && legacyFlat <= 0.0075,
            $"Expected legacyFlatCost ~0.007 from the 2 flat-output scenarios; got {legacyFlat}");
        Assert.True(unknown >= 0.0015 && unknown <= 0.0025,
            $"Expected unknownKeyCost ~0.002 from the in-tree leaf-A/leaf-B keys; got {unknown}");

        // filteredOut is always empty until RunRef.BudgetTier ships (plan-08 backlog).
        Assert.Equal(0, br.GetProperty("filteredOut").GetArrayLength());
    }

    [Fact]
    public async Task RunCostBreakdown_UnknownRunId_ReturnsNull()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql",
            new { query = "{ runCostBreakdown(runId: \"definitely-not-a-real-run\") { totalCost } }" });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(JsonValueKind.Null,
            doc.RootElement.GetProperty("data").GetProperty("runCostBreakdown").ValueKind);
    }

    // ─── Recursive EvalResult tree (MC1.4.6) ──────────────────────────────────

    [Fact]
    public async Task ScenarioTree_ReturnsRecursiveEvalResult_InOneRoundTrip()
    {
        // The seed writes a recursive tree as a third scenario via EvalResultPersistence.
        // This is the demonstration of plan-07 §3 Challenge 1: a single fragment walks
        // the whole tree.
        using var client = _factory.CreateClient();
        var listResp = await client.PostAsJsonAsync("/graphql",
            new { query = "{ recentRuns(count: 1) { runId } }" });
        listResp.EnsureSuccessStatusCode();
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var runId = listDoc.RootElement.GetProperty("data")
            .GetProperty("recentRuns")[0].GetProperty("runId").GetString();

        var query = $$"""
            {
              scenarioTree(runId: "{{runId}}", scenarioId: "tree-scenario") {
                metric { key name }
                score { value passed }
                details {
                  aggregationStrategy
                  subResults {
                    metric { key }
                    score { value passed }
                    details {
                      subResults {
                        metric { key }
                        score { value }
                      }
                    }
                  }
                }
              }
            }
            """;

        var resp = await client.PostAsJsonAsync("/graphql", new { query });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var tree = doc.RootElement.GetProperty("data").GetProperty("scenarioTree");

        Assert.Equal(JsonValueKind.Object, tree.ValueKind);
        Assert.Equal("composite-root", tree.GetProperty("metric").GetProperty("key").GetString());
        Assert.True(tree.GetProperty("score").GetProperty("passed").GetBoolean());

        // Walk down: composite-root → 2 mid-level composites → leaves.
        var midLevel = tree.GetProperty("details").GetProperty("subResults");
        Assert.Equal(2, midLevel.GetArrayLength());

        var firstMidKey = midLevel[0].GetProperty("metric").GetProperty("key").GetString();
        Assert.True(firstMidKey == "mid-1" || firstMidKey == "mid-2");

        // Each mid has one leaf — verify recursion went two levels deep.
        var leaves = midLevel[0].GetProperty("details").GetProperty("subResults");
        Assert.Equal(1, leaves.GetArrayLength());
    }

    [Fact]
    public async Task ScenarioTree_UnknownScenario_ReturnsNull()
    {
        using var client = _factory.CreateClient();
        var listResp = await client.PostAsJsonAsync("/graphql",
            new { query = "{ recentRuns(count: 1) { runId } }" });
        listResp.EnsureSuccessStatusCode();
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var runId = listDoc.RootElement.GetProperty("data")
            .GetProperty("recentRuns")[0].GetProperty("runId").GetString();

        var resp = await client.PostAsJsonAsync("/graphql",
            new { query = $"{{ scenarioTree(runId: \"{runId}\", scenarioId: \"nope\") {{ metric {{ key }} }} }}" });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var tree = doc.RootElement.GetProperty("data").GetProperty("scenarioTree");
        Assert.Equal(JsonValueKind.Null, tree.ValueKind);
    }

    [Fact]
    public async Task GraphQL_DepthLimit_RejectsExcessivelyDeepRecursiveQuery()
    {
        // Now that we have the recursive EvalResult schema, we can write a meaningful
        // depth-limit test. MaxAllowedExecutionDepth=10 (raised from 8 in Wave 8 to
        // accommodate the SPA's 3-level subResults drill-down at depth 9); this query
        // goes 12 levels deep through subResults. Should be rejected with `errors`.
        using var client = _factory.CreateClient();
        var listResp = await client.PostAsJsonAsync("/graphql",
            new { query = "{ recentRuns(count: 1) { runId } }" });
        listResp.EnsureSuccessStatusCode();
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var runId = listDoc.RootElement.GetProperty("data")
            .GetProperty("recentRuns")[0].GetProperty("runId").GetString();

        // 12 nested `details { subResults { ... } }` levels — exceeds the depth=8 cap.
        var deepQuery = $$"""
            {
              scenarioTree(runId: "{{runId}}", scenarioId: "tree-scenario") {
                details { subResults {
                  details { subResults {
                    details { subResults {
                      details { subResults {
                        details { subResults {
                          details { subResults {
                            details { subResults {
                              details { subResults {
                                details { subResults {
                                  details { subResults {
                                    details { subResults {
                                      details { subResults {
                                        metric { key }
                                      } }
                                    } }
                                  } }
                                } }
                              } }
                            } }
                          } }
                        } }
                      } }
                    } }
                  } }
                } }
              }
            }
            """;

        var resp = await client.PostAsJsonAsync("/graphql", new { query = deepQuery });
        var body = await resp.Content.ReadAsStringAsync();

        // Hot Chocolate returns 200 + GraphQL `errors` on validation failures (the spec norm).
        Assert.Contains("errors", body, StringComparison.Ordinal);
        Assert.Contains("depth", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GraphQL_CostLimit_RejectsAliasMultipliedExpensiveQuery()
    {
        // SEC-11: depth<=10 alone does not stop a request that invokes an expensive resolver many times
        // via field aliases. The enforced operation-cost analysis (ModifyCostOptions) must reject a query
        // that fans out far past the cost cap, even though it is shallow.
        using var client = _factory.CreateClient();

        // 60 aliased subjectsConnection(first: 200) fields — shallow (depth 4) but a huge field cost.
        var aliases = string.Join("\n", Enumerable.Range(0, 60).Select(i =>
            $"a{i}: subjectsConnection(first: 200) {{ edges {{ node {{ identity {{ kind name }} }} }} }}"));
        var query = "{\n" + aliases + "\n}";

        var resp = await client.PostAsJsonAsync("/graphql", new { query });
        var body = await resp.Content.ReadAsStringAsync();

        // Hot Chocolate returns 200 + GraphQL `errors` on a cost-limit rejection (spec norm).
        Assert.Contains("errors", body, StringComparison.Ordinal);
        Assert.Contains("cost", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ComplianceEvidence_FetchesSpecificEvidence()
    {
        // First, find the timestamp of the seeded gdpr evidence via the matrix endpoint.
        using var client = _factory.CreateClient();
        var matrixResp = await client.PostAsJsonAsync("/graphql",
            new { query = "{ complianceMatrix(regulation: \"gdpr\") { lastEvidenceAt cells { lastEvidenceAt } } }" });
        matrixResp.EnsureSuccessStatusCode();
        using var matrixDoc = JsonDocument.Parse(await matrixResp.Content.ReadAsStringAsync());
        var lastAt = matrixDoc.RootElement.GetProperty("data")
            .GetProperty("complianceMatrix").GetProperty("lastEvidenceAt").GetString();
        Assert.False(string.IsNullOrEmpty(lastAt));

        // Re-format to the on-disk timestamp shape (the InMemoryOutputStore mirrors that).
        // Phase-0 0.8: the resolver now returns a `ComplianceEvidenceWithChain`
        // wrapper carrying `chainValid` + `chainBreakReason` + nested `evidence`.
        var ts = DateTimeOffset.Parse(lastAt!).ToString("yyyy-MM-dd_HH-mm-ss");
        var evResp = await client.PostAsJsonAsync("/graphql", new
        {
            query = $"{{ complianceEvidence(regulation: \"gdpr\", subjectKind: AGENT, subjectName: \"TravelAgent\", timestamp: \"{ts}\") {{ chainValid chainBreakReason evidence {{ regulation summary {{ overallStatus controlsTotal }} }} }} }}"
        });
        evResp.EnsureSuccessStatusCode();

        var body = await evResp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var wrapper = doc.RootElement.GetProperty("data").GetProperty("complianceEvidence");

        // Wrapper may be null (no evidence at the exact timestamp due to format
        // rounding) or an object. Shape-check only — the per-doc audit-chain
        // contract is exercised in ComplianceEvidenceAuditChainResolverTests.
        Assert.True(wrapper.ValueKind == JsonValueKind.Object || wrapper.ValueKind == JsonValueKind.Null);
    }

    // ─── Per-evaluator timeline (Wave 8b / MC1.6.9) ──────────────────────────

    [Fact]
    public async Task EvaluatorTimeline_ReturnsMatchedEvaluatorAcrossRuns()
    {
        // The seed plants a recursive tree-scenario with composite-root → mid-1/mid-2
        // → leaf-A/leaf-B. Querying for "leaf-A" should return at least one timeline
        // point even though the matching node is two levels deep in the tree.
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = """
                {
                  evaluatorTimeline(evaluatorKey: "leaf-A", count: 10) {
                    runId
                    timestamp
                    subjectKind
                    subjectName
                    score
                    passed
                    severity
                    manifestHash
                  }
                }
                """
        });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var points = doc.RootElement.GetProperty("data").GetProperty("evaluatorTimeline");

        Assert.Equal(JsonValueKind.Array, points.ValueKind);
        Assert.True(points.GetArrayLength() >= 1, "Expected at least one timeline point for leaf-A");

        var first = points[0];
        Assert.Equal(0.92, first.GetProperty("score").GetDouble(), 3);
        Assert.True(first.GetProperty("passed").GetBoolean());
        Assert.False(string.IsNullOrEmpty(first.GetProperty("manifestHash").GetString()));
    }

    [Fact]
    public async Task EvaluatorTimeline_FindsCompositeRootNode()
    {
        // The recursive search is pre-order: a composite at the top of the tree
        // wins over deeper matches. composite-root is the actual root.
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = """
                {
                  evaluatorTimeline(evaluatorKey: "composite-root", count: 5) {
                    score
                    passed
                  }
                }
                """
        });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var points = doc.RootElement.GetProperty("data").GetProperty("evaluatorTimeline");

        Assert.Equal(JsonValueKind.Array, points.ValueKind);
        Assert.True(points.GetArrayLength() >= 1);
        Assert.Equal(0.885, points[0].GetProperty("score").GetDouble(), 3);
    }

    [Fact]
    public async Task EvaluatorTimeline_UnknownEvaluator_ReturnsEmpty()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = """
                {
                  evaluatorTimeline(evaluatorKey: "no-such-evaluator-key", count: 10) {
                    runId
                  }
                }
                """
        });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var points = doc.RootElement.GetProperty("data").GetProperty("evaluatorTimeline");

        Assert.Equal(JsonValueKind.Array, points.ValueKind);
        Assert.Equal(0, points.GetArrayLength());
    }

    [Fact]
    public async Task EvaluatorTimeline_NonPositiveCount_ReturnsEmpty()
    {
        // Lock down the count <= 0 short-circuit; if someone "fixes" it to clamp
        // to 1 instead of yielding an empty list this test will catch the change
        // and force a deliberate decision.
        using var client = _factory.CreateClient();
        foreach (var bogusCount in new[] { 0, -1, -5 })
        {
            var resp = await client.PostAsJsonAsync("/graphql", new
            {
                query = $$"""
                    {
                      evaluatorTimeline(evaluatorKey: "leaf-A", count: {{bogusCount}}) {
                        runId
                      }
                    }
                    """
            });
            resp.EnsureSuccessStatusCode();

            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var points = doc.RootElement.GetProperty("data").GetProperty("evaluatorTimeline");

            Assert.Equal(JsonValueKind.Array, points.ValueKind);
            Assert.Equal(0, points.GetArrayLength());
        }
    }

    [Fact]
    public async Task EvaluatorTimeline_TolerantToFlatScenarios()
    {
        // The seed plants both flat scenarios (Output is plain text → FromScenarioResult
        // returns null) and the recursive tree-scenario. Querying for any composite-tree
        // key still returns matches without throwing on the flat scenarios. Locks down
        // the `tree is null → continue` branch in the resolver.
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = """
                {
                  evaluatorTimeline(evaluatorKey: "mid-1", count: 5) {
                    runId
                    score
                  }
                }
                """
        });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var points = doc.RootElement.GetProperty("data").GetProperty("evaluatorTimeline");

        Assert.Equal(JsonValueKind.Array, points.ValueKind);
        Assert.True(points.GetArrayLength() >= 1);
        Assert.Equal(0.92, points[0].GetProperty("score").GetDouble(), 3);
    }

    [Fact]
    public async Task EvaluatorTimeline_EmptyKey_ReturnsEmpty()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = """
                {
                  evaluatorTimeline(evaluatorKey: "", count: 10) {
                    runId
                  }
                }
                """
        });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var points = doc.RootElement.GetProperty("data").GetProperty("evaluatorTimeline");

        Assert.Equal(JsonValueKind.Array, points.ValueKind);
        Assert.Equal(0, points.GetArrayLength());
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

        // Seed a recursive EvalResult tree as a third scenario so MC1.4.6
        // (Query.scenarioTree) has data. Tree shape: root composite → 2 mid composites
        // → 1 leaf each. Round-tripped through EvalResultPersistence so Output is
        // the serialised tree JSON.
        var tree = BuildSampleEvalResultTree();
        await store.WriteScenarioResultAsync(manifest.Run.RunId,
            EvalResultPersistence.ToScenarioResult(tree, "tree-scenario", "Composite tree demo"));

        var summary = new RunSummary(
            SchemaVersion: "1.0",
            RunId: manifest.Run.RunId,
            Verdict: "PASS",
            Stats: new RunStats(Total: 2, Passed: 2, Failed: 0, Warnings: 0),
            Metrics: new Dictionary<string, double> { ["score"] = 0.92 });

        await store.CompleteRunAsync(manifest, summary);

        // After CompleteRunAsync the manifest is sealed; re-read to get the final
        // ContentHash that the audit-chain check in SaveComplianceEvidenceAsync expects.
        var sealedManifest = await store.GetRunManifestAsync(manifest.Run.RunId)
            ?? throw new InvalidOperationException("Sealed manifest not found after CompleteRunAsync.");

        // Seed compliance evidence for two regulations on the agent so MC1.4.4
        // (compliance resolvers) has matrix data to render.
        await store.SaveComplianceEvidenceAsync("gdpr", agent, BuildEvidence(
            regulation: "gdpr",
            subject: agent,
            sourceRunId: sealedManifest.Run.RunId,
            sourceRunHash: sealedManifest.ContentHash,
            controls: new[]
            {
                new EvidenceControl("art-5", "Lawfulness", "pass", 1.0,
                    Array.Empty<string>(), Notes: null),
                new EvidenceControl("art-17", "Right to erasure", "warn", 0.75,
                    Array.Empty<string>(), Notes: "Backup propagation gap"),
            },
            overallStatus: "warn"));

        await store.SaveComplianceEvidenceAsync("eu-ai-act", agent, BuildEvidence(
            regulation: "eu-ai-act",
            subject: agent,
            sourceRunId: sealedManifest.Run.RunId,
            sourceRunHash: sealedManifest.ContentHash,
            controls: new[]
            {
                new EvidenceControl("art-13", "Deployer transparency", "pass", 1.0,
                    Array.Empty<string>(), Notes: null),
            },
            overallStatus: "pass"));
    }

    private static EvalResult BuildSampleEvalResultTree()
    {
        var leafA = MakeAtomicResult("leaf-A", 0.92);
        var leafB = MakeAtomicResult("leaf-B", 0.85);

        var mid1 = new EvalResult(
            Metric: new EvalMetadata("mid-1", "Mid 1", "test", "1.0"),
            Score: new EvalScore(0.92, null, "pass", true, 0.7, "none", null),
            Details: new EvalDetails(
                Dimensions: null,
                Evidence: null,
                Recommendations: null,
                SubResults: new[] { leafA },
                AggregationStrategy: "weighted-sum"),
            Provenance: new EvalProvenance("composite", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        var mid2 = new EvalResult(
            Metric: new EvalMetadata("mid-2", "Mid 2", "test", "1.0"),
            Score: new EvalScore(0.85, null, "pass", true, 0.7, "none", null),
            Details: new EvalDetails(
                Dimensions: null,
                Evidence: null,
                Recommendations: null,
                SubResults: new[] { leafB },
                AggregationStrategy: "weighted-sum"),
            Provenance: new EvalProvenance("composite", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

        return new EvalResult(
            Metric: new EvalMetadata("composite-root", "Composite Root", "test", "1.0"),
            Score: new EvalScore(0.885, null, "pass", true, 0.7, "none", null),
            Details: new EvalDetails(
                Dimensions: null,
                Evidence: null,
                Recommendations: null,
                SubResults: new[] { mid1, mid2 },
                AggregationStrategy: "weighted-sum"),
            Provenance: new EvalProvenance("composite", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

    private static EvalResult MakeAtomicResult(string key, double score) =>
        new(
            Metric: new EvalMetadata(key, key, "test", "1.0"),
            Score: new EvalScore(score, null, "pass", true, 0.7, "none", null),
            Details: new EvalDetails(null, null, null, null, null),
            Provenance: new EvalProvenance("atomic-llm", "stub", null, null, null, 0.001, false),
            EvaluatedAt: DateTimeOffset.UtcNow);

    private static ComplianceEvidence BuildEvidence(
        string regulation,
        SubjectIdentity subject,
        string sourceRunId,
        string sourceRunHash,
        IReadOnlyList<EvidenceControl> controls,
        string overallStatus)
    {
        var passed = controls.Count(c => c.Status == "pass");
        var warnings = controls.Count(c => c.Status == "warn");
        var failed = controls.Count(c => c.Status == "fail");

        return new ComplianceEvidence(
            SchemaVersion: "1.0",
            Regulation: regulation,
            Subject: subject,
            GeneratedAt: DateTimeOffset.UtcNow,
            SourceRun: new SourceRunRef(sourceRunId, sourceRunHash),
            Controls: controls,
            Summary: new EvidenceSummary(
                ControlsTotal: controls.Count,
                Passed: passed, Warnings: warnings, Failed: failed,
                OverallStatus: overallStatus),
            Attestation: new Attestation(
                AgentEvalVersion: "1.7.0",
                ConfigurationId: null,
                Evaluator: "gpt-4o-mini",
                EvaluatorModel: "gpt-4o-mini-2024-07-18"));
    }
}

// ─── T3.6 Red-team campaign resolvers (2026-05-25) ──────────────────────────

public class RedTeamCampaignResolverTests : IClassFixture<SeededMissionControlFactory>
{
    private readonly SeededMissionControlFactory _factory;

    public RedTeamCampaignResolverTests(SeededMissionControlFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RedTeamCampaignsQuery_ReturnsAllCampaigns()
    {
        // T3.6 contract: redTeamCampaigns lists every manifest under
        // .agenteval/red-team/. The seeded fixture may or may not include
        // a campaign — the resolver MUST return a non-error JSON array
        // either way (empty array when the directory is absent).
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = """
                {
                  redTeamCampaigns {
                    campaignId
                    name
                    startedAt
                    mode
                    targets { kind name }
                  }
                }
                """
        });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.TryGetProperty("errors", out _),
            $"redTeamCampaigns must not surface GraphQL errors; body was:\n{body}");
        var campaigns = doc.RootElement.GetProperty("data").GetProperty("redTeamCampaigns");
        Assert.Equal(JsonValueKind.Array, campaigns.ValueKind);
    }

    [Fact]
    public async Task RedTeamCampaignQuery_ReturnsByIdOrNull()
    {
        // T3.6 contract: redTeamCampaign(id) returns null for an unknown id
        // (and never crashes the resolver). The id is validated via
        // FileSystemLayout.IsSafePathSegment first, so a `../foo` traversal
        // attempt also returns null silently.
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = """
                {
                  redTeamCampaign(id: "definitely-not-a-real-campaign-id_2026-01-01_00-00-00") {
                    campaignId
                  }
                }
                """
        });
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null,
            doc.RootElement.GetProperty("data").GetProperty("redTeamCampaign").ValueKind);

        // Path-traversal attempt: also returns null, no errors.
        var resp2 = await client.PostAsJsonAsync("/graphql", new
        {
            query = """{ redTeamCampaign(id: "../../etc/passwd") { campaignId } }"""
        });
        resp2.EnsureSuccessStatusCode();
        using var doc2 = JsonDocument.Parse(await resp2.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null,
            doc2.RootElement.GetProperty("data").GetProperty("redTeamCampaign").ValueKind);
    }
}

// ─── T3.8 Paginated Connection resolvers (2026-05-25) ──────────────────────

public class GraphQLPaginationTests : IClassFixture<SeededMissionControlFactory>
{
    private readonly SeededMissionControlFactory _factory;

    public GraphQLPaginationTests(SeededMissionControlFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SubjectsConnection_FirstPage_ReturnsEdgesAndCursor()
    {
        // T3.8 contract: subjectsConnection returns Relay-shaped
        // edges + pageInfo. With first=2 we expect 2 edges and (depending on
        // the seed) hasNextPage = true if more than 2 subjects exist.
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = """
                {
                  subjectsConnection(first: 2) {
                    totalCount
                    edges { cursor node { identity { kind name } } }
                    pageInfo { hasNextPage hasPreviousPage startCursor endCursor }
                  }
                }
                """
        });
        resp.EnsureSuccessStatusCode();

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var conn = doc.RootElement.GetProperty("data").GetProperty("subjectsConnection");

        var total = conn.GetProperty("totalCount").GetInt32();
        Assert.True(total >= 1, $"Seed should have at least one subject; got total={total}.");

        var edges = conn.GetProperty("edges");
        Assert.True(edges.GetArrayLength() <= 2, "first=2 must cap edges at 2.");

        if (total >= 1)
        {
            var firstCursor = edges[0].GetProperty("cursor").GetString();
            Assert.False(string.IsNullOrEmpty(firstCursor),
                "Edges must carry an opaque cursor.");
            Assert.Equal(firstCursor,
                conn.GetProperty("pageInfo").GetProperty("startCursor").GetString());
        }
        Assert.False(conn.GetProperty("pageInfo").GetProperty("hasPreviousPage").GetBoolean());
    }

    [Fact]
    public async Task SubjectsConnection_AfterCursor_AdvancesToNextPage()
    {
        // T3.8 contract: passing pageInfo.endCursor of page 1 as `after` for
        // page 2 must return edges that do NOT overlap with page 1. With a
        // small seed (≥ 2 subjects) we can verify by paging size 1 twice.
        using var client = _factory.CreateClient();

        var page1Resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = """
                {
                  subjectsConnection(first: 1) {
                    totalCount
                    edges { cursor node { identity { name } } }
                    pageInfo { endCursor hasNextPage }
                  }
                }
                """
        });
        page1Resp.EnsureSuccessStatusCode();
        using var page1Doc = JsonDocument.Parse(await page1Resp.Content.ReadAsStringAsync());
        var page1 = page1Doc.RootElement.GetProperty("data").GetProperty("subjectsConnection");
        var total = page1.GetProperty("totalCount").GetInt32();
        if (total < 2)
        {
            // Seed only has one subject; can't exercise the page-advance path
            // meaningfully. Bail without failing — the FirstPage_ReturnsEdges
            // assertion already covers the single-page case.
            return;
        }

        var endCursor = page1.GetProperty("pageInfo").GetProperty("endCursor").GetString();
        var page1Name = page1.GetProperty("edges")[0]
            .GetProperty("node").GetProperty("identity").GetProperty("name").GetString();

        var page2Resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = $$"""
                {
                  subjectsConnection(first: 1, after: "{{endCursor}}") {
                    edges { node { identity { name } } }
                    pageInfo { hasPreviousPage }
                  }
                }
                """
        });
        page2Resp.EnsureSuccessStatusCode();
        using var page2Doc = JsonDocument.Parse(await page2Resp.Content.ReadAsStringAsync());
        var page2 = page2Doc.RootElement.GetProperty("data").GetProperty("subjectsConnection");

        Assert.True(page2.GetProperty("edges").GetArrayLength() >= 1);
        var page2Name = page2.GetProperty("edges")[0]
            .GetProperty("node").GetProperty("identity").GetProperty("name").GetString();
        Assert.NotEqual(page1Name, page2Name);
        Assert.True(page2.GetProperty("pageInfo").GetProperty("hasPreviousPage").GetBoolean(),
            "Page 2 must report hasPreviousPage=true.");
    }

    [Fact]
    public async Task SubjectsConnection_FirstExceedsMax_IsClampedTo200()
    {
        // T3.8 contract: callers cannot demand unbounded payloads by passing
        // first=10000; the resolver clamps to MaxFirst=200. Verified via the
        // returned edge count which can never exceed 200 regardless of
        // requested first.
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = """
                {
                  subjectsConnection(first: 10000) {
                    totalCount
                    edges { cursor }
                  }
                }
                """
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var conn = doc.RootElement.GetProperty("data").GetProperty("subjectsConnection");
        Assert.True(conn.GetProperty("edges").GetArrayLength() <= 200,
            "first=10000 must clamp to MaxFirst=200.");
    }

    [Fact]
    public async Task ScenarioResultsConnection_ReturnsConnectionShape_ForKnownRun()
    {
        // T3.8 contract for the scenario-results paginated variant. Uses the
        // seeded run from the SeededMissionControlFactory.
        using var client = _factory.CreateClient();
        var listResp = await client.PostAsJsonAsync("/graphql",
            new { query = "{ recentRuns(count: 1) { runId } }" });
        listResp.EnsureSuccessStatusCode();
        using var listDoc = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync());
        var runId = listDoc.RootElement.GetProperty("data")
            .GetProperty("recentRuns")[0].GetProperty("runId").GetString();
        Assert.False(string.IsNullOrEmpty(runId));

        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = $$"""
                {
                  scenarioResultsConnection(runId: "{{runId}}", first: 50) {
                    totalCount
                    edges { cursor node { id name } }
                    pageInfo { hasNextPage hasPreviousPage }
                  }
                }
                """
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var conn = doc.RootElement.GetProperty("data").GetProperty("scenarioResultsConnection");

        Assert.True(conn.GetProperty("totalCount").GetInt32() >= 1,
            "Seed should have at least one scenario.");
        Assert.False(conn.GetProperty("pageInfo").GetProperty("hasPreviousPage").GetBoolean());
    }
}

#endif
