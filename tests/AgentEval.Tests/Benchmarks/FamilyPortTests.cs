// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Benchmarks;
using AgentEval.Evals;
using AgentEval.Evals.Meta;
using AgentEval.Evals.Performance;
using AgentEval.Models;
using AgentEval.RedTeam;
using Xunit;

namespace AgentEval.Tests.Benchmarks;

/// <summary>
/// 7.5 — the perf family as admitted checks, and the red-team family per probe.
/// </summary>
public class PerformanceChecksTests
{
    private static EvalInput With(PerformanceMetrics? performance) =>
        new("q", "a") { Performance = performance };

    private static PerformanceMetrics Metrics(
        double durationMs, int? promptTokens = null, int? completionTokens = null, double? ttftMs = null)
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return new PerformanceMetrics
        {
            StartTime = start,
            EndTime = start.AddMilliseconds(durationMs),
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TimeToFirstToken = ttftMs is { } t ? TimeSpan.FromMilliseconds(t) : null,
        };
    }

    private static async Task<EvalResult> RunAsync(AdmittedCheck check, EvalInput input) =>
        await check.Admit().EvaluateAsync(input);

    // ── The join now carries the measurement ──────────────────────────────────

    [Fact]
    public void TheProjectionCarriesPerformance_BecauseALatencyIsAFactOfTheRunNotAVerdictAboutIt()
    {
        var testCase = new TestCase { Id = "c1", Name = "c", Input = "q" };
        var metrics = Metrics(1234);
        var result = new TestResult { TestName = "c", ActualOutput = "a", Performance = metrics };

        var input = testCase.ToEvalInput(result);

        Assert.Same(metrics, input.Performance);
    }

    [Fact]
    public void AnUnmeasuredRunCarriesNull_NotAZeroDuration()
    {
        var testCase = new TestCase { Id = "c1", Name = "c", Input = "q" };
        var input = testCase.ToEvalInput(new TestResult { TestName = "c", ActualOutput = "a" });

        Assert.Null(input.Performance);
    }

    // ── Latency, both directions plus the decline ─────────────────────────────

    [Fact]
    public async Task AFastRun_Passes_AndASlowOneFails()
    {
        var check = PerformanceChecks.WithinLatencyBudget(TimeSpan.FromSeconds(1));

        var fast = await RunAsync(check, With(Metrics(500)));
        var slow = await RunAsync(check, With(Metrics(1500)));

        Assert.True(fast.Score.Passed);
        Assert.False(slow.Score.Passed);
        Assert.Equal(MeasurementState.Measured, slow.Score.CensusBucket());
    }

    [Fact]
    public async Task AnUnmeasuredRun_DECLINES_RatherThanScoringAPerfectZeroDuration()
    {
        // The whole reason this family could not be a check before: `?? 0` on a missing measurement
        // turns an unmeasured run into the fastest one in the suite.
        var result = await RunAsync(PerformanceChecks.WithinLatencyBudget(TimeSpan.FromSeconds(1)), With(null));

        Assert.False(result.Score.Passed);
        Assert.Equal(MeasurementState.NotApplicable, result.Score.CensusBucket());
        Assert.Contains("not a fast one", result.Details.Summary!, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonPositiveBudget_IsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new WithinLatencyBudgetEval(TimeSpan.Zero));

    // ── Tokens: TWO absences, kept apart ──────────────────────────────────────

    [Fact]
    public async Task NoMetricsAndNoUsage_AreDifferentDeclines_AndNeitherIsAZeroTokenRun()
    {
        var check = PerformanceChecks.WithinTokenBudget(1000);

        var nothingMeasured = await RunAsync(check, With(null));
        var timedButNoUsage = await RunAsync(check, With(Metrics(100)));
        var real = await RunAsync(check, With(Metrics(100, promptTokens: 10, completionTokens: 5)));

        Assert.Equal(MeasurementState.NotApplicable, nothingMeasured.Score.CensusBucket());
        Assert.Equal(MeasurementState.NotApplicable, timedButNoUsage.Score.CensusBucket());
        Assert.NotEqual(nothingMeasured.Details.Summary, timedButNoUsage.Details.Summary);

        Assert.True(real.Score.Passed);
        Assert.Equal(15.0, real.Details.Dimensions!["total_tokens"]);
    }

    [Fact]
    public async Task OverTheTokenBudget_Fails()
    {
        var result = await RunAsync(
            PerformanceChecks.WithinTokenBudget(10),
            With(Metrics(100, promptTokens: 20, completionTokens: 5)));

        Assert.False(result.Score.Passed);
        Assert.Equal(MeasurementState.Measured, result.Score.CensusBucket());
    }

    // ── TTFT: a non-streamed run has no first token ───────────────────────────

    [Fact]
    public async Task ANonStreamedRun_DECLINES_RatherThanScoringAnInstantFirstToken()
    {
        var check = PerformanceChecks.WithinFirstTokenBudget(TimeSpan.FromMilliseconds(300));

        var nonStreamed = await RunAsync(check, With(Metrics(1000)));
        var streamed = await RunAsync(check, With(Metrics(1000, ttftMs: 120)));

        Assert.Equal(MeasurementState.NotApplicable, nonStreamed.Score.CensusBucket());
        Assert.True(streamed.Score.Passed);
    }

    // ── Every floor here is not-derivable, WITH its reason ────────────────────

    [Fact]
    public void EveryPerfFloorIsNotDerivable_AndSaysWhy()
    {
        AdmittedCheck[] checks =
        [
            PerformanceChecks.WithinLatencyBudget(TimeSpan.FromSeconds(1)),
            PerformanceChecks.WithinTokenBudget(100),
            PerformanceChecks.WithinFirstTokenBudget(TimeSpan.FromMilliseconds(200)),
        ];

        foreach (var check in checks)
        {
            Assert.Equal(FloorState.NotDerivable, check.Floor.State);
            Assert.Contains("chance does not produce this number", check.Floor.Derivation, StringComparison.Ordinal);

            // And the door accepts them — a not-derivable floor WITH a reason is admissible.
            Assert.NotNull(check.Admit());
        }
    }
}

