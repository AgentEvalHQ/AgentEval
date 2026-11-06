// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Models;
using AgentEval.Output;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Writing;

namespace AgentEval.Microsoft365Demo;

/// <summary>Where the benchmark's AEF run was written.</summary>
/// <param name="Folder">The run, relative to the run folder: <c>aef/&lt;yyyy&gt;/&lt;MM&gt;/&lt;runId&gt;</c>.</param>
/// <param name="RunHash">The run hash of its seal.</param>
/// <param name="AbortReason">Why the run closed aborted (<see cref="BenchmarkOutcome.StopCode"/>); null when it completed.</param>
internal sealed record AefRunRecord(string Folder, string? RunHash, string? AbortReason);

/// <summary>
/// Writes the benchmark as a sealed AEF run under <c>aef/</c>, AgentEval's record format. It uses
/// <see cref="AefEvalWriter"/>, the writer the agenteval CLI's eval uses. The run records the provider's run settings
/// (<see cref="Microsoft365RunSettings"/>, the keys the CLI records too), the subject <c>agent:&lt;display name&gt;</c>, and
/// one case per case the benchmark ran. Each case keeps its prompt, the agent's answer and its turn's evidence document
/// under the run's content policy. Each case's state is the demo's verdict: PASS is <c>passed</c>, FAIL is
/// <c>failed</c>, ERROR is <c>error</c>, and NOT MEASURED is <c>not_measured</c>, or <c>error</c> when the judge's call
/// failed or gave no verdict. Both are typed absences, never a pass or a fail. Each check of a case is a child assertion
/// beside its verdict, so a FAIL shows the check that failed it. A case the benchmark did not reach has no line, and the
/// run then closes aborted. The run records no gate: the demo's exit code also counts readiness, the
/// prompt and the run folder's files, which this run does not hold.
/// </summary>
internal static class BenchmarkAef
{
    /// <summary>
    /// Writes the run. A write that fails is recorded in the run folder's write errors (the run then exits 3) and
    /// returns null; nothing is thrown.
    /// </summary>
    /// <param name="context">The run's context (folder, settings, turn recorder, clock).</param>
    /// <param name="config">The config the live agent was built from.</param>
    /// <param name="caseSet">The cases the benchmark ran.</param>
    /// <param name="judge">The judge of the criteria.</param>
    /// <param name="benchmark">The benchmark's outcome.</param>
    /// <param name="startedAt">When the benchmark started.</param>
    public static AefRunRecord? Write(DemoContext context, Microsoft365Config config, DemoCaseSet caseSet, JudgeChoice judge, BenchmarkOutcome benchmark, DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(caseSet);
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentNullException.ThrowIfNull(benchmark);
        var folder = context.Folder;
        try
        {
            var options = new AefEvalRunOptions
            {
                Config = new AefRunConfig { Settings = Microsoft365RunSettings.For(config, context.Settings.Profile, context.Settings.ContentPolicy) },
                SubjectRef = AefEvalWriter.Ref("agent", config.DisplayName),
                SubjectKind = AefSubjectKind.Agent,
                // The demo vouches only for its own transport: a host-supplied one (the tests' scripted Work IQ) is a stand-in.
                TargetMode = context.Overrides.InnerHandler is null ? AefTargetMode.Live : AefTargetMode.Mocked,
                // The run folder holds the answers already (evidence/, console.log), so its AEF run keeps them too.
                ContentCapture = AefContentCapture.On,
                JudgeModel = judge.Model,
                JudgeProvider = judge.Provider,
                DatasetName = caseSet.Name,
                DatasetBytes = caseSet.Bytes,
            };

            using var session = AefEvalWriter.Start(folder.AefFolder.FullName, options, startedAt);
            foreach (var outcome in benchmark.Cases)
            {
                if (ToResult(outcome, judge.IsAvailable) is not { } entry)
                {
                    continue;
                }

                var demoCase = outcome.Case;
                session.Add(
                    entry.Result,
                    demoCase.Id,
                    demoCase.Criteria.Count > 0 ? demoCase.PassingScore : (int?)null,   // the threshold applies to the judge's grade only
                    new AefNativeContent
                    {
                        Input = demoCase.Input,
                        Document = outcome.Turn is { } turn ? context.Turns.DocumentOf(turn) : null,
                    },
                    usage: null,
                    entry.Absence,
                    // A judge call that failed produced no grade: the case's checks decided it, and no score is recorded.
                    scoreMeasured: outcome.JudgeFailure is null);
            }

            var (directory, runHash) = session.Complete(context.Now(), abortReason: benchmark.StopCode);
            return new AefRunRecord(folder.Relative(directory), runHash, benchmark.StopCode);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            folder.RecordError($"{RunFolder.AefFolderName}/ (the benchmark's AEF run): {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The AEF reason of a NOT MEASURED case whose answer could not be measured for another reason.</summary>
    internal const string AnswerNotMeasuredReason =
        "the agent's answer was not measured: no turn result was recorded, or the answer was not complete";

    /// <summary>
    /// The AEF reason of a NOT MEASURED case whose criteria no judge graded: none was configured (<c>--no-judge</c>, or
    /// no judge provider), or the configured one could not be built.
    /// </summary>
    internal const string NoJudgeReason = "no judge graded the case's criteria: none was configured, or it could not be built";

    /// <summary>
    /// The result the AEF run records for a case, and its absence when the case was NOT MEASURED: a new result read from
    /// the harness's, whose state is the demo's verdict (the harness's own result is left as it was). Each check the case
    /// ran is recorded as an assertion (passed, failed, or inconclusive when it applied but was not measured), so a FAIL
    /// shows the check that failed it; the judge's grade is recorded by its criteria. Null for a case the benchmark did
    /// not reach: nothing was sent.
    /// </summary>
    /// <param name="outcome">The case's outcome.</param>
    /// <param name="judgeConfigured">Whether a judge was available for the run.</param>
    internal static (TestResult Result, AefCaseAbsence? Absence)? ToResult(CaseOutcome outcome, bool judgeConfigured)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Harness is not { } harness)
        {
            return null;
        }

        var measured = outcome.Verdict is CaseVerdict.Pass or CaseVerdict.Fail;
        var graded = measured && outcome.Criteria is not null;
        var result = new TestResult
        {
            TestName = outcome.Case.Id,
            ActualOutput = OutputOf(outcome, harness),
            Passed = outcome.Verdict == CaseVerdict.Pass,
            // The judge's grade when it graded; a judge call that failed produced none (the writer is told not to record it).
            Score = measured ? harness.Score : 0,
            CriteriaResults = graded ? outcome.Criteria : null,
            JudgeRubricDigest = graded ? harness.JudgeRubricDigest : null,
            Details = graded ? harness.Details : "",
            AssertionResults = AssertionsOf(outcome),
        };

        if (measured)
        {
            return (result, null);
        }

        if (outcome.Verdict == CaseVerdict.Error && harness.Error is { } thrown)
        {
            // ERROR: the exception the evaluation threw (the turn's failure, or the harness refusing the case).
            result.Error = thrown;
            return (result, null);
        }

        // NOT MEASURED. The reasons are fixed texts: the case's own reason may quote the judge's reply ([RUN-11]).
        if (outcome.Turn is { } unselected && !Microsoft365Evidence.IsAnswerMeasured(unselected))
        {
            return (result, new AefCaseAbsence(AefState.NotMeasured, Microsoft365Evidence.NoAnswerSelectedReason));
        }

        if (outcome.Checks.Any(c => c.Result == CheckResult.NotMeasured && c.Name != Benchmark.CriteriaCheck))
        {
            // On the agent's side: no turn result, or an answer that is not complete.
            return (result, new AefCaseAbsence(AefState.NotMeasured, AnswerNotMeasuredReason));
        }

        if (!judgeConfigured)
        {
            return (result, new AefCaseAbsence(AefState.NotMeasured, NoJudgeReason));
        }

        // The judge's call failed, or its reply had no verdict: an evaluation-infrastructure failure, an AEF error.
        result.JudgeFailed = true;
        return (result, null);
    }

    /// <summary>
    /// The agent's output the run records: the selected answer of a completed turn (the harness has none when the judge
    /// call threw after the turn), else the harness's text. A turn with several answer candidates and none selected
    /// records none: its empty text is not the agent's answer.
    /// </summary>
    private static string? OutputOf(CaseOutcome outcome, TestResult harness) => outcome.Turn switch
    {
        { } turn when !Microsoft365Evidence.IsAnswerMeasured(turn) => null,
        { IsCompleted: true } turn => turn.AnswerText ?? harness.ActualOutput,
        _ => harness.ActualOutput,
    };

    /// <summary>
    /// The case's checks as assertions: a passed check is passed, a failed one failed, one that applied but was not
    /// measured inconclusive. A check that did not apply is left out, and so is the judge's check: its criteria record it.
    /// </summary>
    private static IReadOnlyList<AssertionResult>? AssertionsOf(CaseOutcome outcome)
    {
        var assertions = outcome.Checks
            .Where(c => c.Name != Benchmark.CriteriaCheck)
            .Select(c => c.Result switch
            {
                CheckResult.Pass => AssertionResult.Pass(c.Name),
                CheckResult.Fail => AssertionResult.Fail(c.Name),
                CheckResult.NotMeasured => AssertionResult.Undecidable(c.Name, "the check applied but was not measured"),
                _ => null,
            })
            .OfType<AssertionResult>()
            .ToList();
        return assertions.Count > 0 ? assertions : null;
    }
}
