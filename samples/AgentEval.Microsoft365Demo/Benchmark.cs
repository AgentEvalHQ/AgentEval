// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using AgentEval.Core;
using AgentEval.MAF;
using AgentEval.Microsoft365;
using AgentEval.Microsoft365.Diagnostics;
using AgentEval.Microsoft365.Evidence;
using AgentEval.Models;

namespace AgentEval.Microsoft365Demo;

/// <summary>The verdict of one benchmark case.</summary>
internal enum CaseVerdict
{
    /// <summary>Every check that applies ran and passed.</summary>
    Pass,

    /// <summary>A check ran and failed (an empty answer, or the judge's score below the passing score).</summary>
    Fail,

    /// <summary>The turn did not complete, or the case could not run: nothing about the answer was measured. Never a FAIL score.</summary>
    Error,

    /// <summary>Nothing failed, but a check that applies was not measured (no judge for the criteria, the judge gave no verdict, no answer selected, the turn cap).</summary>
    NotMeasured,
}

/// <summary>The result of one check of a case.</summary>
internal enum CheckResult
{
    /// <summary>The check ran and passed.</summary>
    Pass,

    /// <summary>The check ran and failed.</summary>
    Fail,

    /// <summary>The check applies but could not be measured.</summary>
    NotMeasured,

    /// <summary>The check did not apply (the turn did not complete, or the case declares no criteria); it does not change the verdict.</summary>
    NotRun,
}

/// <summary>One check of a case.</summary>
/// <param name="Name">The check (see <see cref="Benchmark"/>'s constants).</param>
/// <param name="Result">Its result.</param>
/// <param name="Detail">What was observed, in plain words.</param>
internal sealed record CheckOutcome(string Name, CheckResult Result, string Detail);

/// <summary>The outcome of one benchmark case.</summary>
/// <param name="Case">The case.</param>
/// <param name="Verdict">The verdict.</param>
/// <param name="Reason">The verdict in one sentence.</param>
/// <param name="Checks">Every check, in order.</param>
/// <param name="Turn">The case's turn result, when the agent produced one.</param>
/// <param name="Score">The judge's score (0-100), only when the criteria were measured.</param>
/// <param name="Criteria">The judge's per-criterion results, only when the criteria were measured.</param>
internal sealed record CaseOutcome(
    DemoCase Case,
    CaseVerdict Verdict,
    string Reason,
    IReadOnlyList<CheckOutcome> Checks,
    WorkIqTurnResult? Turn,
    int? Score,
    IReadOnlyList<CriterionResult>? Criteria)
{
    /// <summary>The judge call that failed after the agent's turn completed, when one did (the criteria are then NOT MEASURED).</summary>
    public JudgeFailure? JudgeFailure { get; init; }
}

/// <summary>A judge call that threw after the agent's turn completed: the judge's problem, never the agent's.</summary>
/// <param name="ErrorType">The exception type (<c>HttpRequestException</c>, <c>ClientResultException</c>, …).</param>
/// <param name="SafeMessage">The exception message, redacted and on one line.</param>
/// <param name="HttpStatus">The HTTP status the judge's provider answered with, when the exception carries one.</param>
internal sealed record JudgeFailure(string ErrorType, string SafeMessage, int? HttpStatus)
{
    /// <summary>The issue code of a failed judge call.</summary>
    public const string IssueCode = "judge.call_failed";

    /// <summary>How the criteria check and the case reason describe it: <c>the judge call failed: &lt;type&gt;: &lt;message&gt;</c>.</summary>
    public string Text => $"the judge call failed: {ErrorType}: {SafeMessage}";

    /// <summary>Whether the judge's provider refused the credentials or the deployment (HTTP 401 or 403).</summary>
    public bool IsAuthRefusal => IsAuthStatus(HttpStatus);

