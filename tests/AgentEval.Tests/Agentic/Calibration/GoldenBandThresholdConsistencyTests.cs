// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic;
using AgentEval.Evals.Agentic.Calibration;
using Xunit;
using Xunit.Abstractions;

namespace AgentEval.Tests.Agentic.Calibration;

/// <summary>
/// A golden case declares an expected verdict and a score band that should produce it. If the evaluator's pass
/// threshold sits inside the band, a judge that lands exactly where the case says it should is still marked wrong:
/// the calibration then measures the threshold, not the judge (N3 follow-up §3, "X3").
/// </summary>
/// <remarks>
/// The check is structural and free. Every judge-graded case is run through its real evaluator with a fake judge
/// pinned at the band edge: the bottom of the band for a "pass" case, the top for a "fail" case. The verdict must
/// match the label. A case the evaluator decides without consulting the judge is not a band question and is skipped.
/// </remarks>
public class GoldenBandThresholdConsistencyTests(ITestOutputHelper output)
{
    /// <summary>A judge that answers every call with one fixed score and records that it was asked.</summary>
    private sealed class EdgeJudge(double score) : IEvaluator
    {
        public int Calls { get; private set; }

        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken ct = default)
        {
            Calls++;
            var list = criteria.ToList();
            return Task.FromResult(new EvaluationResult
            {
                OverallScore = (int)Math.Round(score * 100, MidpointRounding.AwayFromZero),
                Summary = $"edge score {score}",
                CriteriaResults = list.Select(c => new CriterionResult { Criterion = c, Met = score >= 0.5, Explanation = "edge" }).ToList(),
            });
        }
    }

    [Fact]
    public async Task EveryGoldenBand_ProducesItsExpectedVerdict_AtTheBandEdge()
    {
        AgenticEvalRegistration.Register();
        var datasets = await new CalibrationDatasetLoader()
            .LoadAllFromAssemblyAsync(typeof(GoldenBandThresholdConsistencyTests).Assembly);

        var conflicts = new List<string>();
        var checkedCases = 0;
        foreach (var dataset in datasets)
        {
            foreach (var entry in dataset.Entries)
            {
                var edge = entry.ExpectedVerdict == "pass" ? entry.ExpectedScoreMin : entry.ExpectedScoreMax;
                var judge = new EdgeJudge(edge);
                var eval = EvalRegistry.Shared.Resolve(entry.EvaluatorKey, judge, "edge-judge");
                if (eval is null)
                    continue;

                var result = await eval.EvaluateAsync(entry.ToEvalInput());
                // Decided without the judge, or no verdict at all — by STATE, not label: a composite that withheld its pass
                // because a required component did not run (e.g. tool_input_accuracy on a text-only golden record, whose
                // schema check has no tool definitions to read) reports warn but measured nothing it could stand on.
                if (judge.Calls == 0 || result.Score.Label is not ("pass" or "fail" or "warn")
                    || result.Score.CensusBucket() != AgentEval.Evals.Meta.MeasurementState.Measured)
                    continue;   // not a band question

                checkedCases++;
                var verdict = result.Score.Passed ? "pass" : "fail";
                if (verdict != entry.ExpectedVerdict)
                {
                    conflicts.Add(
                        $"{dataset.CategoryKey}/{entry.EvaluatorKey}/{entry.ScenarioId}: expects {entry.ExpectedVerdict} in " +
                        $"[{entry.ExpectedScoreMin:0.00}, {entry.ExpectedScoreMax:0.00}], but a judge at {edge:0.00} gives " +
                        $"{result.Score.Label} (threshold {result.Score.Threshold?.ToString("0.00") ?? "?"})");
                }
            }
        }

        output.WriteLine($"checked {checkedCases} judge-graded golden cases; {conflicts.Count} conflict(s)");
        foreach (var line in conflicts)
            output.WriteLine(line);

        Assert.True(checkedCases > 0, "no judge-graded golden case was checked; the check measured nothing");
        Assert.Empty(conflicts);
    }
}
