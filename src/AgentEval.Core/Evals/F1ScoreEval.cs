// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>
/// Pure-code evaluator that computes token-level F1 score between an AI response and a ground-truth reference.
/// <para>
/// This evaluator has <b>zero LLM dependency</b> — it is deterministic and does not call any external service.
/// It lives in <c>AgentEval.Core</c> (not <c>AgentEval.Evals.Agentic</c>) so that consumers who only need F1
/// do not need to pull the entire agentic package.
/// Consumers of <c>AgentEval.Evals.Agentic</c> can access this type via
/// <c>using AgentEval.Evals;</c> — no re-export wrapper is needed.
/// </para>
/// <para>
/// <b>Algorithm</b>: whitespace-tokenized, lowercased word sets.
/// <list type="bullet">
///   <item>Precision = |response_tokens ∩ ground_truth_tokens| / |response_tokens|</item>
///   <item>Recall    = |response_tokens ∩ ground_truth_tokens| / |ground_truth_tokens|</item>
///   <item>F1        = 2 × (precision × recall) / (precision + recall)</item>
/// </list>
/// Edge cases: empty response → F1=0; empty ground truth → F1=0; both empty → F1=1.0.
/// </para>
/// <para>
/// <b>Ground-truth resolution</b>: uses <see cref="EvalInput.GroundTruth"/> if set;
/// otherwise falls back to the constructor-supplied <c>groundTruth</c> parameter.
/// If neither is available, the evaluator returns score=0 with a note in evidence.
/// </para>
/// <para>
/// <b>Provenance</b>: <c>Score.Confidence = 1.0</c> (deterministic — no sampling variance).
/// </para>
/// <para>
/// Rationale for placement in <c>AgentEval.Core</c> per plan-05 §5 challenge 5:
/// F1Score has zero LLM dependency and is useful standalone. Pulling a lightweight metric
/// into a package that bundles LLM plumbing would be a layering violation.
/// </para>
/// </summary>
public sealed class F1ScoreEval : AtomicCodeEval
{
    private readonly double _passThreshold;
    private readonly string? _groundTruth;

    /// <summary>
    /// Initialises a new <see cref="F1ScoreEval"/>.
    /// </summary>
    /// <param name="passThreshold">
    /// F1 value (0..1) at or above which the eval passes. Defaults to 0.50.
    /// </param>
    /// <param name="groundTruth">
    /// Optional fallback ground-truth string used when <see cref="EvalInput.GroundTruth"/>
    /// is not supplied. Per-input <see cref="EvalInput.GroundTruth"/> always takes precedence.
    /// </param>
    public F1ScoreEval(double passThreshold = 0.50, string? groundTruth = null)
        : base("f1_score", "F1 Score", "rag", "1.0.0")
    {
        if (!double.IsFinite(passThreshold) || passThreshold < 0.0 || passThreshold > 1.0)
            throw new ArgumentOutOfRangeException(nameof(passThreshold), passThreshold, "passThreshold must be a finite value in [0, 1].");
        _passThreshold = passThreshold;
        _groundTruth = groundTruth;
    }

    /// <inheritdoc/>
    protected override EvalResult Evaluate(EvalInput input)
    {
        var response = input.Response ?? string.Empty;
        // Per-input GroundTruth takes precedence over constructor fallback.
        var groundTruth = input.GroundTruth ?? _groundTruth;

        if (groundTruth is null)
        {
            return Build(
                value: 0.0,
                passed: false,
                severity: "medium",
                evidence: new[]
                {
                    new EvalEvidence("deterministic", "f1_score",
                        "Ground truth not provided via EvalInput.GroundTruth or constructor parameter — F1 cannot be computed. Score = 0."),
                });
        }

        var responseTokens = Tokenize(response);
        var truthTokens = Tokenize(groundTruth);

        // Both empty → trivially perfect.
        if (responseTokens.Count == 0 && truthTokens.Count == 0)
        {
            return BuildWithConfidence(
                value: 1.0,
                passed: 1.0 >= _passThreshold,
                severity: "none",
                message: "Both response and ground truth are empty; F1 = 1.0 (trivially perfect).");
        }

        if (responseTokens.Count == 0)
        {
            return BuildWithConfidence(
                value: 0.0,
                passed: false,
                severity: _passThreshold <= 0 ? "none" : "medium",
                message: "Response is empty; F1 = 0.0.");
        }

        if (truthTokens.Count == 0)
        {
            return BuildWithConfidence(
                value: 0.0,
                passed: false,
                severity: _passThreshold <= 0 ? "none" : "medium",
                message: "Ground truth is empty; F1 = 0.0.");
        }

        var intersection = responseTokens.Intersect(truthTokens).Count();
        var precision = (double)intersection / responseTokens.Count;
        var recall = (double)intersection / truthTokens.Count;
        var f1 = precision + recall == 0.0
            ? 0.0
            : 2.0 * precision * recall / (precision + recall);

        var passed = f1 >= _passThreshold;
        var severity = passed ? "none" : (f1 < 0.25 ? "high" : "medium");

        return new EvalResult(
            Metric: new(Key, Name, Category, Version),
            Score: new(
                Value: Math.Clamp(f1, 0.0, 1.0),
                Ordinal: null,
                Label: passed ? "pass" : "fail",
                Passed: passed,
                Threshold: _passThreshold,
                Severity: severity,
                // Confidence = 1.0 for a deterministic evaluator (no sampling variance).
                Confidence: 1.0),
            Details: new(
                Dimensions: new Dictionary<string, double>
                {
                    ["precision"] = precision,
                    ["recall"] = recall,
                    ["response_token_count"] = responseTokens.Count,
                    ["ground_truth_token_count"] = truthTokens.Count,
                    ["intersection_count"] = intersection,
                },
                Evidence: new[]
                {
                    new EvalEvidence(
                        Source: "deterministic",
                        Reference: "f1_score",
                        Message: $"P={precision:F3}, R={recall:F3}, F1={f1:F3} " +
                                 $"(intersection={intersection} / response={responseTokens.Count}, truth={truthTokens.Count} tokens)."),
                },
                Recommendations: null,
                SubResults: null,
                AggregationStrategy: null),
            Provenance: new("atomic-code", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private EvalResult BuildWithConfidence(double value, bool passed, string severity, string message)
    {
        var label = passed ? "pass" : "fail";
        return new EvalResult(
            Metric: new(Key, Name, Category, Version),
            Score: new(Math.Clamp(value, 0.0, 1.0), null, label, passed, _passThreshold, severity, 1.0),
            Details: new(null, new[] { new EvalEvidence("deterministic", "f1_score", message) }, null, null, null),
            Provenance: new("atomic-code", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Tokenizes a string into a lowercased word set using whitespace splitting.
    /// Punctuation attached to words is stripped for fairer token matching.
    /// </summary>
    private static IReadOnlyCollection<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        return text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.ToLowerInvariant().Trim('.', ',', '!', '?', ';', ':', '"', '\'', '(', ')', '[', ']'))
            .Where(t => t.Length > 0)
            .ToHashSet();
    }
}