    /// <summary>
    /// Why a judge call with <paramref name="httpStatus"/> failed, in plain words. The demo's own issue text: the issue
    /// register and every report read it from here, never by splitting the issue's suggested cause.
    /// </summary>
    /// <param name="httpStatus">The HTTP status of the judge's provider, when known.</param>
    public static string CauseFor(int? httpStatus) => IsAuthStatus(httpStatus)
        ? $"The judge model's provider refused the judge's key or deployment (HTTP {httpStatus!.Value.ToString(CultureInfo.InvariantCulture)}): " +
          "this is the judge's configuration, not the agent, Work IQ or your Microsoft 365 sign-in."
        : "The judge model's provider failed or could not be reached (network, timeout, overload or a service error): this is the judge, not the agent or Work IQ.";

    /// <summary>What to do about a judge call with <paramref name="httpStatus"/> that failed.</summary>
    /// <param name="httpStatus">The HTTP status of the judge's provider, when known.</param>
    public static string ActionFor(int? httpStatus) => IsAuthStatus(httpStatus)
        ? "Check AI_INFERENCE_PROVIDER and that provider's API key, endpoint and model or deployment, then run again (or run with --no-judge)."
        : "Check the judge provider's endpoint and status and run again (or run with --no-judge).";

    /// <summary>Reads a judge exception: its type, its redacted message and the HTTP status of the first exception in the chain that carries one.</summary>
    /// <param name="error">The exception the judge call threw.</param>
    public static JudgeFailure From(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new JudgeFailure(error.GetType().Name, IssueText.Flatten(WorkIqRedaction.Redact(error.Message)), StatusOf(error));
    }

    /// <summary>
    /// The issue that names the JUDGE: category <see cref="WorkIqIssueCategory.Authentication"/> for an HTTP 401/403 from
    /// the judge's provider, <see cref="WorkIqIssueCategory.Service"/> otherwise. The message carries no case id (the
    /// issue's <c>caseId</c> does), so the same failure on several cases is one distinct issue on the screen.
    /// </summary>
    /// <param name="caseId">The case whose criteria were not graded.</param>
    public WorkIqIssue ToIssue(string caseId) =>
        WorkIqIssue.Create(
            WorkIqTraceStage.Outcome,
            IsAuthRefusal ? WorkIqIssueCategory.Authentication : WorkIqIssueCategory.Service,
            IssueCode,
            $"{Text} — the JUDGE failed, not the agent: the agent's turn completed and its deterministic checks were measured; the criteria were NOT MEASURED",
            // The issue register has one text field for both: issues.jsonl readers get the cause and the action in it.
            CauseFor(HttpStatus) + " What to do: " + ActionFor(HttpStatus))
        with
        {
            CaseId = caseId,
            HttpStatus = HttpStatus,
        };

    private static bool IsAuthStatus(int? httpStatus) => httpStatus is 401 or 403;

    private static int? StatusOf(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case HttpRequestException { StatusCode: { } status }:
                    return (int)status;
                case System.ClientModel.ClientResultException { Status: > 0 } client:
                    return client.Status;
                case Azure.RequestFailedException { Status: > 0 } azure:
                    return azure.Status;
            }
        }

        return null;
    }
}

/// <summary>What the benchmark ran, why it stopped early, and why criteria went ungraded.</summary>
/// <param name="Cases">One outcome per case, in dataset order (cases it did not reach are NOT MEASURED).</param>
/// <param name="StopReason">Why the benchmark stopped before the last case; null when it ran them all.</param>
/// <param name="CriteriaNotGraded">
/// Why declared criteria were not graded (no judge, or a judge call that failed), in one line; null when every case
/// with criteria was graded. The stage row and the exit meaning both print this one text.
/// </param>
internal sealed record BenchmarkOutcome(IReadOnlyList<CaseOutcome> Cases, string? StopReason, string? CriteriaNotGraded = null)
{
    /// <summary>How many cases got <paramref name="verdict"/>.</summary>
    /// <param name="verdict">The verdict.</param>
    public int Count(CaseVerdict verdict) => Cases.Count(c => c.Verdict == verdict);

