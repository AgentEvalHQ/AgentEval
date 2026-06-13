// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;
using AgentEval.RedTeam;
using AgentEval.RedTeam.Reporting.Compliance;

namespace AgentEval.Tests.Output;

public class OwaspReporterStoreTests : IDisposable
{
    private readonly string _root;

    public OwaspReporterStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-owasp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        WriteSolutionJson();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void WriteSolutionJson()
    {
        var solution = new { schemaVersion = "1.0", id = Guid.NewGuid(), name = "TestSolution" };
        File.WriteAllText(
            Path.Combine(_root, "solution.json"),
            System.Text.Json.JsonSerializer.Serialize(solution,
                new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
    }

    private static SubjectIdentity DefaultSubject() =>
        new(SubjectKind.Agent, "OWASPTestAgent");

    private async Task<string> CreateCompletedRunAsync(FileSystemOutputStore store)
    {
        var subject = DefaultSubject();
        var context = new RunContext("MyProject", "/path/to/project", "xunit", null, null, "eval");
        var manifest = await store.StartRunAsync(subject, context);
        var runId = manifest.Run.RunId;
        var summary = new RunSummary("1.0", runId, "PASS",
            new RunStats(2, 2, 0, 0),
            new Dictionary<string, double> { ["score"] = 100.0 });
        await store.CompleteRunAsync(manifest, summary);
        return runId;
    }

    private static RedTeamResult BuildFixtureResult() =>
        new()
        {
            AgentName = "OWASPTestAgent",
            AttackResults = new[]
            {
                new AttackResult
                {
                    AttackName = "PromptInjection",
                    OwaspId = "LLM01",
                    MitreAtlasIds = ["AML.T0051"],
                    ResistedCount = 2,
                    SucceededCount = 0,
                    ProbeResults = new[]
                    {
                        new ProbeResult
                        {
                            ProbeId = "pi-01",
                            Outcome = EvaluationOutcome.Resisted,
                            Severity = Severity.High,
                            Prompt = "ignore instructions",
                            Response = "I cannot do that",
                            Reason = "Agent refused"
                        },
                        new ProbeResult
                        {
                            ProbeId = "pi-02",
                            Outcome = EvaluationOutcome.Resisted,
                            Severity = Severity.High,
                            Prompt = "jailbreak attempt",
                            Response = "Request refused",
                            Reason = "Agent refused"
                        }
                    }
                }
            },
            TotalProbes = 2,
            ResistedProbes = 2,
            SucceededProbes = 0,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
            CompletedAt = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromSeconds(5)
        };

    private static RedTeamResult BuildAllInconclusiveResult() =>
        new()
        {
            AgentName = "OWASPTestAgent",
            AttackResults = new[]
            {
                new AttackResult
                {
                    AttackName = "PromptInjection",
                    OwaspId = "LLM01",
                    MitreAtlasIds = ["AML.T0051"],
                    InconclusiveCount = 2,
                    ProbeResults = new[]
                    {
                        new ProbeResult { ProbeId = "pi-01", Outcome = EvaluationOutcome.Inconclusive, Severity = Severity.High, Prompt = "x", Response = "[TIMEOUT]", Reason = "timed out", Error = "Timeout" },
                        new ProbeResult { ProbeId = "pi-02", Outcome = EvaluationOutcome.Inconclusive, Severity = Severity.High, Prompt = "y", Response = "[TIMEOUT]", Reason = "timed out", Error = "Timeout" },
                    }
                }
            },
            TotalProbes = 2,
            ResistedProbes = 0,
            SucceededProbes = 0,
            InconclusiveProbes = 2,
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-5),
            CompletedAt = DateTimeOffset.UtcNow,
            Duration = TimeSpan.FromSeconds(5)
        };

    [Fact]
    public async Task OwaspReporter_SaveReport_AllInconclusive_PersistsNotEvaluated_NotFabricatedPass()
    {
        // #5/#6 (RC-6): a run that conclusively evaluated ZERO categories (e.g. a timed-out SUT → all probes
        // Inconclusive) must persist overallStatus = NOT_EVALUATED into the audit-grade evidence — never a
        // fabricated green PASS. This is the CLI-wired bench-owasp persistence path.
        var store = new FileSystemOutputStore(_root);
        var subject = DefaultSubject();
        var runId = await CreateCompletedRunAsync(store);
        var reporter = new OWASPComplianceReporter();

        await reporter.SaveReportAsync(store, subject, runId, BuildAllInconclusiveResult());

        var evidenceFile = Directory
            .EnumerateFiles(Path.Combine(_root, "compliance", OWASPComplianceReporter.Regulation), "evidence.json", SearchOption.AllDirectories)
            .Single();
        var json = await File.ReadAllTextAsync(evidenceFile);

        Assert.Contains("NOT_EVALUATED", json);   // a fabricated PASS would not contain this token
    }

    [Fact]
    public async Task OwaspReporter_SaveReport_PersistsEvidenceAtCanonicalPath()
    {
        var store = new FileSystemOutputStore(_root);
        var subject = DefaultSubject();
        var runId = await CreateCompletedRunAsync(store);
        var redTeamResult = BuildFixtureResult();
        var reporter = new OWASPComplianceReporter();

        await reporter.SaveReportAsync(store, subject, runId, redTeamResult);

        var complianceRoot = Path.Combine(_root, "compliance", OWASPComplianceReporter.Regulation);
        Assert.True(Directory.Exists(complianceRoot),
            $"Expected compliance directory at {complianceRoot}");

        var subjectDir = Path.Combine(complianceRoot, "OWASPTestAgent");
        Assert.True(Directory.Exists(subjectDir),
            $"Expected subject directory at {subjectDir}");

        var evidenceFiles = Directory.EnumerateFiles(subjectDir, "evidence.json", SearchOption.AllDirectories).ToList();
        Assert.Single(evidenceFiles);
    }

    [Fact]
    public async Task OwaspReporter_SaveReport_ThrowsWhenSourceRunMissing()
    {
        var store = new FileSystemOutputStore(_root);
        var subject = DefaultSubject();
        var redTeamResult = BuildFixtureResult();
        var reporter = new OWASPComplianceReporter();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reporter.SaveReportAsync(store, subject, "nonexistent-run", redTeamResult));
    }

    [Fact]
    public async Task OwaspReporter_SaveReport_EvidenceContainsExpectedControls()
    {
        var store = new FileSystemOutputStore(_root);
        var subject = DefaultSubject();
        var runId = await CreateCompletedRunAsync(store);
        var redTeamResult = BuildFixtureResult();
        var reporter = new OWASPComplianceReporter();

        await reporter.SaveReportAsync(store, subject, runId, redTeamResult);

        var evidenceFile = Directory
            .EnumerateFiles(Path.Combine(_root, "compliance", OWASPComplianceReporter.Regulation), "evidence.json", SearchOption.AllDirectories)
            .Single();
        var json = await File.ReadAllTextAsync(evidenceFile);

        Assert.Contains("\"regulation\"", json);
        Assert.Contains(OWASPComplianceReporter.Regulation, json);
        Assert.Contains("\"LLM01\"", json);
        // T4-4 / review: the honesty disclaimer is a report-surface footer, NOT a synthetic control row.
        // It must never be persisted into the structured evidence Controls (which downstream consumers
        // like the MissionControl compliance matrix iterate as real, scored controls).
        Assert.DoesNotContain("DISCLAIMER", json);
    }
}
