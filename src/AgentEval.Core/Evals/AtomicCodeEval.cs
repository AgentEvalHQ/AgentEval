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

    /// <summary>
    /// The UNDECIDABLE verdict: this eval could not measure anything on this input, and says why.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mirrors <see cref="EvalResult.Skipped"/> (ADR-030 D13) for the deterministic lane. It exists
    /// because both shipped consumers of the join had already hand-written the same five-positional
    /// record under a private <c>Undecidable</c> helper, and the discipline is easy to get wrong in
    /// three separate ways:
    /// </para>
    /// <list type="bullet">
    ///   <item>An undecidable result is <b>never</b> <c>Passed</c>, and it is <b>not a 0.0 fail</b>.
    ///         A 0.0 fail is a MEASUREMENT — it says the eval looked and found nothing good. This says
    ///         the eval could not look. Absence is not a zero.</item>
    ///   <item>The reason is carried <b>twice</b>, in <see cref="EvalDetails.Summary"/> and in
    ///         <c>Recommendations</c>, because renderers read one or the other and a reason nobody
    ///         displays is a reason nobody acts on.</item>
    ///   <item><see cref="EvalScore.NotApplicable"/> is the only route to
    ///         <c>MeasurementState.NotApplicable</c>, which serialises only when non-default — so this
    ///         helper cannot write the <c>measurement</c> field unconditionally, and Q4(ii) stays
    ///         untouched.</item>
    /// </list>
    /// </remarks>
    /// <param name="reason">Why no measurement was possible. Required: an unexplained absence is
    /// indistinguishable from an unnoticed one.</param>
    /// <param name="evidence">Optional supporting evidence for the reason.</param>
    protected EvalResult NotApplicable(string reason, EvalEvidence? evidence = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return new EvalResult(
            Metric: new(Key, Name, Category, Version),
            Score: EvalScore.NotApplicable(),
            Details: new(null, evidence is null ? null : [evidence], [reason], null, null) { Summary = reason },
            Provenance: new("atomic-code", null, null, null, null, 0, false),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }
}