    /// <summary>The ids of the cases that got <paramref name="verdict"/>, in dataset order.</summary>
    /// <param name="verdict">The verdict.</param>
    public string[] IdsOf(CaseVerdict verdict) => Cases.Where(c => c.Verdict == verdict).Select(c => c.Case.Id).ToArray();
}

/// <summary>
/// The benchmark: every case runs through AgentEval's <see cref="MAFEvaluationHarness"/> over the live
/// <see cref="WorkIqEvaluableAgent"/> (one turn per case, a fresh context each), with the judge when one exists. The
/// verdicts are the demo's own reading of the harness result and the provider's turn result: the deterministic checks
/// (turn completed, answer non-empty, response time recorded, evidence availability recorded) always run; the criteria are
/// graded only by a judge, and are NOT MEASURED without one; a turn that did not complete is an ERROR row with its outcome
/// and its failure, never a FAIL score.
/// </summary>
internal static class Benchmark
{
    /// <summary>The check that the turn completed.</summary>
    public const string TurnCompleted = "turn completed";

    /// <summary>The check that the answer is not empty.</summary>
    public const string AnswerNonEmpty = "answer non-empty";

    /// <summary>The check that the client-side response time was recorded.</summary>
    public const string ResponseTime = "response time recorded";

    /// <summary>The check that the evidence document recorded every channel's availability.</summary>
    public const string EvidenceRecorded = "evidence availability recorded";

    /// <summary>The judge's grade of the case's criteria.</summary>
    public const string CriteriaCheck = "criteria (judge)";

