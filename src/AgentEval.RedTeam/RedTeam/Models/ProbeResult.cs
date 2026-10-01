// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Models;

namespace AgentEval.RedTeam;

/// <summary>
/// Result of executing a single attack probe.
/// </summary>
public record ProbeResult
{
    /// <summary>The probe ID that was executed.</summary>
    public required string ProbeId { get; init; }

    /// <summary>
    /// Whether the probe was an attack (<see cref="RequestPermissibility.NotPermissible"/>, the default) or a benign
    /// control (<see cref="RequestPermissibility.Permissible"/>).
    /// </summary>
    /// <remarks>
    /// For a benign control the outcome reads inverted: <see cref="EvaluationOutcome.Resisted"/> means the agent
    /// refused a legitimate request (over-refusal), and <see cref="EvaluationOutcome.Succeeded"/> means it answered.
    /// The runner stamps this on every result from the probe's own marker, on every exit path.
    /// </remarks>
    public RequestPermissibility Expectation { get; init; } = RequestPermissibility.NotPermissible;

    /// <summary>The prompt sent to the agent.</summary>
    public required string Prompt { get; init; }

    /// <summary>The agent's response.</summary>
    public required string Response { get; init; }

    /// <summary>The outcome of the evaluation.</summary>
    public required EvaluationOutcome Outcome { get; init; }

    /// <summary>Explanation of why the probe passed or failed.</summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Optional LLM-generated rationale narrating WHY this verdict + evidence fidelity (the <c>--explain</c> /
    /// <see cref="ScanOptions.ExplainFindings"/> feature). Populated only for Succeeded/Inconclusive findings when a
    /// judge is configured; <c>null</c> otherwise. Best-effort — an explain failure never affects the verdict.
    /// Suppressed (<c>null</c>) when <see cref="ScanOptions.IncludeEvidence"/> is false, since the rationale is
    /// derived from the raw response and would otherwise leak the very content redaction suppresses (H1).
    /// </summary>
    public string? Rationale { get; init; }

    /// <summary>
    /// Technique category (e.g., "delimiter_injection", "roleplay").
    /// Copied from the probe for convenience.
    /// </summary>
    public string? Technique { get; init; }

    /// <summary>
    /// Difficulty level of this probe.
    /// Copied from the probe for convenience.
    /// </summary>
    public Difficulty Difficulty { get; init; } = Difficulty.Moderate;

    /// <summary>Matched tokens or patterns if applicable.</summary>
    public IReadOnlyList<string>? MatchedItems { get; init; }

    /// <summary>Time taken to execute this probe.</summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>Error message if the probe execution failed.</summary>
    public string? Error { get; init; }

    /// <summary>Classifies an inconclusive probe (recoverable timeout vs ambiguous evaluation vs transport fault) (RC-6).</summary>
    public ProbeErrorKind ErrorKind { get; init; } = ProbeErrorKind.None;

    /// <summary>Whether this probe had an error.</summary>
    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>True when this inconclusive probe is a timeout specifically (subset of <see cref="HasError"/>).</summary>
    public bool IsTimeout => ErrorKind == ProbeErrorKind.Timeout;

    /// <summary>True when the probe is inconclusive with no execution error — the evaluator genuinely could not decide.</summary>
    public bool IsAmbiguous => Outcome == EvaluationOutcome.Inconclusive && ErrorKind == ProbeErrorKind.None;

    /// <summary>Severity if this probe found a vulnerability.</summary>
    public Severity Severity { get; init; } = Severity.Medium;

    /// <summary>
    /// RC-1: the fidelity of the evidence behind <see cref="Outcome"/>. Defaults to
    /// <see cref="EvidenceFidelity.Verbal"/> so existing producers keep their prior semantics.
    /// Tool-aware evaluators stamp <see cref="EvidenceFidelity.Behavioral"/>.
    /// </summary>
    public EvidenceFidelity Fidelity { get; init; } = EvidenceFidelity.Verbal;

    /// <summary>
    /// The injection delivery surface this result came from (Wave B, Pillar 4), carried over from
    /// <see cref="AttackProbe.Surface"/>. <c>null</c> when the probe had no surface. Lets reports break results
    /// down <c>by_surface</c> and keep an inlined-proxy result distinct from a real-boundary one.
    /// </summary>
    public InjectionSurface? Surface { get; init; }

    /// <summary>
    /// For a folded multi-turn probe (Wave C, Pillar 2): how faithfully the conversation was carried — Native (the
    /// SUT held a real session) vs Flattened (a one-shot agent driven by a re-sent transcript). <c>null</c> for
    /// single-turn probes. Keeps a flattened-conversation pass honestly distinct from a native one.
    /// </summary>
    public ConversationFidelity? ConversationFidelity { get; init; }

    /// <summary>
    /// L10: true when an attacker LLM (PAIR / TAP / attacker-driven Crescendo) generated the conversation — the run is
    /// non-deterministic. Always false for single-turn / scripted probes. Jun14-L4: currently surfaced only as the
    /// human-readable <c>[ATTACKER-DRIVEN]</c> marker appended to <see cref="Reason"/> and (ADR-021 §5) the
    /// null-omittable <c>attacker_driven</c> field in the JSON/SARIF reports. It is RESERVED for a future
    /// baseline/regression-gate consumer (no comparer reads it yet).
    /// </summary>
    public bool AttackerDriven { get; init; }

    /// <summary>
    /// For a folded linear multi-turn conversation: the agent turns it ran. <see langword="null"/> for a single-turn
    /// probe and for a tree search, whose nodes are separate single-turn calls (see <see cref="NodesExplored"/>).
    /// </summary>
    public int? TurnsUsed { get; init; }

    /// <summary>
    /// For a folded linear multi-turn conversation: the 1-based turn whose verdict the fold reports as its evidence
    /// (the first turn that succeeded; otherwise the highest-fidelity conclusive turn). <see langword="null"/> when no
    /// turn was conclusive, and for single-turn probes and tree searches.
    /// </summary>
    public int? DecidingTurn { get; init; }

    /// <summary>
    /// For a tree search (TAP): the nodes explored. Each node is an independent single-turn call to the agent, so
    /// this is a count of attempts, not the length of a conversation.
    /// </summary>
    public int? NodesExplored { get; init; }

    /// <summary>
    /// ADR-021 (§5): grading provenance for a judge-primary verdict — which grader shipped, and the
    /// keyword-vs-judge disagreement. <c>null</c> on every non-judge-primary path (the default), so a
    /// fallback run is byte-identical. Lifted from the decorator's <c>grader_provenance</c> metadata by
    /// <see cref="GradingMetadata.ProvenanceOf"/> at the conclusive construction sites.
    /// </summary>
    public GraderProvenance? Grading { get; init; }

    /// <summary>True when a judge-primary verdict disagreed with the advisory keyword oracle (§5).
    /// Computed from <see cref="Grading"/>; <c>false</c> when no judge ran.</summary>
    public bool GraderDisagreed => Grading is { } g && g.Disagreed;
}
