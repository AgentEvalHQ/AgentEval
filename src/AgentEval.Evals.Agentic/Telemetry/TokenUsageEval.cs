// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals.Agentic.Telemetry;

/// <summary>
/// Pure-code evaluator that measures total token consumption against a configurable budget.
/// <para>
/// <b>Score formula</b>:
/// <list type="bullet">
///   <item>1.0 if tokens used &lt;= <c>budgetTokens</c></item>
///   <item>0.0 if tokens used &gt; 2 × <c>budgetTokens</c></item>
///   <item>Linear interpolation between budget and 2× budget.</item>
/// </list>
/// </para>
/// <para>
/// <b>Severity</b>: <c>low</c> — token overruns are a cost signal rather than a quality failure.
/// </para>
/// <para>
/// <b>Input contract</b>: telemetry data must be provided either via the constructor or via
/// <see cref="EvalInput.Metadata"/> under key <c>"agentic_telemetry"</c>
/// (an <see cref="AgenticTelemetry"/> instance). Per-input metadata takes precedence.
/// A null token count is not tracked and is skipped; a reported zero remains a measurement.
/// Negative counts produce an error without a measurement.
/// </para>
/// </summary>
public sealed class TokenUsageEval : IEvalConfigurationSource
{
    private const string KeyValue      = "token_usage";
    private const string NameValue     = "Token Usage";
    private const string CategoryValue = "operational";
    private const string VersionValue  = "1.0.0";

    private readonly AgenticTelemetry? _data;
    private readonly int    _budgetTokens;
    private readonly double _passThreshold;

    /// <inheritdoc/>
    public string Key      => KeyValue;

    /// <inheritdoc/>
    public string Name     => NameValue;

    /// <inheritdoc/>
    public string Category => CategoryValue;

    /// <inheritdoc/>
    public string Version  => VersionValue;

    /// <summary>
    /// Initialises a <see cref="TokenUsageEval"/> that reads telemetry from
    /// <see cref="EvalInput.Metadata"/> at evaluation time.
    /// </summary>
    /// <param name="budgetTokens">Maximum acceptable token count per run. Default: 10,000.</param>
    /// <param name="passThreshold">Score threshold for a passing verdict. Default: 0.80.</param>
    public TokenUsageEval(int budgetTokens = 10000, double passThreshold = 0.80)
        : this(null, budgetTokens, passThreshold) { }

    /// <summary>
    /// Initialises a <see cref="TokenUsageEval"/> with telemetry data supplied directly.
    /// The constructor-supplied data is used when <see cref="EvalInput.Metadata"/> does not
    /// contain the <c>"agentic_telemetry"</c> key.
    /// </summary>
    /// <param name="data">Telemetry data to use as a fallback when metadata is absent.</param>
    /// <param name="budgetTokens">Maximum acceptable token count per run. Default: 10,000.</param>
    /// <param name="passThreshold">Score threshold for a passing verdict. Default: 0.80.</param>
    public TokenUsageEval(AgenticTelemetry? data, int budgetTokens = 10000, double passThreshold = 0.80)
    {
        _data          = data;
        _budgetTokens  = budgetTokens;
        _passThreshold = passThreshold;
    }

    /// <summary>Captures evaluation policy; measured telemetry and prior results remain subject evidence.</summary>
    public global::System.Text.Json.Nodes.JsonObject CaptureConfiguration() => new()
    {
        ["budgetTokens"] = _budgetTokens,
        ["passThreshold"] = _passThreshold,
    };

    /// <inheritdoc/>
    public Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var telemetry = TelemetryHelper.Resolve(input, _data);
        if (telemetry is null)
        {
            return Task.FromResult(EvalResult.Skipped(this,
                $"TokenUsageEval requires telemetry data via EvalInput.Metadata[\"{LatencyEval.MetadataKey}\"] or constructor parameter."));
        }

        if (telemetry.TokensUsed is not { } tokensUsed)
            return Task.FromResult(EvalResult.Skipped(this,
                "The run's token usage was not tracked (AgenticTelemetry.TokensUsed is null), so Token Usage was not measured. Supply a recorded count; an untracked count is not 0 tokens."));
        if (tokensUsed < 0)
            return Task.FromResult(TelemetryHelper.InvalidReading(this, _passThreshold,
                $"AgenticTelemetry.TokensUsed is {tokensUsed}: expected a non-negative token count. Supply a recorded count or null when not tracked; Token Usage was not measured."));

        var score = TelemetryHelper.LinearScore(tokensUsed, _budgetTokens);
        var passed = score >= _passThreshold;
        var severity = passed ? "none" : "low";

        return Task.FromResult(new EvalResult(
            Metric: new(KeyValue, NameValue, CategoryValue, VersionValue),
            Score: new(score, null, passed ? "pass" : "fail", passed, _passThreshold, severity, null),
            Details: new(
                Dimensions: new Dictionary<string, double>
                {
                    ["tokens_used"]   = tokensUsed,
                    ["budget_tokens"] = _budgetTokens,
                    ["overage_ratio"] = _budgetTokens > 0 ? (double)tokensUsed / _budgetTokens : 0,
                },
                Evidence:
                [
                    new EvalEvidence(
                        Source: "telemetry",
                        Reference: "token_usage",
                        Message: $"Tokens used: {tokensUsed:N0} vs budget {_budgetTokens:N0} " +
                                 $"({(double)tokensUsed / Math.Max(1, _budgetTokens):P0} of budget)."),
                ],
                Recommendations: passed ? null :
                [
                    $"Token usage ({tokensUsed:N0}) exceeds budget ({_budgetTokens:N0}). " +
                    "Review prompt lengths, context window usage, and tool-result verbosity."
                ],
                SubResults: null,
                AggregationStrategy: null),
            Provenance: new("atomic-code", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow));
    }
}
