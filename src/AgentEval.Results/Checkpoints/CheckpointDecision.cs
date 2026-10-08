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

    /// <summary>The evidence measured nothing the rule reads.</summary>
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

    /// <summary>No evidence for this exact version.</summary>
    Missing,

    /// <summary>The evidence measured nothing the rule reads.</summary>
    NotMeasured,

    /// <summary>A comparison's runs differ on a required axis.</summary>
    Incomparable,

    /// <summary>Older than the lane's freshness at the evaluation time.</summary>
    Stale,
}

/// <summary>A checkpoint's outcome. Missing evidence is never converted into a pass; nothing is averaged.</summary>
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

/// <summary>A lane's evidence: its rule's result, the version it was produced for, and when its newest run closed.</summary>
public sealed record LaneEvidence(
    LaneEvidenceStatus Status, string SubjectVersion, DateTimeOffset ClosedAt, IReadOnlyList<string>? Axes = null);

/// <summary>One lane of the decision's input. <paramref name="Freshness"/> is an ISO 8601 duration in days and hours.</summary>
public sealed record LaneInput(string Lane, bool Blocking, LaneEvidence? Result, string? Freshness = null);

/// <summary>The decision function's input: the checkpoint's exact version, the evaluation time, and its lanes.</summary>
public sealed record CheckpointDecisionInput(
    string SubjectVersion, DateTimeOffset EvaluatedAt, IReadOnlyList<LaneInput> Lanes, string? SupersededBy = null);

/// <summary>One lane in the decision.</summary>
public sealed record LaneDecision(string Lane, LaneStatus Status, bool Blocking, IReadOnlyList<string>? Axes = null);

/// <summary>The outcome, each lane's status, and the reasons as codes (the same in every implementation).</summary>
public sealed record CheckpointDecisionResult(CheckpointOutcome Outcome, IReadOnlyList<LaneDecision> Lanes, IReadOnlyList<string> Reasons);

/// <summary>
/// The AEF v2 checkpoint decision function (contracts/aef/v2/README.md, "The decision function"): pure, with no I/O
/// and no clock (the evaluation time is an input), so anyone can recompute why a release was blocked. Its vectors are
/// contracts/aef/v2/conformance/decision-vectors/.
/// </summary>
public static class CheckpointDecision
{
    private static readonly Regex Duration = new(@"^P(?=\d|T\d)(?:(\d+)D)?(?:T(\d+)H)?$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Decides a checkpoint.</summary>
    /// <exception cref="FormatException">A lane's freshness is not a duration in days and hours.</exception>
    public static CheckpointDecisionResult Decide(CheckpointDecisionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

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
            else if (lane.Freshness is { } freshness && evidence.ClosedAt + ParseDuration(freshness) < input.EvaluatedAt)
            {
                status = LaneStatus.Stale;
            }
            else
            {
                status = evidence.Status switch
                {
                    LaneEvidenceStatus.Passed => LaneStatus.Passed,
                    LaneEvidenceStatus.Failed => LaneStatus.Failed,
                    LaneEvidenceStatus.NotMeasured => LaneStatus.NotMeasured,
                    _ => LaneStatus.Incomparable,
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

    /// <summary>An ISO 8601 duration of days and hours (P14D, PT36H, P1DT12H).</summary>
    /// <exception cref="FormatException">Anything else.</exception>
    public static TimeSpan ParseDuration(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = Duration.Match(text);
        if (!match.Success)
        {
            throw new FormatException($"'{text}' is not a duration in days and hours (P14D, PT36H, P1DT12H).");
        }

        var days = match.Groups[1].Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var hours = match.Groups[2].Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        return TimeSpan.FromDays(days) + TimeSpan.FromHours(hours);
    }
}
