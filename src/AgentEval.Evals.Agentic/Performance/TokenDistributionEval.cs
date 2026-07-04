// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Tracing;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Evals.Agentic.Performance;

/// <summary>
/// Glass Box Phase 3 (A7) — pure-code evaluator that flags a skewed per-turn token distribution: one turn
/// consuming a disproportionate share of the run's completion tokens (a formatting loop / runaway generation).
/// <para><b>Score</b>: <c>1 - maxTurnTokens / sumTurnTokens</c> over ChatTurn Response
/// <b><c>CompletionTokens</c></b> (not TotalTokens — prompt/context accumulates across turns, so TotalTokens
/// structurally lets later turns dominate). <b>Threshold</b>: 0.5 (a 2-turn trace caps at 0.5 by construction,
/// passing only at perfect balance). <b>Severity</b> on fail: <c>low</c>.</para>
/// <para>Skipped when no trace or fewer than 2 turns carry token usage.</para>
/// </summary>
public sealed class TokenDistributionEval : IEval
{
    private const string KeyValue = "token_distribution";
    private const string NameValue = "Token Distribution Skew";
    private const string CategoryValue = "operational";
    private const string VersionValue = "1.0.0";
    private const double PassThreshold = 0.5;

    /// <inheritdoc/>
    public string Key => KeyValue;

    /// <inheritdoc/>
    public string Name => NameValue;

    /// <inheritdoc/>
    public string Category => CategoryValue;

    /// <inheritdoc/>
    public string Version => VersionValue;

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        AgentTrace? trace = input.GetTrace();
        if (trace is null)
        {
            return Task.FromResult(EvalResult.Skipped(this,
                "TokenDistributionEval requires a Glass Box trace attached via EvalInput.WithTrace(...)."));
        }

        var completionTokens = trace.Entries
            .Where(e => e.EffectiveScope == TraceEntryScope.ChatTurn && e.Type == TraceEntryType.Response && e.TokenUsage is not null)
            .Select(e => e.TokenUsage!.CompletionTokens)
            .ToList();
        if (completionTokens.Count < 2)
        {
            return Task.FromResult(EvalResult.Skipped(this,
                "TokenDistributionEval needs at least 2 turns carrying token usage."));
        }

        var sum = completionTokens.Sum();
        if (sum <= 0)
        {
            return Task.FromResult(EvalResult.Skipped(this,
                "TokenDistributionEval: total completion tokens is 0 — cannot measure skew."));
        }

        var max = completionTokens.Max();
        var score = Math.Clamp(1.0 - (double)max / sum, 0.0, 1.0);
        var passed = score >= PassThreshold;

        return Task.FromResult(new EvalResult(
            Metric: new(KeyValue, NameValue, CategoryValue, VersionValue),
            Score: new(score, null, passed ? "pass" : "fail", passed, PassThreshold, passed ? "none" : "low", null),
            Details: new(
                Dimensions: new Dictionary<string, double>
                {
                    ["turns"] = completionTokens.Count,
                    ["max_turn_tokens"] = max,
                    ["total_completion_tokens"] = sum,
                    ["max_share"] = (double)max / sum,
                },
                Evidence:
                [
                    new EvalEvidence("chat-turn", "token_distribution",
                        $"the heaviest turn used {max} of {sum} completion tokens ({(double)max / sum:P0} of the run) across {completionTokens.Count} turn(s)."),
                ],
                Recommendations: passed ? null :
                [
                    "One turn dominated completion-token usage. Inspect it for a formatting loop or runaway "
                    + "generation; consider a per-turn output cap.",
                ],
                SubResults: null,
                AggregationStrategy: null),
            Provenance: new("atomic-code", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow));
    }
}
