// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;

namespace AgentEval.Results.Checkpoints;

/// <summary>
/// Checks a decided checkpoint manifest against itself (contracts/aef/v2/README.md, "Checkpoints"): the rules across
/// its parts that a schema cannot express. The manifest must already be valid against checkpoint.schema.json.
/// </summary>
public static class CheckpointManifest
{
    /// <summary>
    /// The problems, in name order: <c>outcome</c> (not the decision's), <c>decision</c> (not what the recorded input
    /// gives), <c>lanes</c> (the input does not decide exactly the manifest's lanes), <c>version</c> (the input is for
    /// another version), <c>evidence</c> (a lane has runs but no result, or a result but no runs). Empty for a manifest
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

        var problems = new SortedSet<string>(StringComparer.Ordinal);
        var input = manifest["decisionInput"]!;
        var decision = manifest["decision"]!;
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
            var recomputed = CheckpointDecisionJson.Write(CheckpointDecision.Decide(CheckpointDecisionJson.ReadInput(input)));
            if (!JsonNode.DeepEquals(recomputed, decision)) problems.Add("decision");
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            problems.Add("decision");
        }

        return [.. problems];
    }
}
