// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text;
using AgentEval.Comparison;
using AgentEval.Models;

namespace AgentEval.Cli.Commands;

/// <summary>
/// The export report for <c>eval --runs N</c> with N above 1: one entry per test case, whose verdict is the stochastic
/// one (its pass rate reaches <c>--success-threshold</c>), with the N runs attached where every format shows them.
/// </summary>
/// <remarks>
/// <see cref="EvaluationReport"/> holds one score per test, and the JUnit, TRX, CSV and Markdown exporters do not write
/// the report's metadata. So the runs travel with each test: as metric columns (<see cref="RunsMetric"/>,
/// <see cref="RunsPassedMetric"/>, <see cref="RunsNotMeasuredMetric"/>, <see cref="PassRateMetric"/>,
/// <see cref="ScoreSdMetric"/>), which every format writes; as the failure message, which names the pass rate and the
/// threshold; and as the test's output (JUnit system-out, TRX stdout), which lists each run. The score is the mean over
/// the measured runs: a run that errored or got no judge verdict is not one, and a test whose such runs leave the
/// verdict open (<see cref="StochasticVerdict.Undecided"/>, <see cref="StochasticVerdict.NotMeasured"/>) is skipped, not
/// failed. Until this existed the stochastic path exported nothing, and an output file left by an earlier run stayed in
/// place, looking current.
/// </remarks>
internal static class StochasticReport
{
    internal const string RunsMetric = "stochastic_runs";
    internal const string RunsPassedMetric = "stochastic_runs_passed";
    internal const string RunsNotMeasuredMetric = "stochastic_runs_not_measured";
    internal const string PassRateMetric = "stochastic_pass_rate";
    internal const string ScoreSdMetric = "stochastic_score_sd";

    public static EvaluationReport Build(
        IReadOnlyList<StochasticResult> results,
        string suiteName,
        int runs,
        double successThreshold,
        DateTimeOffset startTime,
        DateTimeOffset endTime,
        string? agentName = null,
        string? modelName = null,
        string? endpoint = null)
    {
        ArgumentNullException.ThrowIfNull(results);

        var tests = results.Select(r => Map(r, suiteName)).ToList();
        // A test whose unmeasured runs leave its verdict open (undecided, or nothing measured) is skipped, not failed. The
        // score averages the tests that measured at least one run; one with none has no score.
        var scored = results.Where(r => r.MeasuredCount > 0).ToList();
        return new EvaluationReport
        {
            Name = string.Create(CultureInfo.InvariantCulture, $"{suiteName} (stochastic, {runs} runs per test)"),
            StartTime = startTime,
            EndTime = endTime,
            TotalTests = tests.Count,
            PassedTests = tests.Count(t => t.Passed),
            FailedTests = tests.Count(t => !t.Passed && !t.Skipped),
            SkippedTests = tests.Count(t => t.Skipped),
            OverallScore = scored.Count > 0 ? scored.Average(r => r.Statistics.MeanScore) : null,
            Agent = agentName is null && modelName is null && endpoint is null
                ? null
                : new AgentInfo { Name = agentName, Model = modelName, Endpoint = endpoint },
            Metadata = new Dictionary<string, string>
            {
                ["Mode"] = "stochastic",
                ["RunsPerTest"] = runs.ToString(CultureInfo.InvariantCulture),
                ["SuccessThreshold"] = successThreshold.ToString("0.###", CultureInfo.InvariantCulture),
            },
            TestResults = tests,
        };
    }

    private static TestResultSummary Map(StochasticResult result, string suiteName)
    {
        var stats = result.Statistics;
        var runs = result.IndividualResults.Count;
        // Over every run, as the verdict is decided: a run that was not measured is not passed.
        var passRate = runs > 0 ? (double)result.PassedCount / runs * 100 : 0;
        var threshold = result.Options.SuccessRateThreshold * 100;
        var verdict = result.Verdict;
        var noVerdict = verdict is StochasticVerdict.Undecided or StochasticVerdict.NotMeasured;

        var metrics = new Dictionary<string, double>
        {
            [RunsMetric] = runs,
            [RunsPassedMetric] = result.PassedCount,
            [RunsNotMeasuredMetric] = result.NotMeasuredCount,
            [PassRateMetric] = passRate,
        };
        if (result.MeasuredCount > 0)
            metrics[ScoreSdMetric] = stats.StandardDeviation;   // the spread of nothing is not 0

        return new TestResultSummary
        {
            Name = result.TestCase.Name,
            Category = suiteName,
            // The mean over the measured runs; none when no run was measured (the test is then skipped). The duration
            // sums the timed runs; none when no run was timed.
            Score = result.MeasuredCount > 0 ? stats.MeanScore : null,
            Passed = result.Passed,
            NotMeasured = noVerdict,
            Skipped = noVerdict,
            DurationMs = result.TotalDuration is { } totalDuration ? (long)totalDuration.TotalMilliseconds : null,
            Error = result.Passed
                ? null
                : verdict switch
                {
                    StochasticVerdict.NotMeasured => string.Create(CultureInfo.InvariantCulture,
                        $"Not measured: none of {runs} runs was measured (errored, or the judge produced no verdict)."),
                    StochasticVerdict.Undecided => string.Create(CultureInfo.InvariantCulture,
                        $"Not measured: {result.NotMeasuredCount} of {runs} runs were not measured, so whether the {threshold:F0}% " +
                        $"threshold is reached is undecided ({result.PassedCount} of {runs} passed)."),
                    _ => string.Create(CultureInfo.InvariantCulture,
                        $"{result.PassedCount} of {runs} runs passed ({passRate:F1}%), below the {threshold:F0}% threshold."),
                },
            Output = Describe(result),
            MetricScores = metrics,
        };
    }

    private static string Describe(StochasticResult result)
    {
        var stats = result.Statistics;
        var runs = result.IndividualResults.Count;
        var sb = new StringBuilder();
        var passPercent = runs > 0 ? (double)result.PassedCount / runs * 100 : 0;
        var notMeasured = result.NotMeasuredCount > 0 ? $", {result.NotMeasuredCount} not measured" : "";
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"{runs} runs, {result.PassedCount} passed ({passPercent:F1}%){notMeasured}; the threshold is {result.Options.SuccessRateThreshold * 100:F0}%."));
        if (result.MeasuredCount == 0)
        {
            sb.AppendLine("Score: not measured (no run produced a verdict).");
        }
        else
        {
            // Over the measured runs only.
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"Score over {result.MeasuredCount} measured run(s): mean {stats.MeanScore:F1}, median {stats.MedianScore:F1}, " +
                $"SD {stats.StandardDeviation:F1}, min {stats.MinScore}, max {stats.MaxScore}"));
            if (stats.ConfidenceInterval is { } ci)
                sb.Append(string.Create(CultureInfo.InvariantCulture,
                    $"; {ci.Level * 100:F0}% CI for the mean [{ci.Lower:F1}, {ci.Upper:F1}]"));
            sb.AppendLine(".");
        }

        for (var i = 0; i < result.IndividualResults.Count; i++)
        {
            var run = result.IndividualResults[i];
            sb.AppendLine(run.HasError
                ? string.Create(CultureInfo.InvariantCulture, $"Run {i + 1}: error: {run.Error!.Message}")
                : run.JudgeFailed
                    ? string.Create(CultureInfo.InvariantCulture, $"Run {i + 1}: not measured: the judge produced no verdict")
                    : string.Create(CultureInfo.InvariantCulture, $"Run {i + 1}: {(run.Passed ? "pass" : "fail")}, score {run.Score}"));
        }

        return sb.ToString().TrimEnd();
    }
}
