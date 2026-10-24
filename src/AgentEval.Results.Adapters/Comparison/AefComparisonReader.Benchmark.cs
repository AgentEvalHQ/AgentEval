// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Evals.Meta;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Adapters.Reading;
using AgentEval.Results.Runs;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Comparison;

public sealed partial class AefComparisonReader
{
    // The benchmark's own observed summary is a different unit from its probe cases. No resistance
    // reading is promoted to a benchmark score, and ordinary result-tree selection remains a partition.
    private static JsonObject? BenchmarkObservation(AefRunDocuments documents)
    {
        if (documents.Summary?["ext"] is not JsonObject ext || ext["agenteval.bench"] is not JsonObject bench
            || !bench.TryGetPropertyValue("comparison", out var raw)) return null;
        if (raw is not JsonObject profile || profile["version"] is not JsonValue version
            || !version.TryGetValue<int>(out var number) || number != 1)
            throw Bad("unsupported or malformed benchmark comparison profile");
        if (documents.Results.Objects.Any(pair =>
        {
            var line = pair.Value;
            return line["parentResultId"] is not null || line["trial"] is not null
                || (line["ext"] as JsonObject)?.ContainsKey(AefComparisonMetadata.Extension) == true
                || !((AefNode.String(line["path"]) == AefProbeRunWriter.ProbePath && AefNode.String(line["evaluator"]?["id"]) == "agenteval.redteam")
                    || (AefNode.String(line["path"]) == AefProbeRunWriter.ControlPath && AefNode.String(line["evaluator"]?["id"]) == "agenteval.redteam.over-refusal"));
        })) throw Bad("benchmark summary comparison requires probe-only evidence, not an alternate selection over evaluation trees");

        var measurementName = AefNode.String(profile["measurement"]);
        if (!Enum.TryParse<MeasurementState>(measurementName, out var measurement) || !Enum.IsDefined(measurement)
            || measurementName != measurement.ToString() || profile["passed"] is not JsonValue passedValue
            || !passedValue.TryGetValue<bool>(out var passed) || passed && measurement != MeasurementState.Measured
            || AefNode.String(bench["label"]) is not { } label
            || !AefConverter.IsResultText(label, 64)) throw Bad("benchmark measurement or verdict facts are missing or invalid");
        if (profile["observation"] is not JsonObject original || original.ContainsKey("state") || original.ContainsKey("scores")
            || original.ContainsKey("parentResultId") || original.ContainsKey("resultId"))
            throw Bad("benchmark comparison observation is missing or shadows summary facts");
        var observation = original.DeepClone().AsObject();
        var metadata = ComparisonMetadata(observation) ?? throw Bad("benchmark comparison facts are missing");
        if (observation["ext"]!.AsObject().Count != 1 || observation["evaluator"] is not JsonObject evaluator
            || AefNode.String(metadata["id"]) != AefNode.String(observation["caseId"])
            || metadata["selected"] is not JsonValue selection || !selection.TryGetValue<bool>(out var selected) || !selected
            || AefNode.String(metadata["metric"]) != AefCompositeRunWriter.ScoreMetric
            || !AefConverter.IsResultText(AefNode.String(observation["caseId"]) ?? "", 256)
            || !AefConverter.IsResultText(AefNode.String(evaluator["id"]) ?? "", 256)
            || !AefConverter.IsResultText(AefNode.String(evaluator["version"]) ?? "", 64))
            throw Bad("benchmark comparison identity or selection differs from its recorded observation");

        var definitions = AefNode.Objects(documents.Metrics?["metrics"]).ToArray();
        var metric = definitions.SingleOrDefault(m => AefNode.String(m["id"]) == AefCompositeRunWriter.ScoreMetric);
        if (metric is null || AefNode.String(metric["kind"]) != "score" || AefNode.String(metric["direction"]) != "higher_better"
            || AefNode.Number(metric["scale"]?["min"]) != 0 || AefNode.Number(metric["scale"]?["max"]) != 1 || metric["unit"] is not null)
            throw Bad("benchmark comparison requires the declared normalized whole-benchmark score metric");
        var facts = ValidateObservation(observation, definitions.Select(m => (string)m["id"]!).ToHashSet(StringComparer.Ordinal));
        if (AefNode.String(documents.Run?["contentCapture"]) == "off"
            && (metadata["stimulusHash"] is not null || facts.ChanceFloor is { } floor && floor.Derivation != AefComparisonMetadata.OmittedDerivation))
            throw Bad("benchmark comparison contains content-derived facts despite capture being off");
        if (metadata["stimulusHash"] is not null && AefConverter.Sha256Uri(AefNode.String(metadata["stimulusHash"])) != AefNode.String(metadata["stimulusHash"]))
            throw Bad("benchmark stimulus hash is not a canonical SHA-256 digest");

        var state = EvalScoreState.Of(measurement, label, passed);
        var measured = state is AefState.Passed or AefState.Failed or AefState.Warn or AefState.Scored or AefState.Inconclusive;
        observation["state"] = AefNames.Of(state);
        if (measured)
        {
            if (bench["score"] is not JsonValue score || !score.TryGetValue<double>(out var value) || !double.IsFinite(value) || value is < 0 or > 1)
                throw Bad("the measured benchmark has no recorded normalized score");
            observation["scores"] = new JsonArray(new JsonObject { ["metric"] = AefCompositeRunWriter.ScoreMetric, ["value"] = value });
        }
        else if (bench["score"] is not null) throw Bad("an absent benchmark measurement carries a numeric score");
        return observation;
    }
}
