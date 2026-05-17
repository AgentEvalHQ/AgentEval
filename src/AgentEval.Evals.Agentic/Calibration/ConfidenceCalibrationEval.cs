// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Evals.Agentic.Calibration;

/// <summary>
/// Evaluates whether an AI agent's stated confidence in its own responses is calibrated —
/// i.e., when it says "I'm confident", it is actually right; when it says "I'm unsure", it
/// is more likely to be wrong.
/// <para>
/// The judge inspects the response for explicit confidence markers
/// ("I'm certain", "I think", "I'm not sure but…", etc.) and cross-checks them against either
/// <see cref="EvalInput.GroundTruth"/> (when present) or against the overall apparent correctness
/// of the response.  A high score means that expressed confidence levels match observed accuracy.
/// </para>
/// <para>
/// <b>Cost tier: LOW</b> — single-turn LLM judge (~$0.005-0.01 per scenario).
/// </para>
/// </summary>
public sealed class ConfidenceCalibrationEval : IEval
{
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
    /// Initialises a new <see cref="ConfidenceCalibrationEval"/>.
    /// </summary>
    /// <param name="judge">The LLM evaluator used to score confidence calibration.</param>
    /// <param name="judgeModel">Optional judge model identifier recorded in provenance.</param>
    /// <param name="passThreshold">Score fraction (0..1) at or above which the eval passes. Defaults to 0.70.</param>
    public ConfidenceCalibrationEval(IEvaluator judge, string? judgeModel = null, double passThreshold = 0.70)
    {
        ArgumentNullException.ThrowIfNull(judge);
        _inner = new AtomicLlmEval(
            evaluator: judge,
            key: "confidence_calibration",
            name: "Confidence Calibration",
            category: "calibration",
            version: "1.0.0",
            criteria: new[]
            {
                "When the agent expresses high confidence, the claim is factually correct or well-supported",
                "When the agent expresses low confidence or uncertainty, the claim is appropriately hedged",
                "The agent does not over-confidently assert incorrect or unverifiable information",
            },
            passThreshold: passThreshold,
            judgeModel: judgeModel,
            promptId: "agenteval.confidence_calibration.v1",
            failureSeverity: "low");
    }

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // When ground truth is available, enrich the query with it so the judge can
        // cross-check confidence claims against the reference answer.
        var effective = input.GroundTruth is not null
            ? new EvalInput(
                Query: $"{input.Query}\n\n[Ground truth reference: {input.GroundTruth}]",
                Response: input.Response,
                GroundTruth: input.GroundTruth,
                Metadata: input.Metadata)
            : input;

        return _inner.EvaluateAsync(effective, ct);
    }
}