    /// <summary>Runs every case and returns the outcomes; stops early on the turn cap or cancellation.</summary>
    /// <param name="agent">The live agent (its <see cref="WorkIqEvaluableAgent.NextCaseId"/> is set per case).</param>
    /// <param name="cases">The cases.</param>
    /// <param name="judge">The judge.</param>
    /// <param name="turns">The agent's turn recorder.</param>
    /// <param name="console">The demo console.</param>
    /// <param name="cancellationToken">Stops the benchmark; the cases not run are NOT MEASURED.</param>
    public static async Task<BenchmarkOutcome> RunAsync(
        WorkIqEvaluableAgent agent,
        IReadOnlyList<DemoCase> cases,
        JudgeChoice judge,
        TurnRecorder turns,
        DemoConsole console,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(judge);
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(console);

        var evaluator = judge.Client is null ? null : new ChatClientEvaluator(judge.Client);
        var harness = new MAFEvaluationHarness(evaluator, NullAgentEvalLogger.Instance);
        var options = new EvaluationOptions
        {
            // The provider reports no tool calls, tokens or cost (RawMessages and TokenUsage stay null): the harness must
            // not estimate them, so performance and tool tracking are off and the turn's own timing is used instead.
            TrackTools = false,
            TrackPerformance = false,
            EvaluateResponse = evaluator is not null,
            Verbose = false,
        };

        var outcomes = new List<CaseOutcome>(cases.Count);
        string? stopReason = null;
        for (var index = 0; index < cases.Count; index++)
        {
            var demoCase = cases[index];
            if (stopReason is not null)
            {
                outcomes.Add(NotReached(demoCase, stopReason));
                continue;
            }

            console.Line(DemoVerbosity.Normal, $"  case {(index + 1).ToString(CultureInfo.InvariantCulture)}/{cases.Count.ToString(CultureInfo.InvariantCulture)}  {demoCase.Id}" +
                (demoCase.Category is null ? "" : $"  ({demoCase.Category})"));
            var since = turns.Count;
            agent.NextCaseId = demoCase.Id;
            TestResult? result = null;
            try
            {
                result = await harness.RunEvaluationAsync(agent, demoCase.ToTestCase(), options, cancellationToken).ConfigureAwait(false);
            }
            catch (WorkIqTurnBudgetExceededException ex)
            {
                stopReason = $"the --max-turns cap ({ex.MaxTurns.ToString(CultureInfo.InvariantCulture)}) was reached after {ex.TurnsUsed.ToString(CultureInfo.InvariantCulture)} turn(s); nothing more was sent";
            }
            catch (FatalEvaluationException ex)
            {
                stopReason = "the run stopped: " + WorkIqRedaction.Redact(ex.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                stopReason = "the run was canceled";
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // The harness rejected the case before sending it (a malformed case); nothing reached the agent.
                outcomes.Add(new CaseOutcome(
                    demoCase,
                    CaseVerdict.Error,
                    $"the case could not run: {ex.GetType().Name}: {WorkIqRedaction.Redact(ex.Message)}",
                    [new CheckOutcome(TurnCompleted, CheckResult.NotRun, "nothing was sent")],
                    null,
                    null,
                    null));
                continue;
            }

            agent.NextCaseId = null;
            if (result is null)
            {
                outcomes.Add(NotReached(demoCase, stopReason ?? "the case did not run"));
                continue;
            }

            var outcome = Classify(demoCase, result, turns.LatestFor(demoCase.Id, since), judge);
            outcomes.Add(outcome);
            if (outcome.JudgeFailure is { } judgeFailure)
            {
                // One issue per failed judge call, through the agent's diagnostics: issues.jsonl and trace.jsonl keep every
                // occurrence (with the case id); the screen shows a repeated one once, with its count.
                agent.Diagnostics.Issue(judgeFailure.ToIssue(demoCase.Id), WorkIqTraceLevel.Warning);
            }

            console.Line(DemoVerbosity.Normal, "        " + VerdictLabel(outcome.Verdict) + " — " + outcome.Reason, TintOf(outcome.Verdict));
            foreach (var check in outcome.Checks)
            {
                console.Line(DemoVerbosity.Detailed, $"          {CheckLabel(check.Result),-13} {check.Name}: {check.Detail}", Tint.Dim);
            }

            // A cancellation inside a turn ends that turn (an ERROR row above); the cases after it are not sent.
            if (cancellationToken.IsCancellationRequested)
            {
                stopReason = "the run was canceled";
            }
        }

        if (stopReason is not null)
        {
            console.Line(DemoVerbosity.Quiet, "  benchmark stopped early: " + stopReason, Tint.Warn);
        }

        return new BenchmarkOutcome(outcomes, stopReason, CriteriaNotGraded(outcomes, judge));
    }

    /// <summary>
    /// Why declared criteria were not graded, in one line: no judge, and/or the judge calls that failed after a completed
    /// turn (their count and the first failure); null when nothing applies.
    /// </summary>
    /// <param name="outcomes">The case outcomes.</param>
    /// <param name="judge">The judge.</param>
    internal static string? CriteriaNotGraded(IReadOnlyList<CaseOutcome> outcomes, JudgeChoice judge)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        ArgumentNullException.ThrowIfNull(judge);
        var reasons = new List<string>(2);
        if (!judge.IsAvailable && outcomes.Any(o => o.Case.Criteria.Count > 0))
        {
            reasons.Add(judge.NotMeasuredReason);
        }

        var failed = outcomes.Where(o => o.JudgeFailure is not null).ToArray();
        if (failed.Length > 0)
        {
            var first = failed[0].JudgeFailure!;
            reasons.Add($"the judge call failed for {failed.Length.ToString(CultureInfo.InvariantCulture)} case(s) ({first.ErrorType}: " +
                        $"{IssueText.Truncate(first.SafeMessage, 160)}); the agent was measured, the judge was not");
        }

        return reasons.Count == 0 ? null : string.Join("; ", reasons);
    }

