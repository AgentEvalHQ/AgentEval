// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam;

/// <summary>
/// Result of executing a single attack probe.
/// </summary>
public record ProbeResult
{
    /// <summary>The probe ID that was executed.</summary>
    public required string ProbeId { get; init; }

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
}
