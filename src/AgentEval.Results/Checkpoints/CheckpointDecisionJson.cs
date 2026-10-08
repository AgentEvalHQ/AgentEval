// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace AgentEval.Results.Checkpoints;

/// <summary>
/// The decision function's input and output in their AEF wire form (contracts/aef/1/schemas/*/decision.schema.json):
/// statuses as passed / failed / missing / not_measured / incomparable / stale, outcomes in lower case.
/// </summary>
public static class CheckpointDecisionJson
{
    /// <summary>Reads a decision input (decision.schema.json#/$defs/input).</summary>
    /// <exception cref="FormatException">A required field is missing or a value is not one the format allows.</exception>
    public static CheckpointDecisionInput ReadInput(JsonNode input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var lanes = (input["lanes"] as JsonArray ?? throw new FormatException("lanes is required.")).Select(node =>
        {
            var lane = node ?? throw new FormatException("A lane is null.");
            var result = lane["result"];
            LaneEvidence? evidence = result is null
                ? null
                : new LaneEvidence(
                    Status((string?)result["status"]),
                    Required(result, "subjectVersion"),
                    AefTime.Parse(Required(result, "oldestClosedAt")),
                    result["axes"] is JsonArray axes ? axes.Select(a => (string)a!).ToList() : null);
            return new LaneInput(Required(lane, "lane"), (bool?)lane["blocking"] ?? throw new FormatException("blocking is required."),
                evidence, (string?)lane["freshness"]);
        }).ToList();

        return new CheckpointDecisionInput(
            Required(input, "subjectVersion"), AefTime.Parse(Required(input, "evaluatedAt")), lanes, (string?)input["supersededBy"]);
    }

    /// <summary>Writes a decision result (decision.schema.json).</summary>
    public static JsonObject Write(CheckpointDecisionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var lanes = new JsonArray();
        foreach (var lane in result.Lanes)
        {
            var item = new JsonObject { ["lane"] = lane.Lane, ["status"] = WireName(lane.Status), ["blocking"] = lane.Blocking };
            if (lane.Axes is { Count: > 0 } axes)
            {
                item["axes"] = new JsonArray(axes.Select(a => (JsonNode?)a).ToArray());
            }

            lanes.Add(item);
        }

        return new JsonObject
        {
            ["outcome"] = result.Outcome.ToString().ToLowerInvariant(),
            ["lanes"] = lanes,
            ["reasons"] = new JsonArray(result.Reasons.Select(r => (JsonNode?)r).ToArray()),
        };
    }

    /// <summary>A lane status's wire name.</summary>
    public static string WireName(LaneStatus status) => status switch
    {
        LaneStatus.Passed => "passed",
        LaneStatus.Failed => "failed",
        LaneStatus.Missing => "missing",
        LaneStatus.NotMeasured => "not_measured",
        LaneStatus.Incomparable => "incomparable",
        LaneStatus.Stale => "stale",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    // A status this version does not know (a later minor's) fails closed: the lane measured nothing it can read.
    private static LaneEvidenceStatus Status(string? wire) => wire switch
    {
        "passed" => LaneEvidenceStatus.Passed,
        "failed" => LaneEvidenceStatus.Failed,
        "incomparable" => LaneEvidenceStatus.Incomparable,
        null => throw new FormatException("status is required."),
        _ => LaneEvidenceStatus.NotMeasured,
    };

    private static string Required(JsonNode node, string name) =>
        (string?)node[name] is { Length: > 0 } value ? value : throw new FormatException($"{name} is required.");

}
