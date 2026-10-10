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
/// <see cref="RunsPassedMetric"/>, <see cref="PassRateMetric"/>, <see cref="ScoreSdMetric"/>), which every format
/// writes; as the failure message, which names the pass rate and the threshold; and as the test's output (JUnit
/// system-out, TRX stdout), which lists each run. The score is the mean over the runs. Until this existed the stochastic
/// path exported nothing, and an output file left by an earlier run stayed in place, looking current.
/// </remarks>
internal static class StochasticReport
{
    internal const string RunsMetric = "stochastic_runs";
    internal const string RunsPassedMetric = "stochastic_runs_passed";
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
        return new EvaluationReport
        {
            Name = string.Create(CultureInfo.InvariantCulture, $"{suiteName} (stochastic, {runs} runs per test)"),
            StartTime = startTime,
            EndTime = endTime,
            TotalTests = tests.Count,
            PassedTests = tests.Count(t => t.Passed),
            FailedTests = tests.Count(t => !t.Passed),
            OverallScore = tests.Count > 0 ? tests.Average(t => t.Score) : 0,
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
        var passRate = stats.PassRate * 100;
        var threshold = result.Options.SuccessRateThreshold * 100;

        return new TestResultSummary
        {
            Name = result.TestCase.Name,
            Category = suiteName,
            Score = stats.MeanScore,
            Passed = result.Passed,
            DurationMs = (long)result.TotalDuration.TotalMilliseconds,
            Error = result.Passed
                ? null
                : string.Create(CultureInfo.InvariantCulture,
                    $"{result.PassedCount} of {runs} runs passed ({passRate:F1}%), below the {threshold:F0}% threshold."),
            Output = Describe(result),
            MetricScores = new Dictionary<string, double>
            {
                [RunsMetric] = runs,
                [RunsPassedMetric] = result.PassedCount,
                [PassRateMetric] = passRate,
                [ScoreSdMetric] = stats.StandardDeviation,
            },
        };
    }

    private static string Describe(StochasticResult result)
    {
        var stats = result.Statistics;
        var sb = new StringBuilder();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture,
            $"{result.IndividualResults.Count} runs, {result.PassedCount} passed ({stats.PassRate * 100:F1}%); " +
            $"the threshold is {result.Options.SuccessRateThreshold * 100:F0}%."));
        sb.Append(string.Create(CultureInfo.InvariantCulture,
            $"Score: mean {stats.MeanScore:F1}, median {stats.MedianScore:F1}, SD {stats.StandardDeviation:F1}, " +
            $"min {stats.MinScore}, max {stats.MaxScore}"));
        if (stats.ConfidenceInterval is { } ci)
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"; {ci.Level * 100:F0}% CI for the mean [{ci.Lower:F1}, {ci.Upper:F1}]"));
        sb.AppendLine(".");

        for (var i = 0; i < result.IndividualResults.Count; i++)
        {
            var run = result.IndividualResults[i];
            sb.AppendLine(run.HasError
                ? string.Create(CultureInfo.InvariantCulture, $"Run {i + 1}: error: {run.Error!.Message}")
                : string.Create(CultureInfo.InvariantCulture, $"Run {i + 1}: {(run.Passed ? "pass" : "fail")}, score {run.Score}"));
        }

        return sb.ToString().TrimEnd();
    }
}
