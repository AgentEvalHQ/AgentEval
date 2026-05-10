// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

#if NET10_0_OR_GREATER

using System.Net.Http.Json;
using System.Text.Json;
using AgentEval.Cli.Commands;
using AgentEval.MissionControl.GraphQL;
using AgentEval.Output;
using AgentEval.Tests.MissionControl.Fixtures;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AgentEval.Tests.MissionControl;

/// <summary>
/// Phase 1 end-to-end quality gate. Builds a comprehensive fixture
/// workspace via <see cref="MissionControlFixtureBuilder"/> (real
/// FileSystemOutputStore writes, real audit chain), then exercises the
/// full Mission Control read stack against it:
/// <list type="bullet">
///   <item><c>agenteval doctor</c> validates the on-disk shape.</item>
///   <item>Every primary GraphQL resolver returns realistic data.</item>
///   <item>Cross-time queries (<c>recentRuns</c>, <c>evaluatorTimeline</c>)
///     traverse multiple dates correctly.</item>
///   <item>Audit-chain validation flips false on filesystem tampering.</item>
/// </list>
/// </summary>
public class MissionControlEndToEndTests : IClassFixture<EndToEndFixture>
{
    private readonly EndToEndFixture _fixture;

    public MissionControlEndToEndTests(EndToEndFixture fixture) => _fixture = fixture;

    // Manual-only: materialises the fixture to a stable path for human
    // inspection. Skipped by default so it doesn't pollute the test runner,
    // leave artefacts in %TEMP%, or run unnecessarily as part of the suite.
    // To execute: temporarily delete the Skip argument and run via
    //   dotnet test --filter "FullyQualifiedName~Materialize"
    [Fact(Skip = "Manual-only — materialises a fixture to %TEMP% for human inspection.")]
    public async Task Materialize_FixtureToTempForReview()
    {
        var target = Path.Combine(Path.GetTempPath(), "agenteval-review-fixture");
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.CreateDirectory(target);
        var m = await MissionControlFixtureBuilder.BuildAsync(target);
        Console.WriteLine($"WorkspaceRoot:  {m.WorkspaceRoot}");
        Console.WriteLine($"AgentEvalDir:   {m.AgentEvalDir}");
        Console.WriteLine($"RunIds:         {m.RunIds.Count}");
        Console.WriteLine($"EvidenceRefs:   {m.EvidenceRefs.Count}");
        Console.WriteLine($"CampaignId:     {m.CampaignId}");
        Console.WriteLine($"LegacyBaseline: {m.LegacyBaselinePath}");
    }

    // ─── doctor ──────────────────────────────────────────────────────────────

    [Fact]
    public void RedTeamCampaign_FilesPersisted()
    {
        // MC1.4.5 (red-team resolvers) is ⬜, so we don't query via GraphQL.
        // But the fixture's StartRedTeamCampaignAsync + Complete... pair
        // produces real on-disk artefacts; verify they exist + parse as JSON
        // so a future MC1.4.5 regressing the on-disk shape would surface here.
        var redTeamRoot = Path.Combine(_fixture.Manifest.AgentEvalDir, "red-team");
        Assert.True(Directory.Exists(redTeamRoot), $"red-team dir missing at {redTeamRoot}.");

        var manifests = Directory.GetFiles(redTeamRoot, "manifest.json", SearchOption.AllDirectories);
        var findings = Directory.GetFiles(redTeamRoot, "findings.json", SearchOption.AllDirectories);
        Assert.Single(manifests);
        Assert.Single(findings);

        // Parses + has expected fields.
        using var mDoc = JsonDocument.Parse(File.ReadAllText(manifests[0]));
        Assert.True(mDoc.RootElement.TryGetProperty("campaignId", out _),
            "Red-team manifest must include campaignId.");

        using var fDoc = JsonDocument.Parse(File.ReadAllText(findings[0]));
        Assert.True(fDoc.RootElement.TryGetProperty("items", out var items));
        Assert.Equal(3, items.GetArrayLength());
    }

