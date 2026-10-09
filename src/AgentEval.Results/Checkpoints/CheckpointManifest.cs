// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Nodes;
using AgentEval.Results.Runs;

namespace AgentEval.Results.Checkpoints;

/// <summary>
/// Checks a decided checkpoint manifest against itself (contracts/aef/1/spec/05-checkpoints.md, [CKP-7]): the rules
/// across its parts that a schema cannot express. The manifest must already be valid against the reader checkpoint schema.
/// </summary>
public static class CheckpointManifest
{
    // The values this version's writer schemas accept ([VER-8]: known), state by state and status by status.
    private static readonly HashSet<string> Undecided = new(StringComparer.Ordinal) { "draft", "planned", "approved_to_spend", "running", "evidence_complete" };
    private static readonly HashSet<string> States = new(Undecided, StringComparer.Ordinal) { "decided" };
    private static readonly HashSet<string> Outcomes = new(StringComparer.Ordinal) { "approved", "approved_with_exceptions", "blocked", "inconclusive", "expired" };
    private static readonly HashSet<string> LaneStatuses = new(StringComparer.Ordinal) { "passed", "failed", "missing", "not_measured", "incomparable", "stale", "waived" };
    private static readonly HashSet<string> EvidenceStatuses = new(StringComparer.Ordinal) { "passed", "failed", "not_measured", "incomparable" };

    /// <summary>
    /// The problems, in name order: <c>decision</c> (not what the recorded input gives, or the input cannot be decided),
    /// <c>evidence</c> (a lane has runs but no result in the input, a lane the input leaves out included, or a result but
    /// no runs, a result for a lane the manifest does not have included), <c>exception-evidence</c> (an exception names a
    /// run hash that is not one of its lane's runs), <c>lane-evidence</c> (a lane's evidence in the input is not the set
    /// of its runs' run hashes), <c>lanes</c> (the input does not decide exactly the manifest's lanes), <c>outcome</c>
    /// (not the decision's), <c>version</c> (the input is for another version). Or only <c>unverifiable</c>: the manifest
    /// declares a later minor ([VER-6]) and holds a state, an outcome or a lane status this version does not know, whatever
    /// else it holds; or it is checked as decided (<see cref="IsDecided"/>) and records an outcome other than
    /// <c>aborted</c> without a decision, or a decision without its input: nothing can be recomputed, and that is not
    /// tampering. Empty for a manifest not checked as decided, or aborted.
    /// </summary>
    /// <remarks>
    /// [CKP-7] (round 6): a manifest that declares this version, or an earlier one, is checked as usual whatever it holds.
    /// An unknown lane status in its input reads as [DEC-2] says (<c>not_measured</c>); an unknown outcome, or lane status
    /// in its decision, is whatever the comparison finds (<c>outcome</c>, <c>decision</c>); and one in a state this version
    /// does not know that records an outcome or a decision is checked as decided, so a 1.0 manifest cannot escape the
    /// checks with a state nobody defined. Such a manifest may lack what the schema requires only of <c>decided</c> with
    /// a known outcome: a decision, or the decision's input; it is then <c>unverifiable</c> (Q4-39 R6N-1, R6N-2, ruled 10-09).
    /// </remarks>
    public static IReadOnlyList<string> Verify(JsonNode manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var state = AefNode.String(manifest["state"]);
        var outcome = AefNode.String(manifest["outcome"]);
        var decision = manifest["decision"] as JsonObject;
        var input = manifest["decisionInput"] as JsonObject;

        // A later minor ([VER-6]) that holds a state, an outcome or a lane status this version does not know: a reader
        // cannot recompute it, whatever else it holds.
        if (AefVersion.DeclaresLaterMinor(manifest["schemaVersion"]) && HoldsUnknown(state, outcome, decision, input))
        {
            return ["unverifiable"];
        }

        // Not checked as decided (not yet decided), or abandoned: nothing to recompute.
        if (!IsDecided(manifest))
        {
            return [];
        }

        // Checked as decided, with an outcome but no decision, or a decision without its input: nothing can be recomputed.
        if (decision is null || input is null)
        {
            return ["unverifiable"];
        }

        var problems = new SortedSet<string>(StringComparer.Ordinal);
        if (outcome != AefNode.String(decision["outcome"])) problems.Add("outcome");
        if (AefNode.String(input["subjectVersion"]) != AefNode.String(AefNode.At(manifest, "subject", "version"))) problems.Add("version");

        var lanes = AefNode.Objects(manifest["lanes"]).ToList();
        var inputLanes = AefNode.Objects(input["lanes"]).ToList();
        static string Key(JsonObject lane) =>
            $"{AefNode.String(lane["lane"])}|{AefNode.IsTrue(lane["blocking"])}|{AefNode.String(lane["freshness"])}";
        if (!lanes.Select(Key).SequenceEqual(inputLanes.Select(Key), StringComparer.Ordinal))
        {
            problems.Add("lanes");
        }

        // The run hashes of the runs a lane of the manifest names: the evidence its result and its exceptions may name.
        HashSet<string> RunsOf(string? name) => lanes
            .Where(l => AefNode.String(l["lane"]) == name)
            .SelectMany(l => AefNode.Objects(l["runs"]))
            .Select(r => AefNode.String(r["runHash"]) ?? "")
            .ToHashSet(StringComparer.Ordinal);

        foreach (var lane in lanes)
        {
            // "A lane has runs but no result in the input": a lane the input leaves out has no result there either.
            var inputLane = inputLanes.FirstOrDefault(l => AefNode.String(l["lane"]) == AefNode.String(lane["lane"]));
            if (AefNode.Items(lane["runs"]).Any() != (inputLane?["result"] is JsonObject))
            {
                problems.Add("evidence");
            }

            // A lane's evidence in the input is the set of its runs' run hashes; none is the empty set.
            if (inputLane is not null && !RunsOf(AefNode.String(lane["lane"])).SetEquals(CheckpointDecisionJson.RunHashes(inputLane["evidence"]) ?? []))
            {
                problems.Add("lane-evidence");
            }
        }

        // "Or a result but no runs": a result for a lane the manifest does not have, which has no runs.
        if (inputLanes.Any(l => l["result"] is JsonObject && !lanes.Any(m => AefNode.String(m["lane"]) == AefNode.String(l["lane"]))))
        {
            problems.Add("evidence");
        }

        // An exception names only run hashes of its lane's runs: it accepts evidence this checkpoint holds.
        foreach (var grant in AefNode.Objects(input["exceptions"]))
        {
            if (!AefNode.Strings(grant["evidence"]).All(RunsOf(AefNode.String(grant["lane"])).Contains))
            {
                problems.Add("exception-evidence");
            }
        }

        try
        {
            // Only the fields this version defines are compared: a field a later minor adds to the output is not a
            // difference. An input lane status this version does not know reads as not_measured ([DEC-2]); a recorded
            // outcome or lane status it does not know is not what the function gives.
            var recomputed = CheckpointDecisionJson.Write(CheckpointDecision.Decide(CheckpointDecisionJson.ReadInput(input)));
            if (!JsonNode.DeepEquals(Known(recomputed), Known(decision))) problems.Add("decision");
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            problems.Add("decision");   // the recorded input cannot be decided at all
        }

        return [.. problems];
    }