    /// <summary>
    /// The verdict of a case from the harness result and the case's turn: ERROR when the turn did not complete (or the
    /// agent failed before a turn); otherwise the deterministic checks, then the criteria — graded by the judge only, NOT
    /// MEASURED without one, without a verdict from it, or when no answer was selected.
    /// </summary>
    /// <param name="demoCase">The case.</param>
    /// <param name="result">The harness result.</param>
    /// <param name="turn">The case's turn result (null when the agent produced none).</param>
    /// <param name="judge">The judge.</param>
    internal static CaseOutcome Classify(DemoCase demoCase, TestResult result, WorkIqTurnResult? turn, JudgeChoice judge)
    {
        ArgumentNullException.ThrowIfNull(demoCase);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(judge);

        JudgeFailure? judgeFailure = null;
        if (result.Error is { } error)
        {
            var fromAgent = (error as IWorkIqTurnResultCarrier)?.TurnResult ?? (error.InnerException as IWorkIqTurnResultCarrier)?.TurnResult;
            var protocolFailure = (error as WorkIqProtocolException)?.Failure;
            var judgeCalled = judge.IsAvailable && demoCase.Criteria.Count > 0;
            if (fromAgent is null && protocolFailure is null && turn is { IsCompleted: true } && judgeCalled)
            {
                // The agent's turn completed and the agent returned its answer; the exception came after it, from the
                // judge (MAFEvaluationHarness lets a genuine judge error propagate into TestResult.Error). The agent was
                // measured: its deterministic checks stand, and the criteria are NOT MEASURED because the judge failed.
                judgeFailure = JudgeFailure.From(error);
            }
            else
            {
                return ErrorOutcome(demoCase, error, fromAgent ?? turn, protocolFailure, judgeCalled);
            }
        }

        var list = new List<CheckOutcome>();
        if (turn is null)
        {
            // The harness returned a response but the turn sink saw no turn: nothing provider-side can be checked.
            list.Add(new(TurnCompleted, CheckResult.NotMeasured, "the response arrived but no turn result was recorded"));
            list.Add(new(AnswerNonEmpty, string.IsNullOrWhiteSpace(result.ActualOutput) ? CheckResult.Fail : CheckResult.Pass,
                string.IsNullOrWhiteSpace(result.ActualOutput) ? "the response text is empty" : $"{(result.ActualOutput ?? "").Length.ToString(CultureInfo.InvariantCulture)} characters"));
            list.Add(new(ResponseTime, CheckResult.NotMeasured, "no turn result"));
            list.Add(new(EvidenceRecorded, CheckResult.NotMeasured, "no turn result"));
        }
        else
        {
            list.Add(new(TurnCompleted, CheckResult.Pass, $"Completed (task {turn.Task?.TaskId ?? turn.Identity.ProviderTaskId ?? "(none: direct message)"})"));
            list.Add(AnswerCheck(turn));
            list.Add(new(ResponseTime, CheckResult.Pass, $"{Milliseconds(turn.Timing.Elapsed)} (client side: network and service, not model latency)"));
            list.Add(new(EvidenceRecorded, CheckResult.Pass, TurnRecorder.EvidenceAvailability(turn.Evidence)));
        }

        int? score = null;
        IReadOnlyList<CriterionResult>? criteria = null;
        if (demoCase.Criteria.Count == 0)
        {
            list.Add(new(CriteriaCheck, CheckResult.NotRun, "the case declares no criteria"));
        }
        else if (!judge.IsAvailable)
        {
            list.Add(new(CriteriaCheck, CheckResult.NotMeasured, judge.NotMeasuredReason));
        }
        else if (judgeFailure is not null)
        {
            list.Add(new(CriteriaCheck, CheckResult.NotMeasured, judgeFailure.Text));
        }
        else if (turn is not null && !Microsoft365Evidence.IsAnswerMeasured(turn))
        {
            // The provider's one rule: a completed turn with several candidates and none selected is not a measured answer.
            list.Add(new(CriteriaCheck, CheckResult.NotMeasured, "no answer was selected (several candidates), so the judge graded an empty string: not a measurement of the agent"));
        }
        else if (result.JudgeFailed)
        {
            list.Add(new(CriteriaCheck, CheckResult.NotMeasured, "the judge produced no verdict: " + IssueText.Truncate(IssueText.Flatten(result.Details), 300)));
        }
        else
        {
            score = result.Score;
            criteria = result.CriteriaResults;
            var passed = result.Score >= demoCase.PassingScore;
            list.Add(new(CriteriaCheck, passed ? CheckResult.Pass : CheckResult.Fail,
                $"score {result.Score.ToString(CultureInfo.InvariantCulture)}/100, passing {demoCase.PassingScore.ToString(CultureInfo.InvariantCulture)}" +
                (criteria is { Count: > 0 } ? $"; {criteria.Count(c => c.Met).ToString(CultureInfo.InvariantCulture)} of {criteria.Count.ToString(CultureInfo.InvariantCulture)} criteria met" : "")));
        }

        var verdict = list.Any(c => c.Result == CheckResult.Fail)
            ? CaseVerdict.Fail
            : list.Any(c => c.Result == CheckResult.NotMeasured) ? CaseVerdict.NotMeasured : CaseVerdict.Pass;
        var reason = verdict switch
        {
            CaseVerdict.Fail => "failed: " + string.Join("; ", list.Where(c => c.Result == CheckResult.Fail).Select(c => $"{c.Name} ({c.Detail})")),
            CaseVerdict.NotMeasured => "not measured: " + string.Join("; ", list.Where(c => c.Result == CheckResult.NotMeasured).Select(c => $"{c.Name} ({c.Detail})")),
            _ => "every check passed" + (score is { } s ? $" (judge score {s.ToString(CultureInfo.InvariantCulture)}/100)" : ""),
        };
        return new CaseOutcome(demoCase, verdict, WorkIqRedaction.Redact(reason), list, turn, score, criteria) { JudgeFailure = judgeFailure };
    }