    [Fact]
    public void LegacyLongmemevalBaseline_FileExists()
    {
        // MC1.10.2 (`--legacy-import`) is ⬜. The fixture seeds the file so
        // a future implementation has a known anchor; verify the path exists
        // + parses, so the seed shape doesn't drift before MC1.10.2 lands.
        Assert.True(File.Exists(_fixture.Manifest.LegacyBaselinePath));
        using var doc = JsonDocument.Parse(File.ReadAllText(_fixture.Manifest.LegacyBaselinePath));
        Assert.Equal("longmemeval-fixture",
            doc.RootElement.GetProperty("name").GetString());
        Assert.True(doc.RootElement.GetProperty("scenarios").GetArrayLength() >= 1);
    }

    [Fact]
    public async Task AgentEvalDoctor_AgainstFixture_ExitsZero()
    {
        // The fixture's audit chains must validate via the existing
        // `agenteval doctor` workspace check. Anything else means the
        // fixture writes broke audit-chain integrity.
        // DoctorCommand expects the OUTER workspace root (it builds
        // `.agenteval/` itself).
        var exit = await DoctorCommand.RunAsync(_fixture.OuterWorkspaceRoot);
        Assert.Equal(0, exit);
    }

    // ─── workspace state ────────────────────────────────────────────────────

    [Fact]
    public async Task Workspace_Initialized_True()
    {
        using var client = _fixture.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = "{ workspace { initialized agentEvalVersion } }"
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var ws = doc.RootElement.GetProperty("data").GetProperty("workspace");
        Assert.True(ws.GetProperty("initialized").GetBoolean());
        Assert.False(string.IsNullOrEmpty(ws.GetProperty("agentEvalVersion").GetString()),
            "agentEvalVersion must be a non-empty string.");
    }

    // ─── subjects ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Subjects_ReturnsBothFixtureSubjects()
    {
        using var client = _fixture.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = "{ subjects { identity { kind name } } }"
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var subjects = doc.RootElement.GetProperty("data").GetProperty("subjects");

        Assert.Equal(JsonValueKind.Array, subjects.ValueKind);
        var names = new HashSet<string>();
        foreach (var s in subjects.EnumerateArray())
            names.Add(s.GetProperty("identity").GetProperty("name").GetString()!);
        Assert.Contains("TravelAgent", names);
        Assert.Contains("TripPlanner", names);
    }

    [Fact]
    public async Task Subject_TravelAgent_Resolves()
    {
        using var client = _fixture.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = "{ subject(kind: AGENT, name: \"TravelAgent\") { identity { kind name } } }"
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var subj = doc.RootElement.GetProperty("data").GetProperty("subject");
        Assert.Equal("TravelAgent", subj.GetProperty("identity").GetProperty("name").GetString());
    }

    // ─── recent runs (newest-first across 3 dates) ──────────────────────────

    [Fact]
    public async Task RecentRuns_ReturnsAllFixtureRuns()
    {
        using var client = _fixture.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = "{ recentRuns(count: 50) { runId timestamp } }"
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var runs = doc.RootElement.GetProperty("data").GetProperty("recentRuns");

        // Fixture seeds exactly 9 runs (6 normal + 1 Foundry + 2 memory).
        // (Ordering is not asserted here because fixture manifests cluster
        // at build-time within milliseconds — see builder remarks. A
        // dedicated InMemoryOutputStore-based ordering test elsewhere
        // is a better signal.)
        Assert.Equal(9, runs.GetArrayLength());

        // Every run-id surfaced must be among the fixture's tracked ids.
        var fixtureRunIds = _fixture.Manifest.RunIds.Values.ToHashSet(StringComparer.Ordinal);
        foreach (var run in runs.EnumerateArray())
        {
            var id = run.GetProperty("runId").GetString();
            Assert.NotNull(id);
            Assert.Contains(id, fixtureRunIds);
        }
    }

    // ─── compliance matrix ──────────────────────────────────────────────────

