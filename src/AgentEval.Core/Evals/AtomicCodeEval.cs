// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>
/// Abstract base for deterministic (code-based) atomic evals.
/// Subclasses implement <see cref="Evaluate"/> to compute a result synchronously.
/// </summary>
public abstract class AtomicCodeEval : AtomicEval
{
    /// <summary>Initialises a new instance of <see cref="AtomicCodeEval"/>.</summary>
    protected AtomicCodeEval(string key, string name, string category, string version)
        : base(key, name, category, version) { }

    /// <inheritdoc/>
    public sealed override Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Evaluate(input));
    }

    /// <summary>Synchronously evaluates <paramref name="input"/> and returns a result.</summary>
    protected abstract EvalResult Evaluate(EvalInput input);

    /// <summary>Helper that builds an <see cref="EvalResult"/> with <c>Provenance.Type = "atomic-code"</c>.</summary>
    protected EvalResult Build(
        double value,
        bool passed,
        string severity,
        IReadOnlyDictionary<string, double>? dimensions = null,
        IReadOnlyList<EvalEvidence>? evidence = null)
    {
        var label = passed ? "pass" : "fail";
        return new EvalResult(
            Metric: new(Key, Name, Category, Version),
            Score: new(Math.Clamp(value, 0.0, 1.0), null, label, passed, null, severity, null),
            Details: new(dimensions, evidence, null, null, null),
            Provenance: new("atomic-code", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }
}
