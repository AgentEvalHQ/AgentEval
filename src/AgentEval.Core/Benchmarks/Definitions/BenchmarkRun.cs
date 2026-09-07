// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Benchmarks;

/// <summary>
/// One arm's pass over one definition: what was run, by whom, and every check's answer.
/// </summary>
/// <remarks>
/// <para>
/// A run carries the <see cref="Definition"/> it was produced against rather than a reference to one
/// that may since have changed. Scoring reads the definition back off the run, so a comparison
/// between two runs of different definitions is visible in the data rather than assumed away.
/// </para>
/// <para>
/// <b>One run is one REP of one arm.</b> Several runs of the same <see cref="ArmId"/> are reps, and
/// reps collapse per case BEFORE anything is compared — the unit of analysis is the case, never the
/// rep. Nothing in this record does that collapsing; it holds what happened.
/// </para>
/// </remarks>
/// <param name="RunId">Stable identity for this pass.</param>
/// <param name="ArmId">The arm that produced it.</param>
/// <param name="Definition">The definition this pass was run against.</param>
/// <param name="Observations">Every check's answer on every case.</param>
public sealed record BenchmarkRun(
    string RunId,
    string ArmId,
    BenchmarkDefinition Definition,
    IReadOnlyList<CheckObservation> Observations);