    [Fact]
    public async Task ComplianceMatrix_Gdpr_HasCellsAndChainsValid()
    {
        using var client = _fixture.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = """
                {
                  complianceMatrix(regulation: "gdpr") {
                    allChainsValid
                    subjects { name kind }
                    cells { subjectName controlId status }
                  }
                }
                """
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var m = doc.RootElement.GetProperty("data").GetProperty("complianceMatrix");

        Assert.True(m.GetProperty("allChainsValid").GetBoolean(),
            "Audit chain must validate clean against the fixture's sealed manifests.");
        // Fixture seeds GDPR evidence for 2 subjects × 2 dates (T1, T2).
        // ComplianceMatrixService picks LATEST-evidence-per-subject → uses
        // T2 for both subjects. T2 evidence has 4 controls each.
        // Therefore exactly 2 subjects × 4 controls = 8 cells.
        Assert.Equal(2, m.GetProperty("subjects").GetArrayLength());
        Assert.Equal(8, m.GetProperty("cells").GetArrayLength());
    }

    [Fact]
    public async Task ComplianceMatrix_EuAiAct_HasAtLeastOneCell()
    {
        using var client = _fixture.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = """
                {
                  complianceMatrix(regulation: "eu-ai-act") {
                    allChainsValid
                    cells { controlId status }
                  }
                }
                """
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var m = doc.RootElement.GetProperty("data").GetProperty("complianceMatrix");
        Assert.True(m.GetProperty("allChainsValid").GetBoolean());
        // Fixture seeds EU AI Act evidence for TripPlanner only at T2, with
        // 3 controls. So exactly 1 subject × 3 controls = 3 cells.
        Assert.Equal(3, m.GetProperty("cells").GetArrayLength());
    }

    [Fact]
    public async Task Compliance_ListsBothRegulations()
    {
        using var client = _fixture.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = "{ compliance { regulation evidenceCount } }"
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var regs = doc.RootElement.GetProperty("data").GetProperty("compliance");
        var names = new HashSet<string>();
        foreach (var r in regs.EnumerateArray())
            names.Add(r.GetProperty("regulation").GetString()!);
        Assert.Contains("gdpr", names);
        Assert.Contains("eu-ai-act", names);
    }

    // ─── recursive scenario tree ────────────────────────────────────────────

    [Fact]
    public async Task EvaluatorTimeline_MemRecallAccuracy_ReturnsTwoPoints()
    {
        // The fixture seeds exactly 2 memory-eval runs (TravelAgent at T1
        // and T2), each with one `mem_recall_accuracy` scenario.
        using var client = _fixture.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = "{ evaluatorTimeline(evaluatorKey: \"mem_recall_accuracy\", count: 50) { runId score } }"
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var pts = doc.RootElement.GetProperty("data").GetProperty("evaluatorTimeline");

        Assert.Equal(2, pts.GetArrayLength());
    }

    [Fact]
    public async Task ScenarioTree_OfFoundryRun_ReturnsRecursiveTree()
    {
        var foundryRunId = _fixture.GetRunId(MissionControlFixtureBuilder.TripPlanner,
            MissionControlFixtureBuilder.T2, FixtureRunKind.Foundry);

        using var client = _fixture.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = $$"""
                {
                  scenarioTree(runId: "{{foundryRunId}}", scenarioId: "agentic-execution-suite") {
                    metric { key }
                    details {
                      subResults {
                        metric { key }
                        details { subResults { metric { key } } }
                      }
                    }
                  }
                }
                """
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var tree = doc.RootElement.GetProperty("data").GetProperty("scenarioTree");
        Assert.Equal("composite-root", tree.GetProperty("metric").GetProperty("key").GetString());

        var pillars = tree.GetProperty("details").GetProperty("subResults");
        Assert.Equal(3, pillars.GetArrayLength());
        var firstLeaves = pillars[0].GetProperty("details").GetProperty("subResults");
        Assert.True(firstLeaves.GetArrayLength() >= 1);
    }

    // ─── evaluator timeline (across dates) ──────────────────────────────────

    [Fact]
    public async Task EvaluatorTimeline_CompositeRoot_CoversAllNormalRuns()
    {
        // Every normal-eval run has a `composite-root` recursive tree, so
        // the timeline should cover all 6 normal runs (2 subjects × 3 dates)
        // plus the Foundry run (which also uses key=composite-root) = 7.
        // (Ordering is not asserted here — see builder remarks about
        // build-time-clustered manifest timestamps.)
        using var client = _fixture.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = "{ evaluatorTimeline(evaluatorKey: \"composite-root\", count: 50) { runId timestamp score passed } }"
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var pts = doc.RootElement.GetProperty("data").GetProperty("evaluatorTimeline");

        Assert.Equal(7, pts.GetArrayLength());
    }

