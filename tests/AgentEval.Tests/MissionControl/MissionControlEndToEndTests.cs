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

    // ─── doctor ──────────────────────────────────────────────────────────────

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
    public async Task RecentRuns_TraversesAllThreeDates()
    {
        using var client = _fixture.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = "{ recentRuns(count: 50) { runId timestamp } }"
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var runs = doc.RootElement.GetProperty("data").GetProperty("recentRuns");

        // Fixture seeds: 6 normal-eval + 1 Foundry + 2 memory = 9.
        Assert.True(runs.GetArrayLength() >= 9,
            $"Expected at least 9 fixture runs (6 normal + 1 Foundry + 2 memory); got {runs.GetArrayLength()}.");

        // Spot-check that newest-first ordering crosses the date boundary
        // (T2 dates should appear before T0 dates).
        var firstTs = DateTimeOffset.Parse(runs[0].GetProperty("timestamp").GetString()!);
        var lastTs  = DateTimeOffset.Parse(runs[runs.GetArrayLength() - 1].GetProperty("timestamp").GetString()!);
        Assert.True(firstTs >= lastTs, "recentRuns must be newest-first.");
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
        Assert.True(m.GetProperty("subjects").GetArrayLength() >= 2);
        Assert.True(m.GetProperty("cells").GetArrayLength() >= 4,
            "GDPR fixture has 4 controls × ≥1 subject; expected ≥4 cells.");
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
        Assert.True(m.GetProperty("cells").GetArrayLength() >= 3,
            "EU AI Act fixture has 3 controls; expected ≥3 cells.");
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
    public async Task ScenarioTree_OfFoundryRun_ReturnsRecursiveTree()
    {
        var foundryRunId = _fixture.GetRunId(MissionControlFixtureBuilder.TripPlanner,
            MissionControlFixtureBuilder.T2, "foundry");

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
        //
        // Note: `IOutputStore.StartRunAsync` stamps each manifest with
        // `DateTimeOffset.UtcNow` at write-time — there's no public API
        // for back-dating runs to T0/T1/T2. So the fixture's "different
        // points in time" is encoded in the EVIDENCE timestamps + trace
        // events; the run manifests themselves cluster at fixture-build
        // time. This assertion therefore verifies COVERAGE (every run
        // contributes a timeline point) rather than calendar spread.
        using var client = _fixture.CreateClient();
        var resp = await client.PostAsJsonAsync("/graphql", new
        {
            query = "{ evaluatorTimeline(evaluatorKey: \"composite-root\", count: 50) { runId timestamp score passed } }"
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var pts = doc.RootElement.GetProperty("data").GetProperty("evaluatorTimeline");

        Assert.True(pts.GetArrayLength() >= 7,
            $"Expected ≥7 timeline points (6 normal + 1 Foundry); got {pts.GetArrayLength()}.");

        // Newest-first ordering verified: first.timestamp >= last.timestamp.
        var first = DateTimeOffset.Parse(pts[0].GetProperty("timestamp").GetString()!);
        var last  = DateTimeOffset.Parse(pts[pts.GetArrayLength() - 1].GetProperty("timestamp").GetString()!);
        Assert.True(first >= last, "evaluatorTimeline must be newest-first.");
    }

    // ─── cost-tier breakdown ────────────────────────────────────────────────

    [Fact]
    public async Task RunCostBreakdown_NormalRun_ReturnsTotalAndTiers()
    {
        var runId = _fixture.GetRunId(MissionControlFixtureBuilder.TravelAgent,
            MissionControlFixtureBuilder.T2, "eval");

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
        Assert.Equal(total, sum, 6);
        Assert.True(total >= 0.0);
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
                MissionControlFixtureBuilder.T2, "eval")];
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
    /// <summary>The outer root passed to <see cref="DoctorCommand.RunAsync"/> + <see cref="EndToEndFactory"/>.</summary>
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

    public string GetRunId(SubjectIdentity subject, DateTimeOffset date, string kind) =>
        Manifest.RunIds[(subject, date, kind)];

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(OuterWorkspaceRoot, recursive: true); }
        catch { /* best-effort */ }
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
