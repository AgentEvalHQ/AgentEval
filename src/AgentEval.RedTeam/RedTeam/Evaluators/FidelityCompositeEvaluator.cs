// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Core; // AgentResponse

namespace AgentEval.RedTeam.Evaluators;

/// <summary>
/// RC-1: behavioral-first composite. Runs a tool-aware evaluator; if conclusive the high-fidelity
/// verdict wins. Otherwise it falls back to a verbal evaluator and labels that verdict
/// <see cref="EvidenceFidelity.Verbal"/>, so reports never confuse "the agent said it" with "the agent did it".
/// </summary>
public sealed class FidelityCompositeEvaluator : IProbeEvaluator
{
    private readonly IProbeEvaluator _behavioral;
    private readonly IProbeEvaluator _verbalFallback;

    /// <summary>Creates a composite that prefers the behavioral verdict and falls back to verbal analysis.</summary>
    public FidelityCompositeEvaluator(IProbeEvaluator behavioral, IProbeEvaluator verbalFallback)
    {
        _behavioral = behavioral ?? throw new ArgumentNullException(nameof(behavioral));
        _verbalFallback = verbalFallback ?? throw new ArgumentNullException(nameof(verbalFallback));
    }

    /// <inheritdoc />
    public string Name => $"FidelityComposite({_behavioral.Name}->{_verbalFallback.Name})";

    /// <summary>Text-only path: no trace, so go straight to the verbal fallback (labeled Verbal).</summary>
    public async Task<EvaluationResult> EvaluateAsync(AttackProbe probe, string response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(response);
        var verbal = await _verbalFallback.EvaluateAsync(probe, response, cancellationToken).ConfigureAwait(false);
        return WithFidelity(verbal, EvidenceFidelity.Verbal);
    }

    /// <inheritdoc />
    public async Task<EvaluationResult> EvaluateAsync(AttackProbe probe, AgentResponse response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(response);

        var behavioral = await _behavioral.EvaluateAsync(probe, response, cancellationToken).ConfigureAwait(false);
        if (behavioral.Outcome != EvaluationOutcome.Inconclusive)
            return behavioral; // behavioral-first precedence: a conclusive trace verdict (incl. Resisted — the agent
                               // acted and the act was safe) is authoritative and is NOT second-guessed by the verbal
                               // marker check. (A marker leaked only into final text after a safe tool call is thus
                               // scored Resisted; this is intentional — observed behavior outranks words.)

        var verbal = await _verbalFallback.EvaluateAsync(probe, response.Text, cancellationToken).ConfigureAwait(false);
        return WithFidelity(verbal, EvidenceFidelity.Verbal);
    }

    private static EvaluationResult WithFidelity(EvaluationResult source, EvidenceFidelity fidelity)
    {
        var meta = source.Metadata is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(source.Metadata);
        meta["fidelity"] = fidelity;

        return source.Outcome switch
        {
            EvaluationOutcome.Succeeded => EvaluationResult.Succeeded(source.Reason, source.MatchedItems, meta, source.Confidence),
            EvaluationOutcome.Resisted  => EvaluationResult.Resisted(source.Reason, meta, source.Confidence),
            _                           => EvaluationResult.Inconclusive(source.Reason, meta, source.Confidence),
        };
    }
}
