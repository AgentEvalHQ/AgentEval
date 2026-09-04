// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace AgentEval.Output;

/// <summary>
/// The outcome of a single assertion. Deliberately three-valued: an assertion that could not
/// decide is neither a pass nor a failure, and collapsing it into either one misreports the run.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Inconclusive"/> is <c>0</c> on purpose. It is the value a missing / defaulted /
/// round-tripped-from-an-older-artifact field takes, and "we do not know" is the only safe thing
/// for an unknown to mean. If <see cref="Passed"/> were the zero value, every gap in the pipeline
/// would render green — the flattering direction.
/// </para>
/// </remarks>
public enum AssertionOutcome
{
    /// <summary>
    /// The assertion ran but could not decide: the evidence it needs was not captured
    /// (e.g. tool timing was never recorded), or the assertion is structurally unable to
    /// fail in this run (e.g. <c>NeverCallTool(X)</c> when <c>X</c> was never available to
    /// the agent, so the check has a chance floor of 1.0 and carries no information).
    /// <b>Never render this as a pass.</b>
    /// </summary>
    Inconclusive = 0,

    /// <summary>The assertion was decidable and held.</summary>
    Passed = 1,

    /// <summary>The assertion was decidable and did not hold.</summary>
    Failed = 2
}

/// <summary>
/// Result of a single assertion within a scenario. This is the <b>canonical</b> assertion-result
/// type for AgentEval; <c>AgentEval.Testing.AssertionResult</c> is an obsolete alias that converts
/// to and from it implicitly.
/// </summary>
/// <remarks>
/// <para>
/// The three positional members are unchanged from the original shape
/// (<c>Assertion</c>, <c>Passed</c>, <c>Message</c>) so every existing call site and every
/// persisted artifact keeps working. <see cref="Outcome"/> is additive: when it is absent from
/// JSON it is derived from <see cref="Passed"/>, so old artifacts round-trip unchanged.
/// </para>
/// <para>
/// <b>Invariant:</b> <c>Outcome == Passed</c> if and only if <see cref="Passed"/> is
/// <see langword="true"/>. An <see cref="AssertionOutcome.Inconclusive"/> result therefore always
/// carries <c>Passed == false</c> — a check that could not fail must never be counted as one that
/// held. Constructing a result that claims otherwise throws; use <see cref="Undecidable"/>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// AssertionResult.Pass("HaveCalledTool(Search)");
/// AssertionResult.Fail("HaveCallCount", "Expected 2 tool call(s), but 3 were made.");
/// AssertionResult.Undecidable("NeverCallTool(PlaceOrder)",
///     "'PlaceOrder' was not among the agent's declared tools — this check cannot fail.");
/// </code>
/// </example>
public sealed record AssertionResult(string Assertion, bool Passed, string? Message)
{
    // Null when the outcome was never stated explicitly, in which case it is derived from Passed.
    // Storing the explicit value (rather than a resolved one) keeps `with { Passed = ... }`
    // from silently promoting an Inconclusive result to a pass in the Outcome getter.
    private readonly AssertionOutcome? _explicitOutcome;

    // Passed is declared explicitly (not left as the auto-property the positional parameter would
    // generate) so that a `with { Passed = true }` copy of an Inconclusive result is REFUSED rather
    // than produced. Without this, the copy kept Outcome == Inconclusive but reported Passed == true —
    // and every consumer that reads Passed got a pass on a check that never decided. The record's
    // clone copies _explicitOutcome before the init accessor runs, so the accessor can see it.
    // The primary constructor initialises the field directly (no explicit outcome exists yet, so
    // there is nothing to validate on that path).
    private readonly bool _passed = Passed;

    /// <summary>
    /// Whether the assertion was decidable and held. Kept as a positional member so every existing
    /// call site and persisted artifact is unchanged.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when set to <see langword="true"/> on a copy of a result whose explicit
    /// <see cref="Outcome"/> is not <see cref="AssertionOutcome.Passed"/>. An inconclusive or failed
    /// result cannot be promoted to a pass by copying it; construct a fresh result with
    /// <see cref="Pass"/> instead.
    /// </exception>
    public bool Passed
    {
        get => _passed;
        init
        {
            if (value && _explicitOutcome is { } explicitOutcome && explicitOutcome != AssertionOutcome.Passed)
            {
                throw new ArgumentException(
                    $"Passed=true contradicts the explicit outcome '{explicitOutcome}'. A result that did " +
                    "not decide (or did not hold) cannot be promoted to a pass by copying it — construct a " +
                    $"fresh result with {nameof(AssertionResult)}.{nameof(Pass)}(...) instead.",
                    nameof(value));
            }

            _passed = value;
        }
    }

    /// <summary>
    /// The three-valued outcome. Derived from <see cref="Passed"/> unless stated explicitly.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when the value contradicts <see cref="Passed"/> — i.e. when
    /// <see cref="AssertionOutcome.Passed"/> is set on a result whose <see cref="Passed"/> is
    /// <see langword="false"/>, or a non-passing outcome is set on one whose
    /// <see cref="Passed"/> is <see langword="true"/>.
    /// </exception>
    public AssertionOutcome Outcome
    {
        get => _explicitOutcome ?? (Passed ? AssertionOutcome.Passed : AssertionOutcome.Failed);
        init
        {
            if ((value == AssertionOutcome.Passed) != Passed)
            {
                throw new ArgumentException(
                    $"Outcome '{value}' contradicts Passed={Passed}. An assertion that did not " +
                    "decide (or did not hold) must not report Passed=true. Use " +
                    $"{nameof(AssertionResult)}.{nameof(Undecidable)}(...) for a check that could not decide.",
                    nameof(value));
            }

            _explicitOutcome = value;
        }
    }

    /// <summary>
    /// Alias for <see cref="Assertion"/>, kept so code written against the obsolete
    /// <c>AgentEval.Testing.AssertionResult</c> vocabulary (<c>Name</c>) still compiles.
    /// </summary>
    [JsonIgnore]
    public string Name => Assertion;

    /// <summary>
    /// <see langword="true"/> when the assertion could not decide. Report and gate on this
    /// separately — an inconclusive result is not evidence either way.
    /// </summary>
    [JsonIgnore]
    public bool IsInconclusive => Outcome == AssertionOutcome.Inconclusive;

    /// <summary>Creates a passing result.</summary>
    /// <param name="assertion">The assertion's name, e.g. <c>HaveCalledTool(Search)</c>.</param>
    /// <param name="message">Optional detail.</param>
    public static AssertionResult Pass(string assertion, string? message = null)
        => new(assertion, true, message);

    /// <summary>Creates a failing result.</summary>
    /// <param name="assertion">The assertion's name.</param>
    /// <param name="message">The failure detail.</param>
    public static AssertionResult Fail(string assertion, string? message = null)
        => new(assertion, false, message);

    /// <summary>
    /// Creates a result for an assertion that ran but could not decide — the evidence was
    /// missing, or the check was structurally unable to fail.
    /// </summary>
    /// <param name="assertion">The assertion's name.</param>
    /// <param name="reason">Why the assertion could not decide. Required — an undecidable
    /// result without a stated reason is indistinguishable from a bug.</param>
    public static AssertionResult Undecidable(string assertion, string reason)
        => new(assertion, false, reason) { Outcome = AssertionOutcome.Inconclusive };
}
