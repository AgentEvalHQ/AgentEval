// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Assertions;
using AgentEval.Cli.Commands;
using AgentEval.Comparison;
using AgentEval.Core;
using AgentEval.Models;
using AgentEval.Output;
using AgentEval.Results.Adapters.Native;
using Xunit;

namespace AgentEval.Tests.Comparison;

/// <summary>
/// A stochastic run that threw, or whose judge produced no verdict, carries Score 0 / Passed false as placeholders (the
/// harness sets them). It is not measured: kept in IndividualResults, left out of every statistic, and the verdict over
/// all runs says when such runs leave it open (T06, T13-15, T13-14). The scripted harness below supplies run results as
/// inputs to this arithmetic; it grades nothing.
/// </summary>
public class StochasticNotMeasuredTests
{
    private static readonly TestCase Case = new() { Name = "refund-policy", Input = "q" };

    private static TestResult Pass(int score = 90) => new() { TestName = "t", Score = score, Passed = true };

    private static TestResult Fail(int score = 40) => new() { TestName = "t", Score = score, Passed = false };

    // As MAFEvaluationHarness writes a run whose model call threw.
    private static TestResult Errored() => new()
    {
        TestName = "t", Score = 0, Passed = false, Error = new HttpRequestException("429 Too Many Requests"),
    };

    // As MAFEvaluationHarness writes a run whose judge produced no verdict.
    private static TestResult JudgeFailed() => new() { TestName = "t", Score = 0, Passed = false, JudgeFailed = true };

    private static Task<StochasticResult> RunAsync(double threshold, params TestResult[] runs) =>
        new StochasticRunner(new ScriptedHarness(runs)).RunStochasticTestAsync(
            new NamedAgent("agent"), Case, new StochasticOptions(Runs: runs.Length, SuccessRateThreshold: threshold));

