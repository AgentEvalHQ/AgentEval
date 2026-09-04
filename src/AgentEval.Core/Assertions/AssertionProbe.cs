// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Assertions;

/// <summary>
/// Watches one assertion so its outcome is recorded, not only its failures. Obtained from
/// <see cref="AgentEvalScope.BeginAssertion"/> and disposed at the end of the assertion.
/// </summary>
/// <remarks>
/// <para>
/// The probe never decides anything itself: it observes whether the assertion body raised a
/// failure into the enclosing scope, and records a pass, a failure, or — when the assertion said
/// so via <see cref="MarkInconclusive"/> — an undecidable result. That keeps every assertion's
/// public signature and body unchanged apart from the one <c>using</c> line.
/// </para>
/// <para>
/// When no scope is active (the xUnit path, where a failure throws instead of being collected)
/// <see cref="AgentEvalScope.BeginAssertion"/> hands back the shared <see cref="Inactive"/> probe:
/// nothing is allocated and every member is a no-op.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [StackTraceHidden]
/// public ToolUsageAssertions NeverCallTool(string toolName, string because)
/// {
///     using var probe = AgentEvalScope.BeginAssertion(toolName);
///     if (!_report.WasToolCalled(toolName))
///     {
///         probe.MarkInconclusive($"'{toolName}' was never available — this check cannot fail.");
///         return this;
///     }
///     // ... AgentEvalScope.FailWith(...) as before
/// }
/// </code>
/// </example>
public sealed class AssertionProbe : IDisposable
{
    /// <summary>The shared no-op probe returned when no scope is collecting.</summary>
    internal static readonly AssertionProbe Inactive = new();

    private readonly AgentEvalScope? _scope;
    private readonly string _name;
    private readonly int _failuresAtStart;
    private string? _inconclusiveReason;
    private bool _completed;
    private bool _disposed;

    private AssertionProbe()
    {
        _name = string.Empty;
    }

    internal AssertionProbe(AgentEvalScope scope, string name)
    {
        _scope = scope;
        _name = name;
        _failuresAtStart = scope.FailureCount;
        scope.EnterProbe();
    }

    /// <summary>
    /// Gets whether this probe is recording. <see langword="false"/> for the shared no-op probe.
    /// </summary>
    public bool IsActive => _scope is not null;

    /// <summary>
    /// Gets the name the outcome will be recorded under.
    /// </summary>
    public string Name => _name;

    /// <summary>
    /// Declares that the assertion ran but could not decide — the evidence it needs was not
    /// captured, or it is structurally unable to fail in this run (a chance floor of 1.0). The
    /// result is recorded as <see cref="Output.AssertionOutcome.Inconclusive"/> instead of a pass.
    /// </summary>
    /// <param name="reason">Why the assertion could not decide. Surfaced in the report.</param>
    /// <remarks>
    /// A failure still wins: if the assertion also raised one, the failure is what gets recorded.
    /// Calling this more than once keeps the first reason.
    /// </remarks>
    public void MarkInconclusive(string reason)
    {
        if (_scope is null) return;
        _inconclusiveReason ??= reason;
    }

    /// <summary>
    /// Marks the assertion as having run to completion. Call it on the way out — the assertion
    /// methods do this by returning through <see cref="Complete{T}(T)"/>.
    /// </summary>
    /// <remarks>
    /// A probe that is disposed <i>without</i> this having been called records
    /// <see cref="Output.AssertionOutcome.Inconclusive"/>, not a pass: the only way to reach
    /// <see cref="Dispose"/> without completing is an exception unwinding through the assertion,
    /// and an assertion that crashed decided nothing.
    /// </remarks>
    public void Complete() => _completed = true;

    /// <summary>
    /// Marks the assertion as having run to completion and passes <paramref name="value"/>
    /// straight back, so an assertion can say so inside its own <c>return</c>:
    /// <c>return probe.Complete(this);</c>
    /// </summary>
    /// <typeparam name="T">The returned value's type.</typeparam>
    /// <param name="value">The value to return unchanged.</param>
    /// <returns><paramref name="value"/>.</returns>
    public T Complete<T>(T value)
    {
        _completed = true;
        return value;
    }

    /// <summary>
    /// Closes the probe and records the assertion's outcome. Safe to call more than once.
    /// </summary>
    public void Dispose()
    {
        if (_scope is null || _disposed) return;
        _disposed = true;

        // Only the outermost probe records, so an assertion built out of another one is a single
        // row in the report rather than two.
        if (!_scope.ExitProbe()) return;

        var reason = _inconclusiveReason;
        if (reason is null && !_completed)
        {
            reason = "the assertion did not run to completion — an exception escaped it before " +
                     "it could report an outcome, so nothing was decided.";
        }

        _scope.CompleteProbe(_name, _failuresAtStart, reason);
    }
}
