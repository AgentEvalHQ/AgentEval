// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Calibration;
using AgentEval.Guardrails.Judges;

namespace AgentEval.Interop.AssertAi;

/// <summary>One labelled case: the label, and what ASSERT's judge decided.</summary>
/// <param name="CaseId">AgentEval's case id.</param>
/// <param name="Key">The ASSERT case.</param>
/// <param name="Expected"><c>pass</c> or <c>fail</c>.</param>
/// <param name="Actual">ASSERT's verdict as AgentEval reads it (<c>pass</c> or <c>fail</c>), or
/// <see langword="null"/> when the judge decided nothing.</param>
/// <param name="NotMeasuredReason">Why the judge decided nothing.</param>
public sealed record AssertAiCalibrationCase(string CaseId, AssertAiCaseKey Key, string Expected, string? Actual, string? NotMeasuredReason);

/// <summary>How far ASSERT's judge agreed with AgentEval's labels on one run.</summary>
/// <param name="JudgeModels">The judge model(s) of the run's rows.</param>
/// <param name="LabelSet">The labelled cases.</param>
/// <param name="Cases">Every labelled case.</param>
/// <param name="Decided">Cases the judge decided.</param>
/// <param name="Agreed">Decided cases where it matched the label.</param>
/// <param name="Accuracy">Agreed ÷ decided; <see langword="null"/> when none was decided.</param>
/// <param name="AccuracyInterval">A 95% Wilson interval for the accuracy.</param>
/// <param name="Kappa">Cohen's κ; <see langword="null"/> when none was decided or only one label occurs.</param>
/// <param name="DangerousErrors">Labelled failures the judge passed.</param>
/// <param name="FalseAlarms">Labelled passes the judge failed.</param>
/// <param name="NotMeasured">Labelled cases the judge did not decide.</param>
public sealed record AssertAiCalibrationReport(
    IReadOnlyList<string> JudgeModels,
    string LabelSet,
    IReadOnlyList<AssertAiCalibrationCase> Cases,
    int Decided,
    int Agreed,
    double? Accuracy,
    WilsonInterval? AccuracyInterval,
    double? Kappa,
    int DangerousErrors,
    int FalseAlarms,
    int NotMeasured)
{
    /// <summary>The summary to attach to imported results of the same judge.</summary>
    public AssertAiJudgeCalibration ToCalibration() =>
        new(string.Join(", ", JudgeModels), LabelSet, Decided, Accuracy, Kappa, DangerousErrors, NotMeasured);
}

/// <summary>
/// Calibrates ASSERT's judge on AgentEval's labelled cases: export the cases with <see cref="AssertAiJudgeKit"/>, let
/// ASSERT judge them, read the run back, and compare each verdict with its label. A case ASSERT's judge did not decide
/// (judge failure, not judged, no score row, or no category found relevant) is counted as not measured, never as
/// agreement.
/// </summary>
public static class AssertAiCalibration
{
    /// <summary>Compares the run's verdicts with the case map's labels.</summary>
    /// <param name="run">The ASSERT run that judged the exported cases.</param>
    /// <param name="map">The case map written with them.</param>
    /// <exception cref="ArgumentException">No case in the map carries a label.</exception>
    public static AssertAiCalibrationReport Measure(AssertAiRun run, AssertAiCaseMap map)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(map);
        var labelled = map.Cases.Where(c => c.ExpectedVerdict is "pass" or "fail").ToList();
        if (labelled.Count == 0)
        {
            throw new ArgumentException("No case in the map is labelled pass or fail.", nameof(map));
        }

        var results = AssertAiResults.ToEvalResults(run).ToDictionary(r => r.Key, r => r.Result);
        var cases = new List<AssertAiCalibrationCase>();
        foreach (var entry in labelled)
        {
            if (!results.TryGetValue(entry.Key, out var result))
            {
                cases.Add(new(entry.CaseId, entry.Key, entry.ExpectedVerdict!, null, "ASSERT has no row for this case."));
                continue;
            }

            var actual = result.Score.Label is "pass" or "fail" ? result.Score.Label : null;
            cases.Add(new(entry.CaseId, entry.Key, entry.ExpectedVerdict!, actual,
                actual is null ? $"{result.Score.Label}: {result.Details.Summary}" : null));
        }

        var decided = cases.Where(c => c.Actual is not null).ToList();
        var agreed = decided.Count(c => c.Actual == c.Expected);
        double? kappa = null;
        if (decided.Count > 0)
        {
            var k = AgreementMetrics.CohensKappa(decided.Select(c => (c.Expected, c.Actual!)).ToList());
            kappa = double.IsFinite(k) ? k : null;
        }

        return new AssertAiCalibrationReport(
            run.Rows.Select(r => r.JudgeModel).Where(m => m.Length > 0).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            map.LabelSet,
            cases,
            decided.Count,
            agreed,
            decided.Count == 0 ? null : (double)agreed / decided.Count,
            decided.Count == 0 ? null : WilsonInterval.Compute(agreed, decided.Count),
            kappa,
            decided.Count(c => c.Expected == "fail" && c.Actual == "pass"),
            decided.Count(c => c.Expected == "pass" && c.Actual == "fail"),
            cases.Count - decided.Count);
    }
}
