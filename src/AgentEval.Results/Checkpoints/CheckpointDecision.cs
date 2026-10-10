// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

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

    /// <summary>The rule did not hold, and an exception in force at the evaluation time accepts the failure. Only a failure is ever waived.</summary>
    Waived,
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

    /// <summary>Every lane passed or was waived by an exception, and at least one was waived (an advisory lane may have failed, and says so).</summary>
    ApprovedWithExceptions,
}

/// <summary>
/// A lane's evidence: its rule's result, the version it was produced for, and when the oldest run the result relied on
/// closed (freshness is measured from it).
/// </summary>
public sealed record LaneEvidence(
    LaneEvidenceStatus Status, string SubjectVersion, AefTime OldestClosedAt, IReadOnlyList<string>? Axes = null);

/// <summary>
/// One lane of the decision's input. <paramref name="Freshness"/> is an AEF duration of days, hours and minutes ([ENC-9]);
/// <paramref name="Evidence"/> is the run hash of each of the lane's runs (required with a result; none is the empty set).
/// </summary>
public sealed record LaneInput(
    string Lane, bool Blocking, LaneEvidence? Result, string? Freshness = null, IReadOnlyList<string>? Evidence = null);

/// <summary>
/// An identity and the assurance its writer claims for it (self-attested, signed or authenticated). A claim: a reader
/// shows the assurance only as far as it verified it.
/// </summary>
public sealed record TrustedIdentity(string Identity, string Assurance);

/// <summary>
/// An exception: a person's decision to accept the failure of named, sealed evidence (<c>Evidence</c>: run hashes) from
/// <c>At</c> until <c>Expires</c>. It waives a failed lane whose evidence is the same set of run hashes, while in force
/// (At &lt;= t &lt; Expires); never missing, stale, not-measured or incomparable evidence, and never a re-run's.
/// <c>Requirement</c>, <c>Reason</c> and <c>By</c> are for display and take no part in the decision; <c>By</c> is a
/// claim, attributable only through the checkpoint's signature.
/// </summary>
public sealed record ExceptionGrant(
    string Lane, IReadOnlyList<string> Evidence, string Reason, TrustedIdentity By, AefTime At, AefTime Expires,
    string? Requirement = null)
{
    /// <summary>Granted at or before <paramref name="time"/>, and expiring after it.</summary>
    public bool InForceAt(AefTime time) => At <= time && time < Expires;

    /// <summary>For exactly this evidence: the same set of run hashes, compared byte for byte.</summary>
    public bool IsFor(IReadOnlyList<string>? evidence) =>
        Evidence.ToHashSet(StringComparer.Ordinal).SetEquals(evidence ?? []);
}

/// <summary>The decision function's input: the checkpoint's exact version, the evaluation time, its lanes, and any exceptions.</summary>
public sealed record CheckpointDecisionInput(
    string SubjectVersion, AefTime EvaluatedAt, IReadOnlyList<LaneInput> Lanes, string? SupersededBy = null,
    IReadOnlyList<ExceptionGrant>? Exceptions = null);

/// <summary>One lane in the decision.</summary>
public sealed record LaneDecision(string Lane, LaneStatus Status, bool Blocking, IReadOnlyList<string>? Axes = null);

/// <summary>The outcome, each lane's status, and the reasons as codes (the same in every implementation).</summary>
public sealed record CheckpointDecisionResult(CheckpointOutcome Outcome, IReadOnlyList<LaneDecision> Lanes, IReadOnlyList<string> Reasons);

/// <summary>
/// The AEF 1.0 checkpoint decision function (contracts/aef/1/README.md, "The decision function"): pure, with no I/O
/// and no clock (the evaluation time is an input), so anyone can recompute why a release was blocked. Versions compare
/// byte for byte; times at their full precision. Its vectors are contracts/aef/1/conformance/decision-vectors/.
/// </summary>
public static class CheckpointDecision
{
    /// <summary>Decides a checkpoint.</summary>
    /// <exception cref="ArgumentException">No lane, a lane listed twice, an exception for a lane the input does not
    /// have, one that names no run hash, or one that expires at or before it is granted.</exception>
    /// <exception cref="FormatException">A lane's freshness is not a duration ([ENC-9]).</exception>
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

        var names = input.Lanes.Select(l => l.Lane).ToHashSet(StringComparer.Ordinal);
        foreach (var grant in input.Exceptions ?? [])
        {
            if (!names.Contains(grant.Lane))
            {
                throw new ArgumentException($"An exception names lane '{grant.Lane}', which is not a lane of the input.", nameof(input));
            }

            if (grant.Evidence is not { Count: > 0 })
            {
                throw new ArgumentException($"An exception for lane '{grant.Lane}' names no evidence: it would accept any failure.", nameof(input));
            }

            if (grant.Expires <= grant.At)
            {
                throw new ArgumentException($"An exception for lane '{grant.Lane}' expires at or before it is granted: it is never in force.", nameof(input));
            }
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

            // Only a failure is ever waived, by an exception for exactly this evidence (the same set of run hashes) in force
            // at the evaluation time: never missing or unusable evidence, never a re-run's.
            var unapplied = new List<string>(2);   // why none applied, in [DEC-4] order
            if (status is LaneStatus.Failed
                && input.Exceptions?.Where(e => string.Equals(e.Lane, lane.Lane, StringComparison.Ordinal)).ToList() is { Count: > 0 } grants)
            {
                if (grants.Any(e => e.IsFor(lane.Evidence) && e.InForceAt(input.EvaluatedAt)))
                {
                    status = LaneStatus.Waived;
                }
                else
                {
                    if (grants.Any(e => e.IsFor(lane.Evidence))) unapplied.Add("exception-expired");
                    if (grants.Any(e => !e.IsFor(lane.Evidence))) unapplied.Add("exception-other-evidence");
                }
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
                    LaneStatus.Waived => "waived",
                    _ => "stale",
                };
                reasons.Add($"{code}:{lane.Lane}");
            }

            reasons.AddRange(unapplied.Select(why => $"{why}:{lane.Lane}"));
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
            : lanes.Any(l => l.Status is LaneStatus.Waived) ? CheckpointOutcome.ApprovedWithExceptions
            : CheckpointOutcome.Approved;
        reasons.Add($"outcome:{CheckpointDecisionJson.WireName(outcome)}");
        return new CheckpointDecisionResult(outcome, lanes, reasons);
    }

    /// <summary>A freshness in seconds: an AEF duration of days, hours and minutes ([ENC-9], <see cref="AefDuration"/>).</summary>
    /// <exception cref="FormatException">Anything else.</exception>
    public static long DurationSeconds(string text) => AefDuration.Seconds(text);
}