    [Fact]
    public async Task AnErroredRun_IsKeptOutOfTheStatistics()
    {
        // Today: mean 60, min 0, sample 3, SD 51.96.
        var result = await RunAsync(0.6, Pass(), Errored(), Pass());

        Assert.Equal(90, result.Statistics.MeanScore);
        Assert.Equal(90, result.Statistics.MinScore);
        Assert.Equal(2, result.Statistics.SampleSize);
        Assert.Equal(0, result.Statistics.StandardDeviation);
        Assert.Equal(3, result.IndividualResults.Count);
        Assert.Equal(1, result.NotMeasuredCount);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public async Task AJudgeFailedRun_IsKeptOutOfTheStatistics()
    {
        var result = await RunAsync(0.6, Pass(), JudgeFailed(), Pass());

        Assert.Equal(90, result.Statistics.MeanScore);
        Assert.Equal(2, result.Statistics.SampleSize);
        Assert.Equal(1.0, result.Statistics.PassRate);
        Assert.Equal(1, result.NotMeasuredCount);
    }

    [Fact]
    public async Task EveryRunErrored_IsNotMeasured_NotAMeasuredFailAtZero()
    {
        var result = await RunAsync(0.6, Errored(), Errored(), Errored());

        Assert.Equal(0, result.Statistics.SampleSize);
        Assert.Null(result.Statistics.ConfidenceInterval);   // today (0, 0, 0.95)
        Assert.Equal(StochasticVerdict.NotMeasured, result.Verdict);
        Assert.False(result.Passed);
        Assert.DoesNotContain("FAILED", result.Summary);
        Assert.Contains("NOT MEASURED", result.Summary);
    }

    [Fact]
    public async Task UnmeasuredRunsThatCouldStillReachTheThreshold_LeaveTheVerdictUndecided()
    {
        // 1 pass, 1 fail, 3 errored at 60%: three passing reruns would reach 3 of 5, three failing ones would not.
        var result = await RunAsync(0.6, Pass(), Fail(), Errored(), Errored(), Errored());

        Assert.Equal(StochasticVerdict.Undecided, result.Verdict);
        Assert.False(result.Passed);
        Assert.Contains("not measured", result.Summary);
        Assert.DoesNotContain("FAILED", result.Summary);   // today "❌ FAILED: 1/5 runs passed (20.0% < 60% threshold)"
    }

    [Fact]
    public async Task OnePassAndFourErrored_IsNotAPass()
    {
        // Guard: a pass rate over the measured runs alone would read 100% and pass.
        var result = await RunAsync(0.6, Pass(), Errored(), Errored(), Errored(), Errored());

        Assert.False(result.Passed);
        Assert.Equal(StochasticVerdict.Undecided, result.Verdict);
        Assert.Equal(1.0, result.Statistics.PassRate);   // over the one measured run: not the verdict
    }

    [Fact]
    public async Task PassesThatReachTheThresholdOnTheirOwn_PassDespiteAnUnmeasuredRun()
    {
        // 3 of 5 passed at 60%: no unmeasured run can undo that (the AtLeast rule).
        var result = await RunAsync(0.6, Pass(), Pass(), Pass(), Fail(), Errored());

        Assert.Equal(StochasticVerdict.Passed, result.Verdict);
        Assert.True(result.Passed);
        Assert.Contains("1 not measured", result.Summary);
    }

    [Fact]
    public async Task UnmeasuredRunsThatCouldNotRescueIt_StillFail()
    {
        // 0 of 5 passed, 1 errored, at 60%: even a passing rerun reaches only 1 of 5.
        var result = await RunAsync(0.6, Fail(), Fail(), Fail(), Fail(), Errored());

        Assert.Equal(StochasticVerdict.Failed, result.Verdict);
        Assert.False(result.Passed);
        Assert.Contains("FAILED", result.Summary);
    }

    [Theory]
    [InlineData(0.6, true)]
    [InlineData(0.8, false)]
    public async Task WithEveryRunMeasured_TheVerdictIsTheOneItAlwaysWas(double threshold, bool passed)
    {
        // Guard: 3 of 5 passed, nothing unmeasured — passed / total >= threshold, as before.
        var result = await RunAsync(threshold, Pass(), Pass(), Pass(), Fail(), Fail());

        Assert.Equal(passed, result.Passed);
        Assert.Equal(passed ? StochasticVerdict.Passed : StochasticVerdict.Failed, result.Verdict);
        Assert.Equal(5, result.Statistics.SampleSize);
    }

    [Theory]
    [InlineData(0.6)]
    [InlineData(0.8)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(0.34)]
    public void TheVerdictRule_AgreesWithTheAefRollup_OnEveryMix(double threshold)
    {
        // The AEF writer rolls the same trials up under AtLeast(requirePasses): passed when the passes reach it, failed
        // when the passes and the unmeasured together cannot, otherwise an error (undecided). Census over every mix.
        for (var runs = 3; runs <= 7; runs++)
        {
            var requirePasses = AefEvalWriter.RequirePasses(threshold, runs);
            for (var passes = 0; passes <= runs; passes++)
            {
                for (var unmeasured = 0; passes + unmeasured <= runs; unmeasured++)
                {
                    var verdict = StochasticResult.DecideVerdict(passes, unmeasured, runs, threshold);
                    var expected = unmeasured == runs ? StochasticVerdict.NotMeasured
                        : passes >= requirePasses ? StochasticVerdict.Passed
                        : passes + unmeasured >= requirePasses ? StochasticVerdict.Undecided
                        : StochasticVerdict.Failed;
                    Assert.True(expected == verdict,
                        $"runs {runs}, passes {passes}, unmeasured {unmeasured}, threshold {threshold}: {verdict}, AEF {expected}");
                }
            }
        }
    }

    // ── Assertions ────────────────────────────────────────────────────────────

    [Fact]
    public async Task HaveStandardDeviationAtMost_WhenEveryRunErrored_Throws_NotAZeroSpreadPass()
    {
        var result = await RunAsync(0.6, Errored(), Errored(), Errored());

        var ex = Assert.Throws<StochasticAssertionException>(() => result.Should().HaveStandardDeviationAtMost(10));
        Assert.Contains("not measured", ex.Message);
    }

    [Fact]
    public async Task HaveMeanScoreAtLeast_WithAnUnmeasuredRun_Throws()
    {
        // Strict: the run that errored might have scored anything.
        var result = await RunAsync(0.6, Pass(), JudgeFailed(), Pass());

        var ex = Assert.Throws<StochasticAssertionException>(() => result.Should().HaveMeanScoreAtLeast(80));
        Assert.Contains("not measured", ex.Message);
    }

    [Fact]
    public async Task HavePassRateAtLeast_WhenUndecided_ThrowsNotMeasured()
    {
        // Today: "Expected pass rate >= 60.0%, but was 20.0%".
        var result = await RunAsync(0.6, Pass(), Fail(), Errored(), Errored(), Errored());

        var ex = Assert.Throws<StochasticAssertionException>(() => result.Should().HavePassRateAtLeast());
        Assert.Contains("not measured", ex.Message);
    }

    [Fact]
    public async Task HavePassRateAtLeast_WhenThePassesReachItOnTheirOwn_Passes()
    {
        var result = await RunAsync(0.6, Pass(), Pass(), Pass(), Fail(), Errored());

        Assert.Null(Record.Exception(() => result.Should().HavePassRateAtLeast()));
    }

    [Fact]
    public async Task HaveNoFailures_WithNoMeasuredFailureButAnUnmeasuredRun_ThrowsNotMeasured()
    {
        var result = await RunAsync(0.6, Pass(), Pass(), Errored());

        var ex = Assert.Throws<StochasticAssertionException>(() => result.Should().HaveNoFailures());
        Assert.Contains("not measured", ex.Message);
    }

    // ── The CLI export (eval --runs N) ────────────────────────────────────────

    [Fact]
    public async Task TheExport_ForAnUndecidedTest_SaysNotMeasured_NotBelowTheThreshold()
    {
        var result = await RunAsync(0.6, Pass(), Fail(), Errored(), Errored(), Errored());

        var report = StochasticReport.Build([result], "BatchEvaluation", 5, 0.6, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var test = Assert.Single(report.TestResults);
        Assert.DoesNotContain("below the", test.Error);
        Assert.Contains("not measured", test.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, test.MetricScores[StochasticReport.RunsNotMeasuredMetric]);
        Assert.Equal(65, test.Score);   // the mean of the measured runs (90 and 40), not 26 with three placeholder 0s
        Assert.True(test.NotMeasured);
        Assert.Equal(0, report.FailedTests);
        Assert.Equal(1, report.SkippedTests);
    }

    [Fact]
    public async Task TheExport_ForATestWithNoMeasuredRun_IsSkipped()
    {
        var result = await RunAsync(0.6, JudgeFailed(), JudgeFailed(), JudgeFailed());

        var report = StochasticReport.Build([result], "BatchEvaluation", 3, 0.6, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var test = Assert.Single(report.TestResults);
        Assert.True(test.Skipped);
        Assert.False(test.MetricScores.ContainsKey(StochasticReport.ScoreSdMetric));
        Assert.Contains("Run 1: not measured: the judge produced no verdict", test.Output);
    }

    [Fact]
    public async Task TheExitCode_ForAnUndecidedOrUnmeasuredTest_IsInconclusive_NotATestFailure()
    {
        // D-14: `eval --runs N` exited 1, "a test failed", for a test its unmeasured runs could still decide.
        var passed = await RunAsync(0.6, Pass(), Pass(), Pass());
        var failed = await RunAsync(0.6, Fail(), Fail(), Pass());
        var undecided = await RunAsync(0.6, Pass(), Fail(), Errored(), JudgeFailed(), Pass());
        var notMeasured = await RunAsync(0.6, Errored(), Errored(), Errored());
        Assert.Equal(StochasticVerdict.Undecided, undecided.Verdict);
        Assert.Equal(StochasticVerdict.Failed, failed.Verdict);

        Assert.Equal(AgentEval.Cli.ExitCodes.Success, EvalCommand.StochasticExitCode([passed]));
        Assert.Equal(AgentEval.Cli.ExitCodes.GateIndeterminate, EvalCommand.StochasticExitCode([passed, undecided]));
        Assert.Equal(AgentEval.Cli.ExitCodes.GateIndeterminate, EvalCommand.StochasticExitCode([notMeasured]));
        // A failure still wins: the run is not inconclusive when a test failed.
        Assert.Equal(AgentEval.Cli.ExitCodes.TestFailure, EvalCommand.StochasticExitCode([failed, undecided]));
    }

    // ── Model comparison (decision D-15) ──────────────────────────────────────

    [Fact]
    public async Task AModelWhoseRunsAllErrored_IsNotRankedOnAPlaceholderZero()
    {
        // Today it is ranked last on quality 0.
        var harness = new ByAgentHarness(new()
        {
            ["working"] = [Pass(80), Pass(80), Pass(80)],
            ["dead"] = [Errored(), Errored(), Errored()],
        });

        var result = await Compare(harness, "working", "dead");

        Assert.Equal("Working", result.Winner.ModelName);
        Assert.DoesNotContain(result.Ranking, r => r.ModelName == "Dead");
        Assert.Contains(result.ModelResults, m => m.ModelName == "Dead");   // still reported, with its runs
        Assert.Contains("not ranked: Dead", result.ToGitHubComment());
    }

    [Fact]
    public async Task TheComparisonSummary_NamesTheModelItCouldNotRank()
    {
        // Today: the summary lists the ranked models only, and the unmeasured one silently disappears.
        var harness = new ByAgentHarness(new()
        {
            ["working"] = [Pass(80), Pass(80), Pass(80)],
            ["dead"] = [Errored(), Errored(), Errored()],
        });

        var result = await Compare(harness, "working", "dead");

        Assert.NotNull(result.UnrankedReason);
        Assert.Contains("Dead", result.UnrankedReason);
        Assert.Contains("not ranked", result.UnrankedReason);
        Assert.Contains("Not ranked — no run of Dead was measured", result.Summary);
    }

    [Fact]
    public async Task EveryModelRanked_HasNoUnrankedReason()
    {
        var harness = new ByAgentHarness(new()
        {
            ["a"] = [Pass(80), Pass(80), Pass(80)],
            ["b"] = [Pass(70), Fail(), Pass(70)],
        });

        var result = await Compare(harness, "a", "b");

        Assert.Null(result.UnrankedReason);
        Assert.DoesNotContain("Not ranked", result.Summary);
    }

    [Fact]
    public async Task AModelsPassRate_IsOverEveryRun_SoACrashedRunCountsAgainstIt()
    {
        // D-15. Statistics now describe the measured runs only, so ModelResult.PassRate read 100% for two passes and a
        // crash; a run that crashed delivered nothing.
        var harness = new ByAgentHarness(new()
        {
            ["steady"] = [Pass(90), Pass(90), Pass(90)],
            ["crashy"] = [Pass(90), Pass(90), Errored()],
        });

        var result = await Compare(harness, "steady", "crashy");

        var crashy = result.ModelResults.Single(m => m.ModelName == "Crashy");
        Assert.Equal(2.0 / 3, crashy.PassRate, 6);
        Assert.Equal(1.0, crashy.StochasticResult.Statistics.PassRate);   // the measured runs' share, unchanged
        Assert.Equal(90, crashy.MeanScore);
    }

    [Fact]
    public async Task WhenNoModelWasMeasured_TheComparisonThrows_WithTheResults()
    {
        var harness = new ByAgentHarness(new()
        {
            ["a"] = [Errored(), Errored(), Errored()],
            ["b"] = [JudgeFailed(), JudgeFailed(), JudgeFailed()],
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Compare(harness, "a", "b"));

        Assert.Contains("No model can be ranked", ex.Message);
        Assert.IsAssignableFrom<IReadOnlyList<ModelResult>>(ex.Data["ModelResults"]);
    }

    [Fact]
    public async Task ACrashedRun_StillCountsAgainstReliability()
    {
        // Same quality on the runs each finished; the one that crashed delivered less, so it is less reliable.
        var harness = new ByAgentHarness(new()
        {
            ["steady"] = [Pass(90), Pass(90), Pass(90)],
            ["crashy"] = [Pass(90), Pass(90), Errored()],
        });

        var result = await Compare(harness, "steady", "crashy");

        var steady = result.Ranking.Single(r => r.ModelName == "Steady");
        var crashy = result.Ranking.Single(r => r.ModelName == "Crashy");
        Assert.Equal(steady.QualityScore, crashy.QualityScore);
        Assert.True(steady.ReliabilityScore > crashy.ReliabilityScore);
        Assert.Equal("Steady", result.Winner.ModelName);
    }

    // ── Reports (T13-14) ──────────────────────────────────────────────────────

    [Fact]
    public async Task TheTable_ShowsNoDurationOrToolCalls_ForRunsThatRecordedNone()
    {
        // No run carries performance or tool usage: a 0 ms mean and 0 calls per run would be made up.
        var result = await RunAsync(0.6, Pass(), Pass(), Pass());

        Assert.Null(result.ToolCallCountStats);
        var table = TableFormatter.ToTableString(result, "t", new OutputOptions { UseColors = false });
        var duration = table.Split('\n').Single(l => l.Contains("Duration (ms)", StringComparison.Ordinal));
        var tools = table.Split('\n').Single(l => l.Contains("Tool Calls/Run", StringComparison.Ordinal));
        Assert.Contains("N/A", duration);
        Assert.Contains("N/A", tools);
    }

    private static Task<ModelComparisonResult> Compare(IEvaluationHarness harness, params string[] agents) =>
        new ModelComparer(new StochasticRunner(harness)).CompareModelsAsync(
            agents.Select(a => (IAgentFactory)new DelegateAgentFactory(a, char.ToUpperInvariant(a[0]) + a[1..], () => new NamedAgent(a)))
                .ToList(),
            Case,
            new ModelComparisonOptions(RunsPerModel: 3, EnableCostAnalysis: false));

    /// <summary>Hands out the scripted run results in order; the agent is never called.</summary>
    private sealed class ScriptedHarness(IReadOnlyList<TestResult> runs) : IEvaluationHarness
    {
        private int _next;

        public Task<TestResult> RunEvaluationAsync(
            IEvaluableAgent agent, TestCase testCase, EvaluationOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(runs[Interlocked.Increment(ref _next) - 1]);
    }

    /// <summary>Hands out each agent's scripted run results in order, keyed by the agent's name.</summary>
    private sealed class ByAgentHarness(Dictionary<string, TestResult[]> runsByAgent) : IEvaluationHarness
    {
        private readonly Dictionary<string, int> _next = [];

        public Task<TestResult> RunEvaluationAsync(
            IEvaluableAgent agent, TestCase testCase, EvaluationOptions? options = null, CancellationToken cancellationToken = default)
        {
            lock (_next)
            {
                var index = _next.GetValueOrDefault(agent.Name);
                _next[agent.Name] = index + 1;
                return Task.FromResult(runsByAgent[agent.Name][index]);
            }
        }
    }

    private sealed class NamedAgent(string name) : IEvaluableAgent
    {
        public string Name => name;

        public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("the scripted harness never calls the agent");
    }
}