    // ─── cost-tier breakdown ────────────────────────────────────────────────

    [Fact]
    public async Task RunCostBreakdown_NormalRun_ReturnsTotalAndTiers()
    {
        var runId = _fixture.GetRunId(MissionControlFixtureBuilder.TravelAgent,
            MissionControlFixtureBuilder.T2, FixtureRunKind.Eval);

        using var client = _fixture.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = $$"""
                {
                  runCostBreakdown(runId: "{{runId}}") {
                    totalCost
                    byTier { trivial low medium high }
                    unknownKeyCost
                  }
                }
                """
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var b = doc.RootElement.GetProperty("data").GetProperty("runCostBreakdown");

        var total = b.GetProperty("totalCost").GetDouble();
        var sum =
            b.GetProperty("byTier").GetProperty("trivial").GetDouble() +
            b.GetProperty("byTier").GetProperty("low").GetDouble() +
            b.GetProperty("byTier").GetProperty("medium").GetDouble() +
            b.GetProperty("byTier").GetProperty("high").GetDouble() +
            b.GetProperty("unknownKeyCost").GetDouble();

        // Invariant: total == sum(byTier) + unknown. The resolver computes
        // total this way, so this is a guard against future refactors that
        // might compute total separately.
        Assert.Equal(total, sum, 6);

        // The fixture's normal-eval run has 4 flat scenarios (each
        // estimatedCost=0.003 → 0.012 in `unknown` since flat scenarios are
        // not tree-attributed) plus the composite tree's 2 LLM leaves
        // (cost 0.0009 + 0.0011 = 0.002). composite-root, policy_compliance,
        // response_quality are not in EvaluatorCostMap, so they go to
        // `unknown` as well. Total should be ≥ 0.012 + 0.002 = 0.014.
        Assert.True(total >= 0.013, $"total cost ({total}) is suspiciously low — expected ≥ 0.013.");
    }

    // ─── run / runSummary / scenarios / scenario resolver coverage ─────────

    [Fact]
    public async Task Run_ByRunId_ReturnsManifestWithMatchingSubject()
    {
        var runId = _fixture.GetRunId(MissionControlFixtureBuilder.TravelAgent,
            MissionControlFixtureBuilder.T2, FixtureRunKind.Eval);

        using var client = _fixture.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = $$"""
                {
                  run(runId: "{{runId}}") {
                    run { runId verdict kind }
                    subject { kind name }
                    contentHash
                  }
                }
                """
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var r = doc.RootElement.GetProperty("data").GetProperty("run");
        Assert.Equal(runId, r.GetProperty("run").GetProperty("runId").GetString());
        Assert.Equal("PASS", r.GetProperty("run").GetProperty("verdict").GetString());
        Assert.Equal("TravelAgent", r.GetProperty("subject").GetProperty("name").GetString());
        Assert.StartsWith("sha256:", r.GetProperty("contentHash").GetString());
    }

    [Fact]
    public async Task RunSummary_ByRunId_ReturnsStatsAndMetrics()
    {
        var runId = _fixture.GetRunId(MissionControlFixtureBuilder.TravelAgent,
            MissionControlFixtureBuilder.T2, FixtureRunKind.Eval);

        using var client = _fixture.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = $$"""
                {
                  runSummary(runId: "{{runId}}") {
                    verdict
                    stats { total passed failed warnings }
                  }
                }
                """
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var s = doc.RootElement.GetProperty("data").GetProperty("runSummary");
        Assert.Equal("PASS", s.GetProperty("verdict").GetString());
        Assert.Equal(5, s.GetProperty("stats").GetProperty("total").GetInt32());
        Assert.Equal(5, s.GetProperty("stats").GetProperty("passed").GetInt32());
    }

