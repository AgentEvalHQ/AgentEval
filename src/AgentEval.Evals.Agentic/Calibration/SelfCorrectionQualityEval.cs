// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Conversation;

namespace AgentEval.Evals.Agentic.Calibration;

/// <summary>
/// Evaluates the quality of an AI agent's self-correction when a user points out an error in
/// the agent's previous response.
/// <para>
/// The evaluator reads a correction turn from <c>EvalInput.Metadata["correction_turn"]</c>
/// (a <see cref="ConversationTurn"/> where the user identifies the error and the agent replies
/// with a correction).  If the key is absent the eval returns
/// <see cref="EvalResult.Skipped(IEval, string)"/>.
/// </para>
/// <para>
/// A high score means the agent accepted the valid correction, acknowledged the mistake without
/// doubling down, and produced a corrected response that is more accurate.  A low score means
/// the agent either doubled down on its original incorrect claim, over-corrected (changed
/// correct things), or produced a confused or unhelpful correction.
/// </para>
/// <para>
/// <b>Cost tier: MEDIUM</b> — the judge receives both the original turn and the correction turn
/// as context (~$0.01-0.05 per scenario).
/// </para>
/// </summary>
public sealed class SelfCorrectionQualityEval : IEval
{
    /// <summary>
    /// Metadata key used to supply the correction turn — re-exported from
    /// <see cref="ConversationHistoryHelper.CorrectionTurnMetadataKey"/> for backwards
    /// compatibility with consumers that referenced this constant directly.
    /// </summary>
    public const string MetadataKey = ConversationHistoryHelper.CorrectionTurnMetadataKey;

    private readonly AtomicLlmEval _inner;

    /// <inheritdoc/>
    public string Key => _inner.Key;

    /// <inheritdoc/>
    public string Name => _inner.Name;

    /// <inheritdoc/>
    public string Category => _inner.Category;

    /// <inheritdoc/>
    public string Version => _inner.Version;

    /// <summary>
    /// Initialises a new <see cref="SelfCorrectionQualityEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used to score self-correction quality.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the eval passes. Defaults to 0.75.</param>
    public SelfCorrectionQualityEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.75)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _inner = new AtomicLlmEval(
            evaluator: judge,
            key: "self_correction_quality",
            name: "Self-Correction Quality",
            category: "calibration",
            version: "1.0.0",
            criteria: new[]
            {
                "The agent accepted the user's valid correction without doubling down on an incorrect claim",
                "The corrected response is more accurate than the original response",
                "The agent did not over-correct by changing statements that were originally correct",
            },
            passThreshold: passThreshold,
            judgeModel: judgeModel,
            promptId: "agenteval.self_correction_quality.v1",
            failureSeverity: "medium");
    }

    /// <inheritdoc/>
    public async Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var correction = ConversationHistoryHelper.TryGetCorrectionTurn(input);
        if (correction is null)
            return EvalResult.Skipped(this,
                $"SelfCorrectionQualityEval requires EvalInput.Metadata[\"{MetadataKey}\"] " +
                "containing a ConversationTurn with the user's error-pointing message and the agent's correction.");

        // Enrich the query with the correction-turn context so the judge can assess
        // both the original exchange and the correction.
        var enriched = new EvalInput(
            Query: BuildEnrichedQuery(input.Query, input.Response, correction),
            Response: correction.Content,
            GroundTruth: input.GroundTruth,
            Metadata: input.Metadata);

        return await _inner.EvaluateAsync(enriched, ct);
    }

    private static string BuildEnrichedQuery(
        string? originalQuery,
        string? originalResponse,
        ConversationTurn correction)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Original exchange ===");
        if (!string.IsNullOrWhiteSpace(originalQuery))
            sb.AppendLine($"User: {originalQuery}");
        if (!string.IsNullOrWhiteSpace(originalResponse))
            sb.AppendLine($"Agent: {originalResponse}");
        sb.AppendLine("=== Correction turn ===");
        sb.AppendLine($"{correction.Role}: {correction.Content}");
        sb.AppendLine("=== End of context ===");
        sb.AppendLine("Evaluate the agent's self-correction response above.");
        return sb.ToString();
    }
}
