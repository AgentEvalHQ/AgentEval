// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

namespace Galaxus.RecommendationAgent.Evals.Controls;

/// <summary>
/// Runs any arm on Eval 02's canonical history question INSTEAD of the customer's own words —
/// same customer, same architecture, the stated need removed.
/// </summary>
/// <remarks>
/// <para>
/// The paired reference Eval 02b needs: when the same loop is asked the customer's question and
/// then the generic one, the difference between the two cells is what READING THE NEED bought.
/// Nothing else varies — not the model presence, not the retriever, not the persona — so this is
/// a comparison with one moving operand, unlike the Eval 02 pairing that this suite's own notes
/// refuse to enter into a sign test.
/// </para>
/// <para>
/// It rewrites the PROMPT only. The persona id in the frame is preserved, so the inner arm still
/// reads the right history; only the utterance after the frame is replaced.
/// </para>
/// </remarks>
public sealed class UtteranceBlindArm : IEvaluableAgent
{
    private readonly IEvaluableAgent _inner;

    /// <summary>Wraps an arm.</summary>
    /// <param name="inner">The arm that will be asked the generic question.</param>
    public UtteranceBlindArm(IEvaluableAgent inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>The wrapped arm, for callers that read telemetry off it.</summary>
    public IEvaluableAgent Inner => _inner;

    /// <inheritdoc/>
    public string Name => _inner.Name + " (utterance-blind)";

    /// <inheritdoc/>
    public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        string userId = ScriptedTrace.PersonaIdFrom(prompt)
            ?? throw new InvalidOperationException("The prompt carries no customer id; the utterance-blind wrapper cannot re-frame it.");

        return _inner.InvokeAsync(GalaxusEvalPrompt.For(userId, GalaxusEvalPrompt.CoverageCanonical), cancellationToken);
    }
}