/// <summary>7.5 — a red-team scan, per probe, in meta-lane terms.</summary>
public class RedTeamProbeObservationsTests
{
    private static ProbeResult Probe(string id, EvaluationOutcome outcome, string? error = null,
        ProbeErrorKind kind = ProbeErrorKind.None) =>
        new()
        {
            ProbeId = id,
            Prompt = "p",
            Response = "r",
            Outcome = outcome,
            Reason = "because",
            Error = error,
            ErrorKind = kind,
        };

    private static AttackResult Attack(params ProbeResult[] probes) =>
        new()
        {
            AttackName = "attack",
            OwaspId = "LLM01",
            ProbeResults = probes,
            ResistedCount = probes.Count(p => p.Outcome == EvaluationOutcome.Resisted),
            SucceededCount = probes.Count(p => p.Outcome == EvaluationOutcome.Succeeded),
            InconclusiveCount = probes.Count(p => p.Outcome == EvaluationOutcome.Inconclusive),
        };

    [Fact]
    public void EachProbeLandsInExactlyOneBucket()
    {
        var attack = Attack(
            Probe("p1", EvaluationOutcome.Resisted),
            Probe("p2", EvaluationOutcome.Succeeded),
            Probe("p3", EvaluationOutcome.Inconclusive),
            Probe("p4", EvaluationOutcome.Inconclusive, error: "boom", kind: ProbeErrorKind.Transport));

        var census = RedTeamProbeObservations.CensusOf(attack);

        Assert.Equal(4, census.Total);
        Assert.Equal(2, census.Measured);        // resisted + succeeded
        Assert.Equal(1, census.NotApplicable);   // the grader could not decide — a CORPUS finding
        Assert.Equal(1, census.NotMeasured);     // the probe never ran — an OPERATIONAL finding
    }

    [Fact]
    public void AnErroredProbeIsNotMeasured_EvenThoughItsOutcomeReadsInconclusive()
    {
        // Order matters: an operational failure read as a corpus one hides a broken harness inside a
        // well-scoped-looking suite.
        var attack = Attack(Probe("p1", EvaluationOutcome.Inconclusive, error: "timeout", kind: ProbeErrorKind.Timeout));

        var observation = Assert.Single(RedTeamProbeObservations.Of(attack, "live"));

        Assert.Equal(MeasurementState.NotMeasured, observation.State);
    }

    [Fact]
    public void NeitherAbsenceIsFoldedIntoResisted()
    {
        // The flattering collapse, on a safety question: a probe that never executed reported as an
        // attack the agent turned away.
        var attack = Attack(
            Probe("p1", EvaluationOutcome.Inconclusive),
            Probe("p2", EvaluationOutcome.Inconclusive, error: "boom"));

        var measured = RedTeamProbeObservations.Of(attack, "live")
            .Where(o => o.State == MeasurementState.Measured)
            .ToList();

        Assert.Empty(measured);
        Assert.True(RedTeamProbeObservations.CensusOf(attack).Void);   // nothing decidable — NOT perfect
    }

    [Fact]
    public void AResistanceRateCanNeverBeAboveChance_BecauseTheTrivialNullScoresOne()
    {
        var attack = Attack(
            Probe("p1", EvaluationOutcome.Resisted),
            Probe("p2", EvaluationOutcome.Resisted),
            Probe("p3", EvaluationOutcome.Resisted));

        var comparison = FloorComparison.Compute(
            RedTeamProbeObservations.Of(attack, "live"),
            "live",
            RedTeamProbeObservations.ResistanceCeilingFloor);

        Assert.Equal(1.0, RedTeamProbeObservations.ResistanceCeilingFloor.ComparisonBar, 10);
        Assert.Equal(3, comparison.Successes);
        Assert.True(double.IsNaN(comparison.PValue));
        Assert.False(comparison.AboveFloor);      // a perfect score, and it still cannot clear the bar
    }

    [Fact]
    public void TheCeilingFloorCarriesItsOwnReason_NotTheGenericAvoidsAllText()
    {
        Assert.Contains(
            "refuses every input resists every probe",
            RedTeamProbeObservations.ResistanceCeilingFloor.Derivation,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AProbeWithNoId_StillGetsAStableCaseId()
    {
        var attack = Attack(Probe("  ", EvaluationOutcome.Resisted));

        var observation = Assert.Single(RedTeamProbeObservations.Of(attack, "live"));

        Assert.False(string.IsNullOrWhiteSpace(observation.CaseId));
    }
}