    /// <summary>
    /// The ERROR row of a case whose evaluation failed: the turn did not complete (its outcome and failure), the agent
    /// failed before a turn result, or — rarely — the turn completed and the evaluation failed after it without a judge
    /// failure to blame. Nothing about the answer is graded and the verdict is never a FAIL score.
    /// </summary>
    private static CaseOutcome ErrorOutcome(DemoCase demoCase, Exception error, WorkIqTurnResult? carried, WorkIqFailure? protocolFailure, bool judgeCalled)
    {
        if (carried is { IsCompleted: true })
        {
            // Not the agent: its turn completed. Say what failed instead of "the turn did not complete: outcome Completed".
            var after = $"the turn completed, but the evaluation failed after it: {error.GetType().Name}: {IssueText.Flatten(error.Message)}";
            var completedChecks = new List<CheckOutcome>
            {
                new(TurnCompleted, CheckResult.Pass, $"Completed (task {carried.Task?.TaskId ?? carried.Identity.ProviderTaskId ?? "(none: direct message)"})"),
                AnswerCheck(carried),
                new(ResponseTime, CheckResult.Pass, $"{Milliseconds(carried.Timing.Elapsed)} (client side: network and service, not model latency)"),
                new(EvidenceRecorded, CheckResult.Pass, TurnRecorder.EvidenceAvailability(carried.Evidence)),
                new(CriteriaCheck, CheckResult.NotRun, judgeCalled ? "not graded: " + after : "not graded: the evaluation failed after the turn"),
            };
            return new CaseOutcome(demoCase, CaseVerdict.Error, WorkIqRedaction.Redact(after), completedChecks, carried, null, null);
        }

        var failure = carried?.Failure ?? protocolFailure;
        var what = carried is not null
            ? "the turn did not complete: " + TurnRecorder.NotCompletedSummary(carried)
            : failure is not null
                ? $"the agent failed before a turn result: {TurnRecorder.FailureCodes(failure)}: {IssueText.Flatten(failure.SafeMessage)}"
                : $"the agent failed: {error.GetType().Name}: {IssueText.Flatten(error.Message)}";
        var checks = new List<CheckOutcome>
        {
            new(TurnCompleted, CheckResult.NotRun, what),
            new(AnswerNonEmpty, CheckResult.NotRun, "no answer: the turn did not complete (partial text is never graded)"),
            carried is null
                ? new(ResponseTime, CheckResult.NotMeasured, "no turn result")
                : new(ResponseTime, CheckResult.Pass, $"{Milliseconds(carried.Timing.Elapsed)} until the turn ended (client side)"),
            carried is null
                ? new(EvidenceRecorded, CheckResult.NotMeasured, "no turn result")
                : new(EvidenceRecorded, CheckResult.Pass, TurnRecorder.EvidenceAvailability(carried.Evidence)),
            new(CriteriaCheck, CheckResult.NotRun, "not graded: there is no answer"),
        };
        return new CaseOutcome(demoCase, CaseVerdict.Error, WorkIqRedaction.Redact(what), checks, carried, null, null);
    }

