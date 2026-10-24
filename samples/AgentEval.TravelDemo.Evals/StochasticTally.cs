// SPDX-License-Identifier: MIT
// Copyright (c) 2026 ECS2026 Demo

namespace AgentEval.TravelDemo.Evals;

/// <summary>
/// One run of a stochastic eval (Eval 04 / Eval 05).
/// </summary>
/// <param name="Run">1-based run number.</param>
/// <param name="Score">The judge's score, or null when the run has none: not measured, or measured but unscored (no output to grade, or a failed structure the judge gave no verdict on).</param>
/// <param name="Passed">The run's verdict over every dimension checked (structure AND quality); meaningless when not measured.</param>
/// <param name="NotMeasuredReason">Why the run produced no verdict — the model call or the judge failed. Null for a measured run.</param>
/// <param name="FailureReason">Why a measured run failed, for the console. Null when it passed.</param>
/// <param name="StructureFailed">True when a structural check of the workflow harness failed (Eval 05), whatever the score.</param>
public sealed record RunOutcome(int Run, int? Score, bool Passed, string? NotMeasuredReason = null, string? FailureReason = null, bool StructureFailed = false)
{
    /// <summary>True when the run produced a verdict.</summary>
    public bool Measured => NotMeasuredReason is null;

    /// <summary>A run whose model call or judge failed: no score, no verdict.</summary>
    public static RunOutcome NotMeasured(int run, string reason) => new(run, null, false, reason);
}

/// <summary>Where an agent harness run with <see cref="TestResult.Error"/> set failed.</summary>
public enum FailedStage
{
    /// <summary>Before the agent finished (the model call, a tool, the stream): nothing of the run is left to check.</summary>
    AgentRun,

    /// <summary>The agent finished; the judge call then threw. Its tool calls are recorded; the quality gate is not measured.</summary>
    JudgeCall,

    /// <summary>The judge answered, and the harness failed after it. The quality gate is still not a measured verdict.</summary>
    AfterJudging,
}

/// <summary>
/// Turns one run's raw results into a <see cref="RunOutcome"/>, the same way for Eval 04 and Eval 05. Pure: the
/// caller passes how errors are described (<c>Config.Redact</c> in the evals), so the self-test can drive it offline.
/// </summary>
public static class RunClassifier
{
    /// <summary>
    /// Where a run with <see cref="TestResult.Error"/> set failed. <c>MAFEvaluationHarness</c> catches every exception
    /// into Error, the judge call's too, so Error alone does not say the agent failed. It sets
    /// <see cref="TestResult.ToolUsage"/> once the agent has finished (the streaming path always; the plain path when
    /// tools are tracked) and <see cref="TestResult.CriteriaResults"/> once the judge has answered — the two marks
    /// read here. A run with no ToolUsage is reported as an agent failure: nothing shows it got further.
    /// </summary>
    public static FailedStage FailedStageOf(TestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.ToolUsage is null ? FailedStage.AgentRun
             : result.CriteriaResults is null ? FailedStage.JudgeCall
             : FailedStage.AfterJudging;
    }

    /// <summary>What failed, for a console line: "the agent run" / "the judge call" / "the evaluation after the judge answered".</summary>
    public static string Describe(FailedStage stage) => stage switch
    {
        FailedStage.AgentRun  => "the agent run",
        FailedStage.JudgeCall => "the judge call",
        _                     => "the evaluation after the judge answered",
    };

