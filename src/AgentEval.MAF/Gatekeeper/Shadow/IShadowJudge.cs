// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Agents.AI;

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper (Stage 2, M4) — a judge that runs <b>after</b> a run has returned, off the hot path, so it may do
/// EXPENSIVE work the inline gates reject (an LLM-as-judge, a network reputation/registry lookup). It never
/// blocks the run it judged; instead an adverse verdict ARMS a quarantine flag so a later run fails closed.
/// </summary>
public interface IShadowJudge
{
    /// <summary>Judge a completed run (a snapshot — see <see cref="ShadowJudgeContext"/>).</summary>
    Task<ShadowVerdict> JudgeAsync(ShadowJudgeContext context, CancellationToken cancellationToken = default);
}

/// <summary>The outcome of a shadow judgement.</summary>
/// <param name="Compromised">True if the run should be treated as compromised (arms quarantine).</param>
/// <param name="Reason">Human-readable explanation, recorded to the verdict sink.</param>
public sealed record ShadowVerdict(bool Compromised, string Reason)
{
    /// <summary>A clean (not-compromised) verdict.</summary>
    public static ShadowVerdict Clean(string reason = "clean") => new(false, reason);

    /// <summary>A compromised verdict (arms quarantine).</summary>
    public static ShadowVerdict Compromise(string reason) => new(true, reason);
}

/// <summary>
/// An immutable snapshot of a completed run handed to a shadow judge. Carries only immutable text plus the
/// session (so an adverse verdict can arm quarantine) — never the live <c>AgentTrace</c>, whose read paths
/// assume writes are done (§PERF-05: the shadow task must not race the returning run's trace serialization).
/// </summary>
/// <param name="ResponseText">The run's final response text (immutable).</param>
/// <param name="InputText">The run's input text, folded from the request messages.</param>
/// <param name="AgentName">The invoking agent's name, if known.</param>
/// <param name="Session">The run's session — the arming target for a compromised verdict (may be null).</param>
public sealed record ShadowJudgeContext(string ResponseText, string? InputText, string? AgentName, AgentSession? Session);
