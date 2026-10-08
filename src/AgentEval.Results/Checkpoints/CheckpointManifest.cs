// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace AgentEval.Results.Checkpoints;

/// <summary>
/// Checks a decided checkpoint manifest against itself (contracts/aef/v2/README.md, "Checkpoints"): the rules across
/// its parts that a schema cannot express. The manifest must already be valid against the reader checkpoint schema.
/// </summary>
public static class CheckpointManifest
{
    private static readonly HashSet<string> Outcomes = new(StringComparer.Ordinal) { "approved", "blocked", "inconclusive", "expired" };
    private static readonly HashSet<string> LaneStatuses = new(StringComparer.Ordinal) { "passed", "failed", "missing", "not_measured", "incomparable", "stale" };
    private static readonly HashSet<string> EvidenceStatuses = new(StringComparer.Ordinal) { "passed", "failed", "not_measured", "incomparable" };

    /// <summary>
    /// The problems, in name order: <c>decision</c> (not what the recorded input gives), <c>evidence</c> (a lane has
    /// runs but no result, or a result but no runs), <c>lanes</c> (the input does not decide exactly the manifest's
    /// lanes), <c>outcome</c> (not the decision's), <c>version</c> (the input is for another version). Or only
    /// <c>unverifiable</c>: the manifest uses an outcome or status this version does not know, or lacks the decision a
    /// newer version may make optional; a reader cannot recompute it, and that is not tampering. Empty for a manifest
    /// not yet decided, or aborted.
    /// </summary>
    public static IReadOnlyList<string> Verify(JsonNode manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var state = (string?)manifest["state"];
        var outcome = (string?)manifest["outcome"];
        if (state is not ("decided" or "sealed") || outcome is null or "aborted")
        {
            return [];
        }

        var input = manifest["decisionInput"];
        var decision = manifest["decision"];
        if (!Outcomes.Contains(outcome) || input is null || decision is null
            || !Outcomes.Contains((string?)decision["outcome"] ?? "")
            || (decision["lanes"]?.AsArray() ?? []).Any(l => !LaneStatuses.Contains((string?)l?["status"] ?? ""))
            || (input["lanes"]?.AsArray() ?? []).Any(l => l?["result"] is { } r && !EvidenceStatuses.Contains((string?)r["status"] ?? "")))
        {
            return ["unverifiable"];
        }

        var problems = new SortedSet<string>(StringComparer.Ordinal);
        if (outcome != (string?)decision["outcome"]) problems.Add("outcome");
        if ((string?)input["subjectVersion"] != (string?)manifest["subject"]?["version"]) problems.Add("version");

        var lanes = manifest["lanes"]!.AsArray();
        var inputLanes = input["lanes"]!.AsArray();
        static string Key(JsonNode? lane) => $"{(string?)lane!["lane"]}|{(bool?)lane["blocking"]}|{(string?)lane["freshness"]}";
        if (!lanes.Select(Key).SequenceEqual(inputLanes.Select(Key), StringComparer.Ordinal))
        {
            problems.Add("lanes");
        }

        foreach (var lane in lanes)
        {
            var inputLane = inputLanes.FirstOrDefault(l => (string?)l!["lane"] == (string?)lane!["lane"]);
            if (inputLane is not null && (lane!["runs"]!.AsArray().Count == 0) != (inputLane["result"] is null))
            {
                problems.Add("evidence");
            }
        }

        try
        {
            // Only the fields this version defines are compared: a field a later minor adds to the output is not a difference.
            var recomputed = CheckpointDecisionJson.Write(CheckpointDecision.Decide(CheckpointDecisionJson.ReadInput(input)));
            if (!JsonNode.DeepEquals(Known(recomputed), Known(decision))) problems.Add("decision");
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            problems.Add("decision");   // the recorded input cannot be decided at all
        }

        return [.. problems];
    }

    private static JsonObject Known(JsonNode decision) => new()
    {
        ["outcome"] = decision["outcome"]?.DeepClone(),
        ["reasons"] = decision["reasons"]?.DeepClone(),
        ["lanes"] = new JsonArray((decision["lanes"]?.AsArray() ?? []).Select(l =>
        {
            var lane = new JsonObject { ["lane"] = l!["lane"]?.DeepClone(), ["status"] = l["status"]?.DeepClone(), ["blocking"] = l["blocking"]?.DeepClone() };
            if (l["axes"] is JsonArray { Count: > 0 } axes) lane["axes"] = axes.DeepClone();
            return (JsonNode?)lane;
        }).ToArray()),
    };
}
