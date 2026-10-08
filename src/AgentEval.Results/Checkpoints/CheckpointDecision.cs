// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentEval.Results.Checkpoints;

/// <summary>What a lane's rule gave on its evidence (computed from the lane's runs, outside the decision function).</summary>
public enum LaneEvidenceStatus
{
    /// <summary>The rule held.</summary>
    Passed,

    /// <summary>The rule did not hold.</summary>
    Failed,

    /// <summary>The evidence measured nothing the rule reads (also: a status this version does not know, failing closed).</summary>
    NotMeasured,

    /// <summary>A comparison's runs differ on a required comparability axis.</summary>
    Incomparable,
}

/// <summary>A lane's status in a decision.</summary>
public enum LaneStatus
{
    /// <summary>The rule held on fresh evidence for this version.</summary>
    Passed,

    /// <summary>The rule did not hold.</summary>
    Failed,

    /// <summary>No evidence for this exact version at the evaluation time.</summary>
    Missing,

    /// <summary>The evidence measured nothing the rule reads.</summary>
    NotMeasured,

    /// <summary>A comparison's runs differ on a required axis.</summary>
    Incomparable,

    /// <summary>The oldest evidence the lane relied on is older than its freshness at the evaluation time.</summary>
    Stale,
}

/// <summary>A checkpoint's outcome from the decision function. Missing evidence is never converted into a pass; nothing is averaged.</summary>
public enum CheckpointOutcome
{
    /// <summary>Every lane passed (an advisory lane may have failed, and says so).</summary>
    Approved,

    /// <summary>A blocking lane failed.</summary>
    Blocked,

    /// <summary>A lane is missing, measured nothing, or is incomparable.</summary>
    Inconclusive,

    /// <summary>Evidence aged past its freshness, or a newer version superseded this one.</summary>
    Expired,
}

/// <summary>
/// A lane's evidence: its rule's result, the version it was produced for, and when the oldest run the result relied on
/// closed (freshness is measured from it).
/// </summary>
public sealed record LaneEvidence(
    LaneEvidenceStatus Status, string SubjectVersion, AefTime OldestClosedAt, IReadOnlyList<string>? Axes = null);

/// <summary>One lane of the decision's input. <paramref name="Freshness"/> is an ISO 8601 duration in days and hours.</summary>
public sealed record LaneInput(string Lane, bool Blocking, LaneEvidence? Result, string? Freshness = null);

/// <summary>The decision function's input: the checkpoint's exact version, the evaluation time, and its lanes.</summary>
public sealed record CheckpointDecisionInput(
    string SubjectVersion, AefTime EvaluatedAt, IReadOnlyList<LaneInput> Lanes, string? SupersededBy = null);

/// <summary>One lane in the decision.</summary>
public sealed record LaneDecision(string Lane, LaneStatus Status, bool Blocking, IReadOnlyList<string>? Axes = null);

/// <summary>The outcome, each lane's status, and the reasons as codes (the same in every implementation).</summary>
public sealed record CheckpointDecisionResult(CheckpointOutcome Outcome, IReadOnlyList<LaneDecision> Lanes, IReadOnlyList<string> Reasons);

/// <summary>
/// The AEF v2 checkpoint decision function (contracts/aef/v2/README.md, "The decision function"): pure, with no I/O
/// and no clock (the evaluation time is an input), so anyone can recompute why a release was blocked. Versions compare
/// byte for byte; times at their full precision. Its vectors are contracts/aef/v2/conformance/decision-vectors/.
/// </summary>
public static class CheckpointDecision
{
    private static readonly Regex Duration = new(
        "^P(?=[0-9]|T[0-9])(?:([0-9]{1,5})D)?(?:T([0-9]{1,5})H)?\\z", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Decides a checkpoint.</summary>
    /// <exception cref="ArgumentException">No lane, or a lane listed twice.</exception>
    /// <exception cref="FormatException">A lane's freshness is not a duration in days and hours.</exception>
    public static CheckpointDecisionResult Decide(CheckpointDecisionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Lanes.Count == 0)
        {
            throw new ArgumentException("A checkpoint has at least one lane: deciding none would approve nothing.", nameof(input));
        }

        if (input.Lanes.GroupBy(l => l.Lane, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            throw new ArgumentException($"Lane '{twice.Key}' is listed twice.", nameof(input));
        }

        var lanes = new List<LaneDecision>(input.Lanes.Count);
        var reasons = new List<string>();
        foreach (var lane in input.Lanes)
        {
            string? code = null;
            LaneStatus status;
            if (lane.Result is not { } evidence)
            {
                status = LaneStatus.Missing;
            }
            else if (!string.Equals(evidence.SubjectVersion, input.SubjectVersion, StringComparison.Ordinal))
            {
                (status, code) = (LaneStatus.Missing, "wrong-version");
            }
            else if (evidence.OldestClosedAt > input.EvaluatedAt)
            {
                (status, code) = (LaneStatus.Missing, "future-evidence");
            }
            else if (lane.Freshness is { } freshness && evidence.OldestClosedAt.AddSeconds(DurationSeconds(freshness)) < input.EvaluatedAt)
            {
                status = LaneStatus.Stale;
            }
            else
            {
                status = evidence.Status switch
                {
                    LaneEvidenceStatus.Passed => LaneStatus.Passed,
                    LaneEvidenceStatus.Failed => LaneStatus.Failed,
                    LaneEvidenceStatus.Incomparable => LaneStatus.Incomparable,
                    _ => LaneStatus.NotMeasured,
                };
            }

            lanes.Add(new LaneDecision(lane.Lane, status, lane.Blocking,
                status is LaneStatus.Incomparable && lane.Result?.Axes is { Count: > 0 } axes ? axes : null));

            if (status is not LaneStatus.Passed)
            {
                code ??= status switch
                {
                    LaneStatus.Failed => lane.Blocking ? "failed" : "advisory-failed",
                    LaneStatus.Missing => "missing",
                    LaneStatus.NotMeasured => "not-measured",
                    LaneStatus.Incomparable => "incomparable",
                    _ => "stale",
                };
                reasons.Add($"{code}:{lane.Lane}");
            }
        }

        var superseded = input.SupersededBy is { } newer && !string.Equals(newer, input.SubjectVersion, StringComparison.Ordinal);
        if (superseded)
        {
            reasons.Add($"superseded:{input.SupersededBy}");
        }

        var outcome =
            superseded || lanes.Any(l => l.Status is LaneStatus.Stale) ? CheckpointOutcome.Expired
            : lanes.Any(l => l is { Status: LaneStatus.Failed, Blocking: true }) ? CheckpointOutcome.Blocked
            : lanes.Any(l => l.Status is LaneStatus.Missing or LaneStatus.NotMeasured or LaneStatus.Incomparable) ? CheckpointOutcome.Inconclusive
            : CheckpointOutcome.Approved;
        reasons.Add($"outcome:{outcome.ToString().ToLowerInvariant()}");
        return new CheckpointDecisionResult(outcome, lanes, reasons);
    }

    /// <summary>An ISO 8601 duration of days and hours (P14D, PT36H, P1DT12H), each at most five digits, in seconds.</summary>
    /// <exception cref="FormatException">Anything else.</exception>
    public static long DurationSeconds(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = Duration.Match(text);
        if (!match.Success)
        {
            throw new FormatException($"'{text}' is not a duration in days and hours (P14D, PT36H, P1DT12H), each at most five digits.");
        }

        long days = match.Groups[1].Success ? long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        long hours = match.Groups[2].Success ? long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        return (days * 86_400) + (hours * 3_600);
    }
}
