// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Evals;
using AgentEval.Tracing;
using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Evals.Agentic.Safety;

/// <summary>
/// Glass Box Phase 3 (A4) — pure-code evaluator that flags turns truncated by the output-token cap
/// (a per-turn <c>length</c> finish reason), which signal incomplete reasoning / cost misconfiguration.
/// <para><b>Score</b>: <c>1 - lengthFinishTurns / totalChatResponses</c>. <b>Threshold</b>: 1.0.
/// <b>Severity</b> on fail: <c>low</c>.</para>
/// <para>Reads ChatTurn Response entries from the Glass Box trace; skipped when no trace / no ChatTurn responses.</para>
/// </summary>
public sealed class TruncationDetectionEval : IEval
{
    private const string KeyValue = "truncation_detection";
    private const string NameValue = "Truncation Detection";
    private const string CategoryValue = "operational";
    private const string VersionValue = "1.0.0";
    private const double PassThreshold = 1.0;

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
                "TruncationDetectionEval requires a Glass Box trace attached via EvalInput.WithTrace(...)."));
        }

        var responses = trace.Entries
            .Where(e => e.EffectiveScope == TraceEntryScope.ChatTurn && e.Type == TraceEntryType.Response)
            .ToList();
        if (responses.Count == 0)
        {
            return Task.FromResult(EvalResult.Skipped(this,
                "TruncationDetectionEval: the trace has no ChatTurn response entries to score."));
        }

        var truncated = responses.Count(e =>
            string.Equals(e.FinishReason, "length", StringComparison.OrdinalIgnoreCase));
        var score = Math.Clamp(1.0 - (double)truncated / responses.Count, 0.0, 1.0);
        var passed = score >= PassThreshold;

        return Task.FromResult(new EvalResult(
            Metric: new(KeyValue, NameValue, CategoryValue, VersionValue),
            Score: new(score, null, passed ? "pass" : "fail", passed, PassThreshold, passed ? "none" : "low", null),
            Details: new(
                Dimensions: new Dictionary<string, double>
                {
                    ["truncated_turns"] = truncated,
                    ["total_responses"] = responses.Count,
                },
                Evidence:
                [
                    new EvalEvidence("chat-turn", "truncation_detection",
                        $"{truncated} turn(s) hit the output-token cap ('length' finish) across {responses.Count} response(s)."),
                ],
                Recommendations: passed ? null :
                [
                    "One or more turns were truncated by the max-output-token cap. Raise the cap or shorten the "
                    + "prompt to avoid incomplete responses.",
                ],
                SubResults: null,
                AggregationStrategy: null),
            Provenance: new("atomic-code", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow));
    }
}
