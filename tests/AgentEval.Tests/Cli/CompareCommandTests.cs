// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
//
// ADR-031 S5 — `agenteval compare`, end to end over files the REAL
// FileSystemOutputStore wrote.
//
// ⚠ These deliberately do NOT hand-write scenario JSON. The S2 review found the
// "asserted against a hand-built COPY of the store's options" defect on exactly
// this boundary: a reader configured differently from the writer stays green while
// every real file fails. So every run here is produced by the shipped store, and
// what `compare` reads is what a `bench` run leaves on disk.

using AgentEval.Cli;
using AgentEval.Cli.Commands;
using AgentEval.Output;
using AgentEval.Tests.Output;
using Xunit;

namespace AgentEval.Tests.Cli;

public class CompareCommandTests
{
    private static ComparabilityFacts Facts(
        string key = "eval.k",
        string version = "1.0.0",
        double? bar = 0.7,
        string? judgeModel = "gpt-5.5",
        string? rubricDigest = null) =>
        new(key, version)
        {
            EffectiveBar = bar,
            Judge = judgeModel is null ? null : new JudgeFingerprint(judgeModel, rubricDigest),
        };

    /// <summary>Writes one run through the shipped store and returns its run directory.</summary>
    private static async Task<string> WriteRunAsync(
        TempWorkspace temp,
        string subjectName,
        IReadOnlyList<ScenarioResult> scenarios)
    {
        var store = new FileSystemOutputStore(temp.Path);
        var subject = new SubjectIdentity(SubjectKind.Agent, subjectName);
        await store.EnsureSubjectAsync(subject);
        var manifest = await store.StartRunAsync(
            subject, new RunContext("Evals", ".", "TestHarness", null, null, "eval"));

        foreach (var scenario in scenarios)
            await store.WriteScenarioResultAsync(manifest.Run.RunId, scenario);

        string dir = Directory
            .GetDirectories(temp.Path, "*", SearchOption.AllDirectories)
            .Single(d => Path.GetFileName(d) == manifest.Run.RunId);

        // Assert the fixture: "no file was written" and "the files carry nothing" are otherwise
        // indistinguishable, and this test suite would then be measuring an empty directory.
        Assert.Equal(scenarios.Count, Directory.GetFiles(Path.Combine(dir, "scenarios"), "*.json").Length);
        return dir;
    }

    private static ScenarioResult Scenario(
        string id, double score, bool passed, string? stimulusHash, ComparabilityFacts? facts) =>
        new(id, id, "in", "out", passed, score,
            new Dictionary<string, double>(), [], TimeSpan.Zero, 0.0)
        {
            StimulusHash = stimulusHash,
            Comparability = facts,
        };

    // ── The success path, over real files ────────────────────────────────────

    [Fact]
    public async Task TwoRunsWithTheSameFacts_ExitZero()
    {
        using var temp = TempWorkspace.Create("CompareOk");
        string a = await WriteRunAsync(temp, "A", [Scenario("s1", 0.5, true, "sha256:aaa", Facts())]);
        string b = await WriteRunAsync(temp, "B", [Scenario("s1", 0.9, true, "sha256:aaa", Facts())]);

        Assert.Equal(0, CompareCommand.Run(a, b));
    }

    [Fact] // The command accepts the scenarios/ folder too, because that is what tab-completion lands on.
    public async Task TheScenariosFolderItself_IsAcceptedAsARunDirectory()
    {
        using var temp = TempWorkspace.Create("CompareScenariosDir");
        string a = await WriteRunAsync(temp, "A", [Scenario("s1", 0.5, true, "sha256:aaa", Facts())]);
        string b = await WriteRunAsync(temp, "B", [Scenario("s1", 0.5, true, "sha256:aaa", Facts())]);

        Assert.Equal(0, CompareCommand.Run(Path.Combine(a, "scenarios"), Path.Combine(b, "scenarios")));
    }

    // ── Exit 13, over real files ─────────────────────────────────────────────

    [Fact]
    public async Task DifferentStimulus_ExitsThirteen()
    {
        using var temp = TempWorkspace.Create("CompareStimulus");
        string a = await WriteRunAsync(temp, "A", [Scenario("s1", 0.5, true, "sha256:aaa", Facts())]);
        string b = await WriteRunAsync(temp, "B", [Scenario("s1", 0.5, true, "sha256:bbb", Facts())]);

        Assert.Equal(ExitCodes.Incomparable, CompareCommand.Run(a, b));
    }