    /// <summary>
    /// Whether a manifest is <b>checked as decided</b> ([CKP-7]; [CKP-8] compares its lanes with its recorded input) and
    /// not abandoned: its state is <c>decided</c>, or, in a manifest that declares this version or an earlier one, its
    /// state is one this version does not know and it records an outcome or a decision (a 1.0 manifest cannot escape the
    /// checks with a state nobody defined; Q4-39 R6N-2, R6N-3, ruled 10-09); and its outcome is not <c>aborted</c> (an
    /// abandoned checkpoint has nothing to recompute). A manifest in a state before the decision is not checked as decided.
    /// </summary>
    public static bool IsDecided(JsonNode manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var state = AefNode.String(manifest["state"]);
        var outcome = AefNode.String(manifest["outcome"]);
        if (outcome == "aborted" || (state is not null && Undecided.Contains(state)))
        {
            return false;
        }

        return state == "decided"
               || (!AefVersion.DeclaresLaterMinor(manifest["schemaVersion"]) && (outcome is not null || manifest["decision"] is JsonObject));
    }

    // [CKP-7]: a state, an outcome (the manifest's or its decision's) or a lane status (its decision's, or a result's in
    // its input) that this version's writer schemas do not accept ([VER-8]).
    private static bool HoldsUnknown(string? state, string? outcome, JsonObject? decision, JsonObject? input) =>
        state is null || !States.Contains(state)
        || (outcome is not null && outcome != "aborted" && !Outcomes.Contains(outcome))
        || (decision is not null && !Outcomes.Contains(AefNode.String(decision["outcome"]) ?? ""))
        || AefNode.Objects(decision?["lanes"]).Any(l => !LaneStatuses.Contains(AefNode.String(l["status"]) ?? ""))
        || AefNode.Objects(input?["lanes"]).Any(l => l["result"] is JsonObject r && !EvidenceStatuses.Contains(AefNode.String(r["status"]) ?? ""));

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
