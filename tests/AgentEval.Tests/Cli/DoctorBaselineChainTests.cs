// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Cli.Commands;
using AgentEval.Output;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// ADR-031 §5.4 / 7.6 <c>doctor</c> check #3 — the baseline audit chain.
/// </summary>
/// <remarks>
/// <para>
/// §5.4 says the audit chain <c>doctor</c> already verifies for <c>evidence.json</c> extends to
/// baselines <i>"for free"</i>. It did not: nothing read <c>baseline.json</c> at all, so a baseline
/// could name a run that no longer exists, fail its own schema, or be a pin of a run that measured
/// nothing — and <c>doctor</c> printed a clean bill of health for all three.
/// </para>
/// <para>
/// ⚠ <b>Checking at REST is a different check from the write-side guard, not a duplicate of it.</b>
/// <c>BaselinePromotion</c> cannot see a baseline that was hand-edited, or written before the guard
/// existed. One is a rule; the other is the rule's history.
/// </para>
/// </remarks>
[Collection("ConsoleTests")]
public class DoctorBaselineChainTests : IDisposable
{
    private readonly string _root;

    public DoctorBaselineChainTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "agenteval-doctor-baseline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        if (Directory.Exists(_root))
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    // ── THE POSITIVE CONTROL FIRST: a healthy baseline is not flagged ────────────────────────

    [Fact]
    public async Task Doctor_AHealthyBaseline_IsClean()
    {
        // Without this, every assertion below would be satisfied by a check that flags everything.
        var (subject, runId, store) = await ASubjectWithOneRunAsync("BaselineHealthy");
        await store.SaveBaselineAsync(subject, HealthySummary(runId));

        Assert.Equal(0, await DoctorCommand.RunAsync(rootOverride: _root));
    }

    [Fact]
    public async Task Doctor_NoBaselineAtAll_IsClean()
    {
        // Most subjects have none. An absent baseline is not a defect and must not be reported as one.
        await ASubjectWithOneRunAsync("BaselineAbsent");

        Assert.Equal(0, await DoctorCommand.RunAsync(rootOverride: _root));
    }

    // ── the three things it now catches ──────────────────────────────────────────────────────

    [Fact]
    public async Task Doctor_BaselinePointingAtAMissingRun_IsFlagged()
    {
        // The bar every later run is measured against points at nothing. This is the exact
        // audit-chain question doctor already asks of evidence.json and never asked of a baseline.
        var (subject, runId, store) = await ASubjectWithOneRunAsync("BaselineDangling");
        await store.SaveBaselineAsync(subject, HealthySummary(runId));

        string baselineFile = Path.Combine(SubjectDir("BaselineDangling"), "baseline.json");
        await File.WriteAllTextAsync(
            baselineFile,
            (await File.ReadAllTextAsync(baselineFile)).Replace(runId, "2020-01-01_00-00-00_aaaaaaaa", StringComparison.Ordinal));

        Assert.Equal(2, await DoctorCommand.RunAsync(rootOverride: _root));
    }

    [Fact]
    public async Task Doctor_ABaselineOfARunThatMeasuredNothing_IsFlagged()
    {
        // Hand-written, because the write-side guard now refuses to produce one — which is the
        // point: this is the file that predates the guard, or was edited around it.
        var (_, runId, _) = await ASubjectWithOneRunAsync("BaselineEmpty");

        await WriteBaselineDirectlyAsync("BaselineEmpty",
            new RunSummary("1.0", runId, "PASS", new RunStats(0, 0, 0, 0), new Dictionary<string, double>()));

        Assert.Equal(2, await DoctorCommand.RunAsync(rootOverride: _root));
    }

    [Fact]
    public async Task Doctor_ABaselineThatFailsItsOwnSchema_IsFlagged()
    {
        var (_, _, _) = await ASubjectWithOneRunAsync("BaselineMalformed");

        string baselineFile = Path.Combine(SubjectDir("BaselineMalformed"), "baseline.json");
        await File.WriteAllTextAsync(baselineFile, "{\"schemaVersion\":\"1.0\"}");   // no runId, no verdict, no stats

        Assert.Equal(2, await DoctorCommand.RunAsync(rootOverride: _root));
    }