    [Fact]
    public async Task DifferentEvalKey_ExitsThirteen()
    {
        using var temp = TempWorkspace.Create("CompareKey");
        string a = await WriteRunAsync(temp, "A", [Scenario("s1", 0.5, true, "sha256:aaa", Facts(key: "eval.a"))]);
        string b = await WriteRunAsync(temp, "B", [Scenario("s1", 0.5, true, "sha256:aaa", Facts(key: "eval.b"))]);

        Assert.Equal(ExitCodes.Incomparable, CompareCommand.Run(a, b));
    }

    [Fact]
    public async Task ARunWrittenWithoutComparabilityFacts_ExitsThirteen()
    {
        using var temp = TempWorkspace.Create("CompareNoFacts");
        string a = await WriteRunAsync(temp, "A", [Scenario("s1", 0.5, true, null, facts: null)]);
        string b = await WriteRunAsync(temp, "B", [Scenario("s1", 0.5, true, null, facts: null)]);

        Assert.Equal(ExitCodes.Incomparable, CompareCommand.Run(a, b));
    }

    [Fact] // Default admits the blind spot and says so; --strict refuses on it.
    public async Task StrictRefusesWhatTheDefaultAdmits()
    {
        using var temp = TempWorkspace.Create("CompareStrict");
        string a = await WriteRunAsync(temp, "A", [Scenario("s1", 0.5, true, stimulusHash: null, Facts())]);
        string b = await WriteRunAsync(temp, "B", [Scenario("s1", 0.5, true, stimulusHash: null, Facts())]);

        Assert.Equal(0, CompareCommand.Run(a, b, strict: false));
        Assert.Equal(ExitCodes.Incomparable, CompareCommand.Run(a, b, strict: true));
    }

    [Fact] // VACUITY over the filesystem: two runs sharing no scenario id are refused, not "equal".
    public async Task RunsSharingNoScenarioId_ExitThirteen()
    {
        using var temp = TempWorkspace.Create("CompareDisjoint");
        string a = await WriteRunAsync(temp, "A", [Scenario("s1", 0.5, true, "sha256:aaa", Facts())]);
        string b = await WriteRunAsync(temp, "B", [Scenario("s2", 0.5, true, "sha256:aaa", Facts())]);

        Assert.Equal(ExitCodes.Incomparable, CompareCommand.Run(a, b));
    }

    // ── Usage errors are 2, never 13 — a bad path is not a comparability verdict ──

    [Fact]
    public void AMissingDirectory_ExitsTwo()
    {
        using var temp = TempWorkspace.Create("CompareMissing");
        string nowhere = Path.Combine(temp.Path, "no-such-run");

        Assert.Equal(ExitCodes.UsageError, CompareCommand.Run(nowhere, nowhere));
    }

    [Fact]
    public async Task ADirectoryWithNoScenarioFiles_ExitsTwo()
    {
        using var temp = TempWorkspace.Create("CompareEmpty");
        string a = await WriteRunAsync(temp, "A", [Scenario("s1", 0.5, true, "sha256:aaa", Facts())]);
        string empty = Path.Combine(temp.Path, "empty");
        Directory.CreateDirectory(empty);

        Assert.Equal(ExitCodes.UsageError, CompareCommand.Run(a, empty));
        Assert.Equal(ExitCodes.UsageError, CompareCommand.Run(empty, a));
    }

    [Fact]
    public void ABlankPath_ExitsTwo()
        => Assert.Equal(ExitCodes.UsageError, CompareCommand.Run("  ", "  "));

    // ── JSON mode carries the same verdict as the exit code ──────────────────

    [Fact]
    public async Task JsonMode_ReturnsTheSameExitCodeAsTheReport()
    {
        using var temp = TempWorkspace.Create("CompareJson");
        string a = await WriteRunAsync(temp, "A", [Scenario("s1", 0.5, true, "sha256:aaa", Facts())]);
        string b = await WriteRunAsync(temp, "B", [Scenario("s1", 0.5, true, "sha256:bbb", Facts())]);

        Assert.Equal(CompareCommand.Run(a, b), CompareCommand.Run(a, b, asJson: true));
        Assert.Equal(ExitCodes.Incomparable, CompareCommand.Run(a, b, asJson: true));
    }

    // ── The exit code itself ─────────────────────────────────────────────────

    [Fact] // ADR-031 §8.4 reserves 13, and 12 is deliberately NOT claimed (S4 waits on Q5).
    public void IncomparableIsThirteen_AndIsNotAnExistingCode()
    {
        Assert.Equal(13, ExitCodes.Incomparable);
        Assert.NotEqual(ExitCodes.GateIndeterminate, ExitCodes.Incomparable);
        Assert.NotEqual(ExitCodes.RuntimeError, ExitCodes.Incomparable);
        Assert.NotEqual(ExitCodes.UsageError, ExitCodes.Incomparable);
    }
}