    /// <summary>
    /// An agent run (Eval 04). The harness turns a failed model call into Score 0 with <see cref="TestResult.Error"/>
    /// set, and a judge failure into Score 0 with <see cref="TestResult.JudgeFailed"/>: both are NOT measured here.
    /// </summary>
    public static RunOutcome FromAgentRun(int run, TestResult result, int passingScore, Func<Exception, string> describeError)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Error is { } error) return RunOutcome.NotMeasured(run, $"{Describe(FailedStageOf(result))} failed: {describeError(error)}");
        if (result.JudgeFailed)       return RunOutcome.NotMeasured(run, "the judge produced no verdict");

        return new RunOutcome(run, result.Score, result.Passed,
            FailureReason: result.Passed ? null
                : result.Score < passingScore ? $"score {result.Score} < {passingScore}"
                : "a check besides the score failed");
    }

    /// <summary>
    /// A workflow run (Eval 05) before its output goes to the judge: the verdict when the run cannot be judged, or
    /// null when it can. The workflow adapter does not throw on a failed agent call — it records it in
    /// <see cref="WorkflowExecutionResult.Errors"/> — so an errored executor is read here, not caught.
    /// </summary>
    public static RunOutcome? BeforeJudging(int run, WorkflowTestResult test, Func<Exception, string> describeError, Func<string?, string> redact)
    {
        ArgumentNullException.ThrowIfNull(test);
        if (test.Error is { } error)
            return RunOutcome.NotMeasured(run, $"the workflow run failed: {describeError(error)}");
        if (test.ExecutionResult is not { } execution)
            return RunOutcome.NotMeasured(run, "the harness returned no execution result");
        if (execution.Errors is { Count: > 0 } errors)
            return RunOutcome.NotMeasured(run, $"{errors.Count} agent call(s) failed — [{errors[0].ExecutorId}] {redact(errors[0].Message)}");

        // No error and no output: the workflow ran and produced nothing. A measured FAILURE — counted as failed in the
        // pass rate — with no judge score: it is "unscored" (StochasticTally.Unscored), not a 0 nobody gave.
        if (string.IsNullOrWhiteSpace(execution.FinalOutput))
        {
            var failedChecks = StructuralFailures(test);
            return new RunOutcome(run, null, false, FailureReason: Reasons(failedChecks, "no final output (unscored)"), StructureFailed: failedChecks.Count > 0);
        }
        return null;
    }

    /// <summary>
    /// A judged workflow run (Eval 05). It passes only when the harness's structural checks (executor order,
    /// expected tools, duration, no errors) AND the judge's score pass: a good score never hides a failed structure.
    /// The structure is read FIRST: a failed structure is a measured FAIL even when the judge gave no verdict (then
    /// unscored), so a broken judge cannot turn a broken workflow into "not measured".
    /// </summary>
    public static RunOutcome AfterJudging(int run, WorkflowTestResult test, EvaluationResult judge, int passingScore)
    {
        ArgumentNullException.ThrowIfNull(test);
        ArgumentNullException.ThrowIfNull(judge);
        var structural = StructuralFailures(test);
        if (judge.EvaluationFailed || judge.OverallScore is not { } score)
        {
            return structural.Count == 0
                ? RunOutcome.NotMeasured(run, "the judge produced no verdict")
                : new RunOutcome(run, null, false,
                    FailureReason: Reasons(structural, "quality not measured — the judge produced no verdict (unscored)"),
                    StructureFailed: true);
        }

        bool qualityPassed = score >= passingScore;
        bool passed = structural.Count == 0 && qualityPassed;
        return new RunOutcome(run, score, passed,
            FailureReason: passed ? null
                : Reasons(structural, qualityPassed ? null : $"score {judge.OverallScore} < {passingScore}"),
            StructureFailed: structural.Count > 0);
    }

    /// <summary>
    /// A workflow run whose judge call threw (Eval 05). Same rule as <see cref="AfterJudging"/>: a failed structure is
    /// a measured FAIL (unscored); a sound structure with no judge verdict is not measured.
    /// </summary>
    /// <param name="run">1-based run number.</param>
    /// <param name="test">The workflow run that was being judged.</param>
    /// <param name="describedError">The judge's exception, already redacted for the console.</param>
    public static RunOutcome JudgeThrew(int run, WorkflowTestResult test, string describedError)
    {
        ArgumentNullException.ThrowIfNull(test);
        var structural = StructuralFailures(test);
        return structural.Count == 0
            ? RunOutcome.NotMeasured(run, $"the judge failed: {describedError}")
            : new RunOutcome(run, null, false,
                FailureReason: Reasons(structural, $"quality not measured — the judge failed (unscored): {describedError}"),
                StructureFailed: true);
    }

    /// <summary>The names of the harness checks that failed; "harness checks" when it failed without naming one.</summary>
    public static IReadOnlyList<string> StructuralFailures(WorkflowTestResult test)
    {
        var failed = (test.AssertionResults ?? []).Where(a => !a.Passed).Select(a => a.AssertionName).ToList();
        if (!test.Passed && failed.Count == 0) failed.Add("harness checks");
        return failed;
    }

    private static string Reasons(IReadOnlyList<string> structural, string? quality)
    {
        var parts = new List<string>();
        if (structural.Count > 0) parts.Add($"structure failed: {string.Join(", ", structural)}");
        if (quality is not null)  parts.Add(quality);
        return string.Join("; ", parts);
    }
}

/// <summary>
/// The statistics of a stochastic eval with errored runs kept OUT of them: a failed model call or judge is not a
/// score of 0, so it never widens the spread or lowers the mean. The observed pass rate covers measured runs only;
/// the verdict uses the library's rule over all planned runs, preserving missing verdicts as unknown.
/// </summary>
/// <remarks>
/// A measured run with no judge score (<see cref="Unscored"/>: no output to grade, or a failed structure the judge
/// gave no verdict on) is a FAILED run in the pass rate, and stays out of <see cref="Scores"/>: the spread is then over
/// <see cref="Scores"/>.Count of <see cref="Total"/> runs and gives no consistency verdict. The alternative — scoring it
/// 0 — would put a number no judge gave into the spread; leaving it out silently made a workflow that produced nothing
/// look as consistent as one that never failed.
/// </remarks>
public sealed record StochasticTally(IReadOnlyList<RunOutcome> Runs, double Threshold)
{
    public int Total       => Runs.Count;
    public int Errored     => Runs.Count(r => !r.Measured);
    public int Measured    => Total - Errored;
    public int PassedCount => Runs.Count(r => r.Measured && r.Passed);

    /// <summary>Measured runs with no judge score: failed in the pass rate, absent from the spread.</summary>
    public int Unscored    => Runs.Count(r => r.Measured && r.Score is null);

    /// <summary>Passed over MEASURED runs (unscored ones count as failed); 0 when none was measured.</summary>
    public double PassRate => Measured == 0 ? 0 : (double)PassedCount / Measured;

    /// <summary>True when the spread covers every run: none errored and none unscored.</summary>
    public bool SpreadCoversEveryRun => Errored == 0 && Unscored == 0;

    /// <summary>The judged scores of the measured runs.</summary>
    public IReadOnlyList<int> Scores => [.. Runs.Where(r => r.Measured && r.Score is not null).Select(r => r.Score!.Value)];

    /// <summary>The shared verdict over every planned run; missing runs never count as passes.</summary>
    public StochasticVerdict Verdict => StochasticResult.DecideVerdict(PassedCount, Errored, Total, Threshold);
}