    /// <summary>A case the benchmark did not reach (the turn cap, a cancellation): NOT MEASURED, nothing sent.</summary>
    /// <param name="demoCase">The case.</param>
    /// <param name="why">Why it did not run.</param>
    internal static CaseOutcome NotReached(DemoCase demoCase, string why) => new(
        demoCase,
        CaseVerdict.NotMeasured,
        "not run: " + why,
        [new CheckOutcome(TurnCompleted, CheckResult.NotMeasured, "not run: " + why)],
        null,
        null,
        null);

    /// <summary>The verdict as printed: <c>PASS</c>, <c>FAIL</c>, <c>ERROR</c>, <c>NOT MEASURED</c>.</summary>
    /// <param name="verdict">The verdict.</param>
    public static string VerdictLabel(CaseVerdict verdict) => verdict switch
    {
        CaseVerdict.Pass => "PASS",
        CaseVerdict.Fail => "FAIL",
        CaseVerdict.Error => "ERROR",
        _ => "NOT MEASURED",
    };

    /// <summary>A check result as printed: <c>PASS</c>, <c>FAIL</c>, <c>NOT MEASURED</c>, <c>NOT RUN</c>.</summary>
    /// <param name="result">The result.</param>
    public static string CheckLabel(CheckResult result) => result switch
    {
        CheckResult.Pass => "PASS",
        CheckResult.Fail => "FAIL",
        CheckResult.NotMeasured => "NOT MEASURED",
        _ => "NOT RUN",
    };

    /// <summary>The colour of a verdict.</summary>
    /// <param name="verdict">The verdict.</param>
    public static Tint TintOf(CaseVerdict verdict) => verdict switch
    {
        CaseVerdict.Pass => Tint.Good,
        CaseVerdict.Fail or CaseVerdict.Error => Tint.Bad,
        _ => Tint.Warn,
    };

    /// <summary>Milliseconds without decimals (<c>1234 ms</c>).</summary>
    /// <param name="elapsed">The duration.</param>
    public static string Milliseconds(TimeSpan elapsed) => elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms";

    private static CheckOutcome AnswerCheck(WorkIqTurnResult turn)
    {
        if (!Microsoft365Evidence.IsAnswerMeasured(turn))
        {
            return new(AnswerNonEmpty, CheckResult.NotMeasured, "several answer candidates and none selected; the evidence document keeps them all");
        }

        return turn.AnswerCompleteness switch
        {
            OutputCompleteness.Complete when !string.IsNullOrWhiteSpace(turn.AnswerText) =>
                new(AnswerNonEmpty, CheckResult.Pass, $"{turn.AnswerText!.Length.ToString(CultureInfo.InvariantCulture)} characters"),
            OutputCompleteness.Complete or OutputCompleteness.Empty =>
                new(AnswerNonEmpty, CheckResult.Fail, "the turn completed with an empty answer"),
            OutputCompleteness.None =>
                new(AnswerNonEmpty, CheckResult.Fail, "the turn completed with no answer artifact"),
            _ =>
                new(AnswerNonEmpty, CheckResult.NotMeasured, $"answer {turn.AnswerCompleteness}: not a complete answer"),
        };
    }
}