    [Fact]
    public async Task Scenarios_OfNormalRun_YieldsFourFlatPlusComposite()
    {
        var runId = _fixture.GetRunId(MissionControlFixtureBuilder.TravelAgent,
            MissionControlFixtureBuilder.T2, FixtureRunKind.Eval);

        using var client = _fixture.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = $$"""{ scenarios(runId: "{{runId}}") { id passed score } }"""
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var arr = doc.RootElement.GetProperty("data").GetProperty("scenarios");
        Assert.Equal(5, arr.GetArrayLength());
        var ids = new HashSet<string>();
        foreach (var s in arr.EnumerateArray())
            ids.Add(s.GetProperty("id").GetString()!);
        Assert.Contains("s-1", ids);
        Assert.Contains("s-4", ids);
        Assert.Contains("composite-tree", ids);
    }

    [Fact]
    public async Task Scenario_BySingleId_ReturnsThatScenario()
    {
        var runId = _fixture.GetRunId(MissionControlFixtureBuilder.TravelAgent,
            MissionControlFixtureBuilder.T2, FixtureRunKind.Eval);

        using var client = _fixture.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = $$"""
                {
                  scenario(runId: "{{runId}}", scenarioId: "s-2") {
                    id name passed score
                  }
                }
                """
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var s = doc.RootElement.GetProperty("data").GetProperty("scenario");
        Assert.NotEqual(JsonValueKind.Null, s.ValueKind);
        Assert.Equal("s-2", s.GetProperty("id").GetString());
        Assert.Equal("Plan trip 2", s.GetProperty("name").GetString());
        Assert.True(s.GetProperty("passed").GetBoolean());
    }

    // ─── negative paths ──────────────────────────────────────────────────────