    [Fact]
    public async Task Doctor_ReadsPINNEDBaselinesToo_NotJustTheUnpinnedOne()
    {
        // A check that only ever opened baseline.json would give a clean bill of health to a
        // subject whose pinned baselines were all broken. The unpinned one here is HEALTHY, so the
        // only thing that can turn this red is the pinned file.
        var (subject, runId, store) = await ASubjectWithOneRunAsync("BaselinePinned");
        await store.SaveBaselineAsync(subject, HealthySummary(runId));

        string pinnedDir = Path.Combine(SubjectDir("BaselinePinned"), "baselines");
        Directory.CreateDirectory(pinnedDir);
        await File.WriteAllTextAsync(
            Path.Combine(pinnedDir, "v1.2.3.json"),
            JsonSerializer.Serialize(
                new RunSummary("1.0", runId, "PASS", new RunStats(4, 0, 0, 0, 4), new Dictionary<string, double>()),
                s_json));

        Assert.Equal(2, await DoctorCommand.RunAsync(rootOverride: _root));
    }

    [Fact]
    public void EnumerateBaselineFiles_AssertsItsOwnInput()
    {
        // The enumerator is the thing that decides how much of the store this check can see, so its
        // empty case is asserted directly rather than inferred from a green doctor run.
        using var temp = new TempDir();
        Assert.Empty(DoctorCommand.EnumerateBaselineFiles(temp.Path));

        File.WriteAllText(Path.Combine(temp.Path, "baseline.json"), "{}");
        Directory.CreateDirectory(Path.Combine(temp.Path, "baselines"));
        File.WriteAllText(Path.Combine(temp.Path, "baselines", "v1.json"), "{}");
        File.WriteAllText(Path.Combine(temp.Path, "baselines", "v2.json"), "{}");

        Assert.Equal(3, DoctorCommand.EnumerateBaselineFiles(temp.Path).Count());
    }

    // ── fixtures ─────────────────────────────────────────────────────────────────────────────

    private static RunSummary HealthySummary(string runId) =>
        new("1.0", runId, "PASS", new RunStats(1, 1, 0, 0), new Dictionary<string, double> { ["score"] = 1.0 });

    private string SubjectDir(string name) =>
        Path.Combine(_root, ".agenteval", "subjects", "agents", name);

    private async Task<(SubjectIdentity Subject, string RunId, FileSystemOutputStore Store)> ASubjectWithOneRunAsync(string name)
    {
        var agentEvalDir = Path.Combine(_root, ".agenteval");
        Directory.CreateDirectory(agentEvalDir);
        await File.WriteAllTextAsync(
            Path.Combine(agentEvalDir, "solution.json"),
            JsonSerializer.Serialize(
                new { schemaVersion = "1.0", id = Guid.NewGuid().ToString(), name = "DoctorBaselineSolution" },
                s_json));

        var store = new FileSystemOutputStore(agentEvalDir);
        var subject = new SubjectIdentity(SubjectKind.Agent, name);
        await store.EnsureSubjectAsync(subject);

        var manifest = await store.StartRunAsync(subject, new RunContext("TP", "/p", "xunit", null, null, "eval"));
        await store.WriteScenarioResultAsync(manifest.Run.RunId, new ScenarioResult(
            "sc1", "Scenario 1", "input", "output", true, 1.0,
            new Dictionary<string, double> { ["score"] = 1.0 },
            [], TimeSpan.Zero, 0.0));
        await store.CompleteRunAsync(manifest, HealthySummary(manifest.Run.RunId));

        return (subject, manifest.Run.RunId, store);
    }

    private async Task WriteBaselineDirectlyAsync(string subjectName, RunSummary summary) =>
        await File.WriteAllTextAsync(
            Path.Combine(SubjectDir(subjectName), "baseline.json"),
            JsonSerializer.Serialize(summary, s_json));

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agenteval-baseline-enum-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
