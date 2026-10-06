// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Evals.Agentic.Calibration;
using AgentEval.Evals.Meta;
using Xunit;

namespace AgentEval.Tests.Agentic;

/// <summary>
/// Only a MEASURED verdict is calibration evidence (ADR-030; #203 review, B3a). The runner used to add every result to
/// the accuracy/kappa pairs: a skipped or inapplicable result never equals a gold label, so it read as a disagreement,
/// and its 0.0 placeholder was credited "within score range" whenever a band started at 0.
/// </summary>
public class CalibrationRunnerMeasuredOnlyTests
{
    /// <summary>Returns one fixed score for every entry; the runner is what is under test.</summary>
    private sealed class FixedEval(string key, EvalScore score) : IEval
    {
        public string Key => key;
        public string Name => key;
        public string Category => "test";
        public string Version => "1.0.0";

        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
            Task.FromResult(new EvalResult(
                new(Key, Name, Category, Version), score, new(null, null, null, null, null),
                new("atomic-llm", "judge-x", null, null, null, 0, false), DateTimeOffset.UtcNow));
    }

    private static readonly IReadOnlyDictionary<string, IEval> Evals = new Dictionary<string, IEval>
    {
        ["measured_pass"] = new FixedEval("measured_pass", new EvalScore(0.9, null, "pass", true, 0.7, "none", null)),
        ["skipped"] = new FixedEval("skipped", new EvalScore(0.0, null, "skipped", false, null, "none", null)),
        ["inapplicable"] = new FixedEval("inapplicable", EvalScore.NotApplicable()),
        ["withheld"] = new FixedEval("withheld", new EvalScore(0.95, null, "warn", false, null, "none", null)
        {
            Measurement = MeasurementState.NotMeasured,
        }),
        ["judge_error"] = new FixedEval("judge_error", new EvalScore(0.0, null, "error", false, 0.7, "none", null)),
    };

    private static CalibrationEntry Entry(string id, string key, string expected, double min, double max) =>
        new(id, key, "q", "r", expected, min, max, "why");

    private static async Task<CalibrationCategoryReport> RunAsync(params CalibrationEntry[] entries)
    {
        var runner = new CalibrationRunner(key => Evals.GetValueOrDefault(key));
        var report = await runner.RunAsync([new CalibrationDataset("process", entries)], caseSink: null, limitPerCategory: null);
        return Assert.Single(report.PerCategory.Values);
    }

    [Fact]
    public async Task ResultsThatReachedNoVerdict_AreCounted_NotScored()
    {
        var report = await RunAsync(
            Entry("a", "measured_pass", "pass", 0.7, 1.0),
            Entry("b", "measured_pass", "pass", 0.7, 1.0),
            Entry("c", "skipped", "fail", 0.0, 0.35),
            Entry("d", "inapplicable", "pass", 0.0, 1.0),
            Entry("e", "withheld", "pass", 0.7, 1.0));

        Assert.Equal(2, report.EntryCount);         // only the two measured verdicts were scored
        Assert.Equal(1.0, report.Accuracy);         // the old runner: 2 of 5 → 0.4
        Assert.Equal(2, report.NotMeasured);        // skipped + the composite that withheld its pass
        Assert.Equal(1, report.NotApplicable);
        Assert.Equal(0, report.EvaluationFailures);
    }

    [Fact]
    public async Task AZeroPlaceholder_IsNeverCreditedWithinScoreRange()
    {
        var report = await RunAsync(Entry("c", "skipped", "fail", 0.0, 0.35));

        Assert.Equal(0, report.WithinScoreRange);   // the old runner credited the skip's 0.0 as in band
        Assert.Equal(0, report.EntryCount);
    }

    [Fact]
    public async Task AJudgeThatReturnedNoVerdict_IsAnEvaluationFailure_NotADroppedRow()
    {
        // Dropping it would let a judge outage RAISE accuracy; counting it as a disagreement (the old behaviour) hid it.
        var report = await RunAsync(
            Entry("a", "measured_pass", "pass", 0.7, 1.0),
            Entry("x", "judge_error", "pass", 0.7, 1.0));

        Assert.Equal(1, report.EvaluationFailures);
        Assert.Equal(1, report.EntryCount);
        Assert.Equal(0, report.NotMeasured);
    }

    // ── B6c-7 (mid-branch review): exclusion is by KEY, never by outcome ───────────────────────────────────────────

    /// <summary>Withholds its pass (the tool composites on text-only goldens), and measures only a fail.</summary>
    private sealed class SelectiveEval : IEval
    {
        public string Key => "selective";
        public string Name => "selective";
        public string Category => "test";
        public string Version => "1.0.0";

        public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default) =>
            Task.FromResult(new EvalResult(
                new(Key, Name, Category, Version),
                input.Query == "would-pass"
                    ? new EvalScore(0.95, null, "warn", false, null, "none", null) { Measurement = MeasurementState.NotMeasured }
                    : new EvalScore(0.2, null, "fail", false, 0.7, "high", null),
                new(null, null, null, null, null),
                new("atomic-llm", "judge-x", null, null, null, 0, false), DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task AKeyMeasuredOnlyOnSomeRecords_IsLeftOutWhole_NotScoredOnTheRecordsItsOutcomeSelected()
    {
        var runner = new CalibrationRunner(key => key == "selective" ? new SelectiveEval() : Evals.GetValueOrDefault(key));
        var report = Assert.Single((await runner.RunAsync([new CalibrationDataset("process",
        [
            new CalibrationEntry("s-pass", "selective", "would-pass", "r", "pass", 0.7, 1.0, "gold pass, withheld"),
            new CalibrationEntry("s-fail", "selective", "would-fail", "r", "fail", 0.0, 0.3, "gold fail, measured"),
            Entry("m1", "measured_pass", "pass", 0.7, 1.0),
            Entry("m2", "measured_pass", "pass", 0.7, 1.0),
        ])], caseSink: null, limitPerCategory: null)).PerCategory.Values);

        // Before: the withheld gold-pass dropped out, the gold-fail stayed, and "selective" looked perfect (1 of 1).
        Assert.Equal(["selective"], report.ExcludedKeys);
        Assert.Equal(2, report.EntryCount);               // only the clean key is scored
        Assert.Equal(1, report.ExcludedMeasuredRecords);  // the measured fail left with its key
        Assert.Equal(1, report.NotMeasured);
    }

    [Theory]
    [InlineData(0, false, 1.0, 1.0, "PASS")]
    [InlineData(0, true, 1.0, 1.0, "INCOMPLETE")]   // a key left out: not a measured PASS, whatever the metrics say
    [InlineData(2, true, 1.0, 1.0, "INFRA-FAIL")]   // an evaluation failure first
    [InlineData(0, false, 0.5, 0.2, "FAIL")]
    public void TheCategoryStatus_IsNeverPassWithAKeyLeftOut(int failures, bool excluded, double accuracy, double kappa, string expected)
    {
        var report = new CalibrationCategoryReport("process", 10, accuracy, kappa, 10, 0.0, EvaluationFailures: failures)
        {
            ExcludedKeys = excluded ? ["selective"] : [],
        };

        Assert.Equal(expected, AgentEval.Cli.Commands.BenchAgenticCalibrateCommand.CategoryStatus(report, 0.85, 0.70));
    }
}