    [Fact]
    public async Task Run_UnknownRunId_ReturnsNull()
    {
        using var client = _fixture.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = """{ run(runId: "2099-12-31_23-59-59_deadbeef") { run { runId } } }"""
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null,
            doc.RootElement.GetProperty("data").GetProperty("run").ValueKind);
    }

    [Fact]
    public async Task RunCostBreakdown_UnknownRunId_ReturnsNull()
    {
        using var client = _fixture.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = """{ runCostBreakdown(runId: "2099-12-31_23-59-59_deadbeef") { totalCost } }"""
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null,
            doc.RootElement.GetProperty("data").GetProperty("runCostBreakdown").ValueKind);
    }

    [Fact]
    public async Task ScenarioTree_FlatScenarioId_ReturnsNull()
    {
        // Flat scenarios (s-1..s-4) have a plain string Output, not a JSON
        // EvalResult tree. ScenarioTree must return null for them so the SPA
        // can fall back to the flat shape. Guards against EvalResultPersistence
        // ever silently parsing arbitrary strings as a tree.
        var runId = _fixture.GetRunId(MissionControlFixtureBuilder.TravelAgent,
            MissionControlFixtureBuilder.T2, FixtureRunKind.Eval);

        using var client = _fixture.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = $$"""
                {
                  scenarioTree(runId: "{{runId}}", scenarioId: "s-1") { metric { key } }
                }
                """
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null,
            doc.RootElement.GetProperty("data").GetProperty("scenarioTree").ValueKind);
    }

    [Fact]
    public async Task RecentRuns_ReturnsNewestFirst()
    {
        // Production guarantee: recentRuns is newest-first. The fixture's
        // 9 runs are completed sequentially, so the build-order matches
        // chronological order. This is the only E2E assertion of the
        // resolver's ordering contract — earlier tests deliberately
        // skipped it because ordering is mostly verified at the
        // InMemoryOutputStore unit level, but reverse-of-file-order can
        // and has silently broken on past refactors.
        using var client = _fixture.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = "{ recentRuns(count: 50) { runId timestamp } }"
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var runs = doc.RootElement.GetProperty("data").GetProperty("recentRuns");
        var timestamps = runs.EnumerateArray()
            .Select(r => DateTimeOffset.Parse(r.GetProperty("timestamp").GetString()!))
            .ToList();
        for (var i = 1; i < timestamps.Count; i++)
        {
            Assert.True(timestamps[i - 1] >= timestamps[i],
                $"recentRuns[{i - 1}].timestamp ({timestamps[i - 1]:O}) must be ≥ recentRuns[{i}].timestamp ({timestamps[i]:O}).");
        }
    }

    // ─── compliance evidence drill-through ──────────────────────────────────

    [Fact]
    public async Task ComplianceEvidence_RoundTripsASingleRecord()
    {
        var ev = _fixture.Manifest.EvidenceRefs.First(e => e.Regulation == "gdpr");

        using var client = _fixture.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = $$"""
                {
                  complianceEvidence(
                    regulation: "{{ev.Regulation}}",
                    subjectKind: {{ev.Subject.Kind.ToString().ToUpperInvariant()}},
                    subjectName: "{{ev.Subject.Name}}",
                    timestamp: "{{ev.Timestamp}}"
                  ) {
                    regulation
                    sourceRun { runId manifestHash }
                    summary { controlsTotal overallStatus }
                  }
                }
                """
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var e = doc.RootElement.GetProperty("data").GetProperty("complianceEvidence");
        Assert.NotEqual(JsonValueKind.Null, e.ValueKind);
        Assert.Equal(ev.Regulation, e.GetProperty("regulation").GetString());
        Assert.Equal(ev.SourceRunId, e.GetProperty("sourceRun").GetProperty("runId").GetString());
    }

    // ─── tampering negative test ────────────────────────────────────────────

    [Fact]
    public async Task AgentEvalDoctor_AgainstTamperedFixture_ExitsNonZero()
    {
        // Companion to the matrix-flip negative test below. Validates that
        // the workspace-side `agenteval doctor` command also catches a
        // contentHash tamper — that's the headline guarantee for users who
        // run doctor as a CI check rather than going through MC.
        var tempRoot = Path.Combine(Path.GetTempPath(), "agenteval-e2e-doctor-tamper-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempRoot);
        try
        {
            var manifest = await MissionControlFixtureBuilder.BuildAsync(tempRoot);
            var runId = manifest.RunIds[(MissionControlFixtureBuilder.TravelAgent,
                MissionControlFixtureBuilder.T2, FixtureRunKind.Eval)];
            var manifestFile = Directory.GetFiles(manifest.AgentEvalDir, "manifest.json", SearchOption.AllDirectories)
                .Single(p => Path.GetDirectoryName(p)!.EndsWith(runId, StringComparison.Ordinal));

            var raw = await File.ReadAllTextAsync(manifestFile);
            var tampered = System.Text.RegularExpressions.Regex.Replace(
                raw,
                "\"contentHash\"\\s*:\\s*\"[^\"]*\"",
                "\"contentHash\":\"TAMPERED\"");
            Assert.NotEqual(raw, tampered);
            await File.WriteAllTextAsync(manifestFile, tampered);

            var exit = await DoctorCommand.RunAsync(tempRoot);
            Assert.Equal(2, exit); // doctor returns 2 when errors found.
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task ComplianceMatrix_AfterFilesystemTampering_FlipsRed()
    {
        // Use a separate per-test fixture so the tampering doesn't leak
        // into the shared class-fixture's clean state.
        var tempRoot = Path.Combine(Path.GetTempPath(), "agenteval-e2e-tamper-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempRoot);
        try
        {
            var manifest = await MissionControlFixtureBuilder.BuildAsync(tempRoot);

            // Pick the TravelAgent T2 run's manifest (the LATEST GDPR
            // evidence for TravelAgent points at the T2 run; the matrix
            // uses latest-per-subject, so tampering an older run wouldn't
            // be observable in the matrix's allChainsValid flag).
            var runId = manifest.RunIds[(MissionControlFixtureBuilder.TravelAgent,
                MissionControlFixtureBuilder.T2, FixtureRunKind.Eval)];
            var manifestCandidates = Directory.GetFiles(manifest.AgentEvalDir, "manifest.json", SearchOption.AllDirectories)
                .Where(p => Path.GetDirectoryName(p)!.EndsWith(runId, StringComparison.Ordinal))
                .ToList();
            Assert.Single(manifestCandidates);
            var manifestFile = manifestCandidates[0];

            var raw = await File.ReadAllTextAsync(manifestFile);
            var tampered = System.Text.RegularExpressions.Regex.Replace(
                raw,
                "\"contentHash\"\\s*:\\s*\"[^\"]*\"",
                "\"contentHash\":\"TAMPERED\"");
            Assert.NotEqual(raw, tampered);
            await File.WriteAllTextAsync(manifestFile, tampered);

            // Sanity: confirm the on-disk hash now reads as "TAMPERED" before
            // we query — same diagnostic that AuditChainTamperingTests added
            // to settle a previously-flaky timing question.
            var verifyStore = new FileSystemOutputStore(manifest.AgentEvalDir);
            var rereadManifest = await verifyStore.GetRunManifestAsync(runId);
            Assert.NotNull(rereadManifest);
            Assert.Equal("TAMPERED", rereadManifest!.ContentHash);

            using var factory = new EndToEndFactory(manifest.WorkspaceRoot);
            using var client = factory.CreateClient();
            var resp = await client.PostAsJsonAsync("/graphql", new
            {
                query = "{ complianceMatrix(regulation: \"gdpr\") { allChainsValid cells { subjectName controlId lastEvidenceRunId } } }"
            });
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var m = doc.RootElement.GetProperty("data").GetProperty("complianceMatrix");

            Assert.False(m.GetProperty("allChainsValid").GetBoolean(),
                $"Tampering with manifest.contentHash must flip allChainsValid to false. Tampered runId={runId}. Matrix response: {body}");
            Assert.NotEmpty(m.GetProperty("cells").EnumerateArray().ToArray());
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { /* best-effort */ }
        }
    }
}

/// <summary>
/// xUnit class fixture: builds the workspace once + spins up the MC web app
/// against it, shared across all tests in the class. Disposing the fixture
/// nukes the temp dir.
/// </summary>
public sealed class EndToEndFixture : IDisposable
{
    /// <summary>The outer root passed to <see cref="DoctorCommand.RunAsync(string?)"/> + <see cref="EndToEndFactory"/>.</summary>
    public string OuterWorkspaceRoot { get; }
    public FixtureManifest Manifest { get; }
    private readonly EndToEndFactory _factory;

    public EndToEndFixture()
    {
        OuterWorkspaceRoot = Path.Combine(Path.GetTempPath(),
            "agenteval-e2e-fixture-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(OuterWorkspaceRoot);
        Manifest = MissionControlFixtureBuilder.BuildAsync(OuterWorkspaceRoot).GetAwaiter().GetResult();
        _factory = new EndToEndFactory(OuterWorkspaceRoot);
    }

    public HttpClient CreateClient() => _factory.CreateClient();

    public string GetRunId(SubjectIdentity subject, DateTimeOffset date, FixtureRunKind kind) =>
        Manifest.RunIds[(subject, date, kind)];

    public void Dispose()
    {
        _factory.Dispose();
        // On Windows, Kestrel + FileSystemOutputStore handles can linger past
        // the factory dispose. Retry the temp-dir delete a few times with
        // backoff so we don't leak %TEMP%\agenteval-e2e-fixture-* dirs on
        // dev boxes / CI runners.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(OuterWorkspaceRoot))
                    Directory.Delete(OuterWorkspaceRoot, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
        // Final swallow — best-effort.
        try { Directory.Delete(OuterWorkspaceRoot, recursive: true); }
        catch { /* leak rather than throw from disposer */ }
    }
}

/// <summary>
/// WebApplicationFactory that points the MC's IOutputStoreReader at a
/// caller-provided workspace root. Mirrors <see cref="FilesystemSeededFactory"/>
/// but accepts the root from the test rather than building its own.
/// </summary>
public sealed class EndToEndFactory : WebApplicationFactory<Query>
{
    private readonly string _workspaceRoot;

    public EndToEndFactory(string workspaceRoot)
    {
        _workspaceRoot = workspaceRoot;
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IOutputStoreReader>();
            services.AddSingleton<IOutputStoreReader>(
                new FileSystemOutputStore(Path.Combine(_workspaceRoot, ".agenteval")));
        });
    }
}

#endif
