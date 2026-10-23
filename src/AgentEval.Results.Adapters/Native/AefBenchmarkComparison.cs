// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.Results.Adapters.Comparison;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Native;

/// <summary>The producer's observed whole-benchmark comparison unit, distinct from its individual probe cases.</summary>
/// <param name="Id">The historical scenario identity.</param>
/// <param name="Result">The original benchmark evaluation, including typed measurement and instrument facts.</param>
/// <param name="SubjectModel">Known subject model context, used only when the result did not record its own observation.</param>
public sealed record AefBenchmarkComparison(string Id, EvalResult Result, string? SubjectModel = null)
{
    internal JsonObject Encode(AefContentCapture capture)
    {
        ArgumentNullException.ThrowIfNull(Result);
        if (!AefConverter.IsResultText(Id ?? "", 256) || Result.Metric is null || Result.Score is null || Result.Provenance is null
            || !AefConverter.IsResultText(Result.Metric.Key ?? "", 256) || !AefConverter.IsResultText(Result.Metric.Version ?? "", 64)
            || !AefConverter.IsResultText(Result.Score.Label ?? "", 64) || !Enum.IsDefined(Result.Score.Measurement)
            || !double.IsFinite(Result.Score.Value) || Result.Score.Value is < 0 or > 1)
            throw new ArgumentException("Expected: an original normalized benchmark observation with recorded identity. Actual: invalid comparison facts. Suggestions: supply the evaluation that produced the benchmark verdict. Because: probe resistance cannot substitute for the benchmark score.");
        var subjectModel = Result.Observation is { } observed ? observed.SubjectModel : SubjectModel;
        var attributed = Result with { ComparisonObservation = EvalResultPersistence.ComparabilityOf(Result, subjectModel) };
        return new()
        {
            ["version"] = 1, ["measurement"] = Result.Score.Measurement.ToString(), ["passed"] = Result.Score.Passed,
            ["observation"] = new JsonObject
            {
                ["caseId"] = Id,
                ["evaluator"] = new JsonObject { ["id"] = Result.Metric.Key, ["version"] = Result.Metric.Version },
                ["ext"] = AefComparisonMetadata.Attach(null, attributed, capture, selected: true, id: Id),
            },
        };
    }
}
