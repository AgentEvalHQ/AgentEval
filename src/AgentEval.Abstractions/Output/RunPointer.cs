// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

/// <summary>
/// Lightweight pointer used in run listings and the runs index.
/// </summary>
/// <remarks>
/// <para>
/// The first four fields are the v1.0 contract; the remaining fields are additive
/// extensions added in v1.7 to support Mission Control's recent-runs UI without
/// the N+1 follow-up <c>GET /runs/{id}/summary</c> round-trip. They are nullable
/// so existing producers / consumers compile unchanged; old <c>history.jsonl</c>
/// entries written before v1.7 round-trip with the new fields as null.
/// </para>
/// </remarks>
public sealed record RunPointer(
    string RunId,
    string SubjectName,
    string Verdict,
    DateTimeOffset Timestamp,
    SubjectKind? Kind = null,
    double? Score = null,
    long? DurationMs = null,
    double? EstimatedCost = null);
