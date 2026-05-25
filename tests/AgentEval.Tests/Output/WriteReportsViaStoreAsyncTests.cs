// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Evals;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Output;

/// <summary>
/// Plan-13 T4.1b item 10 — direct integration test for the canonical write
/// sequence that <c>BenchmarkSampleHelpers.WriteReportsViaStoreAsync</c>
/// performs in <c>samples/AgentEval.Samples/Benchmarks/_BenchmarkSampleHelpers.cs</c>.
/// Until now this sequence was covered only transitively through end-to-end
/// sample runs (which require Azure creds); the test below pins the contract
/// against a synthetic <see cref="EvalResult"/> + <see cref="FileSystemOutputStore"/>
/// fixture so a regression in the canonical-write flow surfaces in CI.
/// </summary>
/// <remarks>
/// The fixture does NOT exercise the sample-side HTML/PDF/sidecar emission
/// (those are covered by <c>HtmlEvalResultRendererTests</c> +
/// <c>PdfEvalResultRendererTests</c>). It pins the canonical sequence:
/// <list type="number">
///   <item>EnsureSolution + EnsureSubject seed the workspace.</item>
///   <item>StartRun → WriteScenarioResult ×N → CompleteRun produces a manifest
///         whose <c>ContentHash</c> binds the run shape.</item>
///   <item>GetRunManifest after completion returns the same hash (round-trip).</item>
///   <item><see cref="IOutputStoreReader.ResolveRunDirectory"/> agrees with the
///         on-disk path of the manifest.</item>
/// </list>
/// </remarks>
public class WriteReportsViaStoreAsyncTests : IDisposable
{
    private readonly string _workspaceRoot;
    private readonly FileSystemOutputStore _store;

    public WriteReportsViaStoreAsyncTests()
    {
        _workspaceRoot = Path.Combine(Path.GetTempPath(), "agenteval-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspaceRoot);
        SeedSolutionFile(_workspaceRoot, "WriteReportsTest");
        _store = new FileSystemOutputStore(_workspaceRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceRoot, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task CanonicalWriteSequence_ProducesManifestAuditHash_RoundTripsThroughStore()
    {
        var subject = new SubjectIdentity(SubjectKind.Agent, "TestAgent");
        await _store.EnsureSolutionAsync();
        await _store.EnsureSubjectAsync(subject);

        var manifest = await _store.StartRunAsync(subject, new RunContext(
            EvalProject: "AgentEval.Tests",
            EvalProjectPath: "tests/AgentEval.Tests/",
            Harness: "WriteReportsViaStoreAsyncTests",
            Seed: 42,
            ParentInvocationId: null,
            Kind: "benchmark"));
        var runId = manifest.Run.RunId;

        // Two scenario leaves — one pass, one fail — to mirror the typical
        // composite shape WriteReportsViaStoreAsync walks via EnumerateAtomicLeaves.
        await _store.WriteScenarioResultAsync(runId, MakeScenario("s-001", passed: true, score: 0.92));
        await _store.WriteScenarioResultAsync(runId, MakeScenario("s-002", passed: false, score: 0.42));

        var summary = new RunSummary(
            SchemaVersion: "1.0",
            RunId: runId,
            Verdict: "FAIL",
            Stats: new RunStats(2, 1, 1, 0),
            Metrics: new Dictionary<string, double> { ["overallScore"] = 0.67 });
        await _store.CompleteRunAsync(manifest, summary);

        // Reload the manifest — completion writes the ContentHash that the
        // sample-side helper later threads into the HTML/PDF renderer's
        // auditHash option. A null hash would silently break audit-chain.
        var loaded = await _store.GetRunManifestAsync(runId);
        Assert.NotNull(loaded);
        Assert.False(string.IsNullOrWhiteSpace(loaded!.ContentHash));
        Assert.StartsWith("sha256:", loaded.ContentHash);

        // ResolveRunDirectory (plan-13 T4.1b item 11) must agree with the
        // actual on-disk location of the manifest.
        var resolvedDir = _store.ResolveRunDirectory(subject, runId);
        Assert.True(Directory.Exists(resolvedDir),
            $"Resolved run dir does not exist on disk: {resolvedDir}");
        Assert.True(File.Exists(Path.Combine(resolvedDir, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(resolvedDir, "summary.json")));
    }

    [Fact]
    public async Task CanonicalWriteSequence_PersistsAllScenarios_ReadableFromStore()
    {
        var subject = new SubjectIdentity(SubjectKind.Agent, "TestAgent2");
        await _store.EnsureSolutionAsync();
        await _store.EnsureSubjectAsync(subject);

        var manifest = await _store.StartRunAsync(subject, new RunContext(
            EvalProject: "AgentEval.Tests",
            EvalProjectPath: "tests/AgentEval.Tests/",
            Harness: "WriteReportsViaStoreAsyncTests",
            Seed: null,
            ParentInvocationId: null,
            Kind: "benchmark"));
        var runId = manifest.Run.RunId;

        const int leafCount = 5;
        for (var i = 0; i < leafCount; i++)
        {
            await _store.WriteScenarioResultAsync(runId, MakeScenario($"leaf-{i:D3}", passed: i % 2 == 0, score: 0.5 + (i * 0.1)));
        }

        var summary = new RunSummary("1.0", runId, "PASS",
            new RunStats(leafCount, 3, 2, 0),
            new Dictionary<string, double> { ["overallScore"] = 0.7 });
        await _store.CompleteRunAsync(manifest, summary);

        var loaded = new List<ScenarioResult>();
        await foreach (var sr in _store.GetScenarioResultsAsync(runId))
        {
            loaded.Add(sr);
        }
        Assert.Equal(leafCount, loaded.Count);
        Assert.All(loaded, sr => Assert.False(string.IsNullOrWhiteSpace(sr.Id)));
    }

    [Fact]
    public async Task ResolveRunDirectory_OnSubjectWithUnsafeName_ReturnsSanitisedPath()
    {
        // Subject names containing Windows-invalid characters must round-trip
        // through Sanitize() — re-implementing this rule in callers was the
        // original v0.10.1 review finding ResolveRunDirectory closes.
        var subject = new SubjectIdentity(SubjectKind.Agent, "Subject:With:Colons");

        var path = _store.ResolveRunDirectory(subject, "2026-05-25_12-00-00_abcdef01");

        Assert.DoesNotContain(":", Path.GetFileName(Path.GetDirectoryName(path) ?? ""));
        Assert.EndsWith("2026-05-25_12-00-00_abcdef01", path);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ScenarioResult MakeScenario(string id, bool passed, double score) =>
        new(
            Id: id,
            Name: $"Scenario {id}",
            Input: "test input",
            Output: "test output",
            Passed: passed,
            Score: score,
            Metrics: new Dictionary<string, double> { ["accuracy"] = score },
            Assertions: Array.Empty<AssertionResult>(),
            Duration: TimeSpan.FromMilliseconds(100),
            EstimatedCost: 0.001);

    private static void SeedSolutionFile(string root, string name)
    {
        var solutionDoc = new
        {
            schemaVersion = "1.0",
            id = Guid.NewGuid(),
            name,
        };
        var json = JsonSerializer.Serialize(solutionDoc, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        File.WriteAllText(Path.Combine(root, "solution.json"), json);
    }
}
