// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365Demo.Tests.TestHelpers;

namespace AgentEval.Microsoft365Demo.Tests;

/// <summary>
/// The exit code is decided once (<c>ExitPolicy</c>), and results.json, results.md, summary.md and the final table are
/// written from that one decision: a zip that cannot be written shows in every one of them, not only in the process exit.
/// </summary>
public sealed class ExitCodeTests : IDisposable
{
    private readonly DemoRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public async Task ZipCannotBeCreated_TheScreenTheSummaryAndTheResultsAllSayExitThree()
    {
        _rig.WithHealthyEndpoint().WithCompletedTurns();
        _rig.Judge = new FakeJudgeChatClient();
        // A folder where the zip would go: the zip cannot be created there.
        var blocker = Directory.CreateDirectory(_rig.RunFolder.FullName + ".zip");

        var run = await _rig.RunAsync(DemoRig.Consent, "--zip");

        run.AssertExit(3);
        Assert.Contains("Result: ERROR (exit 3)", run.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("Result: PASS", run.Out, StringComparison.Ordinal);
        Assert.Contains("**Result: ERROR** (exit 3)", run.Text("summary.md"), StringComparison.Ordinal);
        Assert.Contains("Result: ERROR (exit 3)", run.Text("console.log"), StringComparison.Ordinal);
        var results = run.Json("results.json");
        Assert.Equal(3, results.GetProperty("exitCode").GetInt32());
        Assert.Equal("ERROR", results.GetProperty("result").GetString());
        Assert.Contains("run.zip", results.GetProperty("exitMeaning").GetString(), StringComparison.Ordinal);

        // The agent was measured: every case passed; only the zip is missing, and the folder in its way is untouched.
        Assert.Equal(DemoRig.DefaultCaseIds.Length, results.GetProperty("counts").GetProperty("pass").GetInt32());
        Assert.Contains("zip the run folder yourself", run.Out, StringComparison.Ordinal);
        blocker.Refresh();
        Assert.True(blocker.Exists);
        Assert.Empty(blocker.EnumerateFileSystemInfos());
    }

    [Fact]
    public void Decide_ARuntimeErrorWinsOverEverything()
    {
        var decision = ExitPolicy.Decide(Inputs(runtimeError: "IOException: disk full", stages: [new StageRow("prompt", StageResult.Fail, "x")]));

        Assert.Equal(DemoExitCodes.Runtime, decision.Code);
        Assert.Equal("the run stopped on a runtime error: IOException: disk full", decision.Meaning);
    }

    [Fact]
    public void Decide_AFileThatCouldNotBeWritten_IsExitThree_TheConsoleLogErrorNamedFirst()
    {
        var decision = ExitPolicy.Decide(Inputs(writeErrors: ["results.json: IOException: denied"], logError: "console.log could not be written: full"));

        Assert.Equal(DemoExitCodes.Runtime, decision.Code);
        Assert.Equal("a file of the run could not be written: console.log could not be written: full", decision.Meaning);
    }

    [Fact]
    public void Decide_AFailedStageOrCase_IsExitOne_NamingThem()
    {
        var benchmark = new BenchmarkOutcome([Outcome("case_a", CaseVerdict.Fail), Outcome("case_b", CaseVerdict.Pass)], null);

        var decision = ExitPolicy.Decide(Inputs(stages: [new StageRow("readiness: auth", StageResult.Pass, "ok"), new StageRow("benchmark", StageResult.Fail, "1 FAIL")], benchmark: benchmark));

        Assert.Equal(DemoExitCodes.Failure, decision.Code);
        Assert.Equal("failed: benchmark, case case_a", decision.Meaning);
    }

    [Fact]
    public void Decide_ErrorAndNotMeasuredCases_AreExitEleven_WithTheCriteriaReasonOnce()
    {
        var benchmark = new BenchmarkOutcome(
            [Outcome("case_a", CaseVerdict.Error), Outcome("case_b", CaseVerdict.NotMeasured), Outcome("case_c", CaseVerdict.Pass)],
            StopReason: null,
            CriteriaNotGraded: "no judge configured");

        var decision = ExitPolicy.Decide(Inputs(stages: [new StageRow("benchmark", StageResult.NotMeasured, "3 cases")], benchmark: benchmark));

        Assert.Equal(DemoExitCodes.NotMeasured, decision.Code);
        Assert.Equal(
            "nothing failed, but not measured: 1 case(s) ERROR, not evaluated (case_a; results.md gives each reason); " +
            "1 case(s) NOT MEASURED (case_b; criteria not graded: no judge configured)",
            decision.Meaning);
    }

    [Fact]
    public void Decide_OnlyTheBenchmarkRowIsNotMeasured_ItsDetailSaysWhy()
    {
        var decision = ExitPolicy.Decide(Inputs(stages: [new StageRow("prompt", StageResult.Pass, "ok"), new StageRow("benchmark", StageResult.NotMeasured, "not run: the run was canceled")]));

        Assert.Equal(DemoExitCodes.NotMeasured, decision.Code);
        Assert.Equal("nothing failed, but not measured: benchmark (not run: the run was canceled)", decision.Meaning);
    }

    [Theory]
    [InlineData(false, "every stage that ran passed and everything requested was measured")]
    [InlineData(true, "readiness passed (--readiness-only)")]
    public void Decide_EverythingPassed_IsExitZero(bool readinessOnly, string meaning)
    {
        var decision = ExitPolicy.Decide(Inputs(stages: [new StageRow("readiness: auth", StageResult.Pass, "ok"), new StageRow("prompt", StageResult.Skipped, "not requested")], readinessOnly: readinessOnly));

        Assert.Equal(new ExitDecision(DemoExitCodes.Success, meaning), decision);
    }

    private static ExitInputs Inputs(
        string? runtimeError = null,
        IReadOnlyList<string>? writeErrors = null,
        string? logError = null,
        IReadOnlyList<StageRow>? stages = null,
        BenchmarkOutcome? benchmark = null,
        bool readinessOnly = false) =>
        new(runtimeError, writeErrors ?? [], logError, stages ?? [], benchmark, readinessOnly);

    private static CaseOutcome Outcome(string id, CaseVerdict verdict) =>
        new(new DemoCase(id, "input", null, [], 70, []), verdict, verdict.ToString(), [], null, null, null);
}
