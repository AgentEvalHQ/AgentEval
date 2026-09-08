// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using AgentEval.Output;

namespace AgentEval.Assertions;

/// <summary>
/// What a scope does with the assertions it collects when it is disposed.
/// </summary>
/// <remarks>
/// The two consumers of the fluent assertions want opposite things and both are legitimate:
/// an xUnit test wants a failure to <b>throw</b>; an eval run wants every outcome
/// <b>collected</b> into a report it can render and gate on. The mode picks which.
/// <see cref="AgentEvalScopeMode.Throw"/> is the default so existing behaviour is unchanged.
/// </remarks>
public enum AgentEvalScopeMode
{
    /// <summary>
    /// Collect failures and throw a single <see cref="AgentEvalScopeException"/> on dispose
    /// if there were any. This is the default and the pre-existing behaviour.
    /// </summary>
    Throw = 0,

    /// <summary>
    /// Collect every outcome and throw nothing. Read the run's verdict from
    /// <see cref="AgentEvalScope.Results"/> after the scope is disposed.
    /// </summary>
    Collect = 1
}

/// <summary>
/// Collects multiple assertion failures within a scope and throws a single exception
/// containing all failures when disposed. Similar to FluentAssertions' AssertionScope.
/// </summary>
/// <remarks>
/// <para>
/// A scope also records what <i>held</i>, not only what failed: assertions report a pass, a
/// failure or an "could not decide" through <see cref="BeginAssertion"/> / <see cref="RecordPass"/>
/// / <see cref="RecordInconclusive"/>, and the whole set is readable as
/// <see cref="AssertionResult"/> values via <see cref="Results"/> — without needing an exception.
/// </para>
/// <para>
/// Set <see cref="AgentEvalScopeMode.Collect"/> (via <see cref="Collecting"/>) for an eval run
/// that must not throw. The default stays <see cref="AgentEvalScopeMode.Throw"/>.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // xUnit — unchanged: throws one exception listing every failure.
/// using (new AgentEvalScope())
/// {
///     report.Should().HaveCalledTool("SearchTool");
///     report.Should().HaveCalledTool("CalculateTool");
///     response.Should().Contain("result");
/// }
///
/// // Eval run — never throws; hands back a report.
/// using var scope = AgentEvalScope.Collecting("weather agent");
/// report.Should().HaveCalledTool("SearchTool");
/// scope.Dispose();
/// foreach (var r in scope.Results) Console.WriteLine($"{r.Outcome}: {r.Assertion}");
/// </code>
/// </example>
public sealed class AgentEvalScope : IDisposable
{
    /// <summary>
    /// Name given to a failure that reached the scope through <see cref="FailWith(AgentEvalAssertionException)"/>
    /// without an enclosing <see cref="BeginAssertion"/>, so no assertion name is known. Such
    /// failures are still reported — a report that silently drops a failure is worse than an
    /// unnamed one.
    /// </summary>
    public const string UnattributedAssertionName = "(unattributed assertion)";

    // _current uses AsyncLocal (not [ThreadStatic]) so a scope created before an await is still
    // visible to assertions that run on the continuation thread. With [ThreadStatic] such an
    // assertion saw no scope and threw immediately — a gotcha for an async framework (MNT-07).
    // Within a synchronous using-block the behaviour is identical to thread-local.
    private static readonly AsyncLocal<AgentEvalScope?> _currentScope = new();

    private static AgentEvalScope? _current
    {
        get => _currentScope.Value;
        set => _currentScope.Value = value;
    }

    private readonly AgentEvalScope? _parent;
    private readonly List<AgentEvalAssertionException> _failures = new();
    private readonly List<AssertionResult> _results = new();
    private readonly object _gate = new();
    private readonly string? _context;
    private bool _disposed;

    // How many entries of _failures have already been turned into a named AssertionResult by a
    // probe. Anything past this index is reported under UnattributedAssertionName.
    private int _claimedFailures;

    // Nesting depth of BeginAssertion probes. Only the outermost probe records a result, so an
    // assertion implemented in terms of another one yields a single row, not two.
    private int _probeDepth;

    /// <summary>
    /// Gets the current active scope, if any.
    /// </summary>
    public static AgentEvalScope? Current => _current;

    /// <summary>
    /// Creates a new assertion scope. All assertion failures within this scope
    /// will be collected and thrown as a single exception when the scope is disposed.
    /// </summary>
    /// <param name="context">Optional context description for the scope.</param>
    public AgentEvalScope(string? context = null)
        : this(AgentEvalScopeMode.Throw, context)
    {
    }

    /// <summary>
    /// Creates a new assertion scope with an explicit <paramref name="mode"/>.
    /// </summary>
    /// <param name="mode">
    /// <see cref="AgentEvalScopeMode.Throw"/> (the default elsewhere) to throw an aggregate
    /// exception on dispose; <see cref="AgentEvalScopeMode.Collect"/> to throw nothing and report
    /// through <see cref="Results"/>.
    /// </param>
    /// <param name="context">Optional context description for the scope.</param>
    public AgentEvalScope(AgentEvalScopeMode mode, string? context = null)
    {
        Mode = mode;
        _context = context;
        _parent = _current;
        _current = this;
    }

    /// <summary>
    /// Opens a scope that <b>never throws</b> — the mode an eval run wants. Failures, passes and
    /// undecidable checks are all collected; read them from <see cref="Results"/>.
    /// </summary>
    /// <param name="context">Optional context description for the scope.</param>
    /// <returns>A scope in <see cref="AgentEvalScopeMode.Collect"/> mode. Dispose it (a
    /// <c>using</c> block is fine) and then read <see cref="Results"/>.</returns>
    public static AgentEvalScope Collecting(string? context = null)
        => new(AgentEvalScopeMode.Collect, context);

    /// <summary>Gets what this scope does on dispose.</summary>
    public AgentEvalScopeMode Mode { get; }

    /// <summary>Gets the context description this scope was created with, if any.</summary>
    public string? Context => _context;

    /// <summary>
    /// Gets whether this scope has collected any failures. Undecidable results are
    /// <b>not</b> failures and do not set this.
    /// </summary>
    public bool HasFailures => _failures.Count > 0;

    /// <summary>
    /// Gets the number of failures collected in this scope. Undecidable results are not counted.
    /// </summary>
    public int FailureCount => _failures.Count;

    /// <summary>
    /// Gets all failures collected in this scope.
    /// </summary>
    public IReadOnlyList<AgentEvalAssertionException> Failures => _failures.AsReadOnly();

    /// <summary>
    /// Gets every assertion outcome recorded in this scope — passes, failures and undecidable
    /// checks alike — as a reportable projection. Readable before <i>and</i> after
    /// <see cref="Dispose"/>; no exception is needed to get results out.
    /// </summary>
    /// <remarks>
    /// Failures raised through <see cref="FailWith(AgentEvalAssertionException)"/> outside any
    /// <see cref="BeginAssertion"/> still appear, named
    /// <see cref="UnattributedAssertionName"/> and appended after the attributed results.
    /// </remarks>
    public IReadOnlyList<AssertionResult> Results
    {
        get
        {
            lock (_gate)
            {
                if (_claimedFailures >= _failures.Count)
                {
                    return _results.ToArray();
                }

                var projected = new List<AssertionResult>(_results.Count + (_failures.Count - _claimedFailures));
                projected.AddRange(_results);
                for (var i = _claimedFailures; i < _failures.Count; i++)
                {
                    projected.Add(AssertionResult.Fail(UnattributedAssertionName, _failures[i].Message));
                }

                return projected;
            }
        }
    }

    /// <summary>Gets the number of assertions that were decidable and held.</summary>
    public int PassedCount => Results.Count(r => r.Outcome == AssertionOutcome.Passed);

    /// <summary>
    /// Gets the number of assertions that ran but could not decide. A non-zero value here means
    /// part of the report is not evidence — treat it as coverage lost, never as green.
    /// </summary>
    public int InconclusiveCount => Results.Count(r => r.Outcome == AssertionOutcome.Inconclusive);

    /// <summary>
    /// Records an assertion failure. If no scope is active, throws immediately.
    /// If a scope is active, collects the failure for later.
    /// </summary>
    /// <param name="exception">The assertion exception to record.</param>
    /// <returns>True if the failure was collected (scope active), false if thrown immediately.</returns>
    internal static bool RecordFailure(AgentEvalAssertionException exception)
    {
        var scope = _current;
        if (scope != null)
        {
            lock (scope._gate)
            {
                scope._failures.Add(exception);
            }

            return true;
        }
        return false;
    }

    /// <summary>
    /// Throws an assertion exception, or records it if within a scope.
    /// </summary>
    /// <param name="exception">The assertion exception.</param>
    [StackTraceHidden]
    public static void FailWith(AgentEvalAssertionException exception)
    {
        if (!RecordFailure(exception))
        {
            throw exception;
        }
    }

    /// <summary>
    /// Creates a tool assertion exception and throws/records it.
    /// </summary>
    [StackTraceHidden]
    public static void FailWith(
        string message,
        string? toolName = null,
        IReadOnlyList<string>? calledTools = null,
        string? expected = null,
        string? actual = null,
        string? context = null,
        IReadOnlyList<string>? suggestions = null,
        string? because = null)
    {
        var exception = ToolAssertionException.Create(
            message, toolName, calledTools, expected, actual, context, suggestions, because);
        FailWith(exception);
    }

    // ─── Recording what held, not only what failed ───────────────────────────

    /// <summary>
    /// Opens a probe around one assertion so its outcome — pass, failure, or undecidable — is
    /// recorded, not only its failures. Call it as the first statement of an assertion method:
    /// <c>using var probe = AgentEvalScope.BeginAssertion(toolName);</c>
    /// </summary>
    /// <param name="subject">
    /// Optional subject the assertion is about (a tool name, a parameter name). When supplied the
    /// recorded name reads <c>MethodName(subject)</c>.
    /// </param>
    /// <param name="assertionName">
    /// The assertion's name. Defaults to the calling member's name — leave it to the compiler.
    /// </param>
    /// <returns>
    /// A probe to dispose at the end of the assertion. When no scope is active (the xUnit path)
    /// this is a shared no-op: nothing is allocated and nothing is recorded, because a failure
    /// will have thrown instead.
    /// </returns>
    /// <remarks>
    /// The probe decides the outcome by watching <see cref="FailureCount"/> across its own
    /// lifetime, so assertions need no change to their bodies or signatures beyond this one line.
    /// Nested probes (an assertion implemented in terms of another) record once, at the outermost.
    /// </remarks>
    public static AssertionProbe BeginAssertion(
        string? subject = null,
        [CallerMemberName] string assertionName = "")
    {
        var scope = _current;
        if (scope is null || scope._disposed)
        {
            return AssertionProbe.Inactive;
        }

        var name = string.IsNullOrEmpty(subject)
            ? assertionName
            : $"{assertionName}({subject})";

        return new AssertionProbe(scope, name);
    }

    /// <summary>
    /// Records an assertion that held. No-op when no scope is active.
    /// </summary>
    /// <param name="assertion">The assertion's name.</param>
    /// <param name="message">Optional detail.</param>
    public static void RecordPass(string assertion, string? message = null)
        => Record(AssertionResult.Pass(assertion, message));

    /// <summary>
    /// Records an assertion that ran but could not decide — the evidence it needs was not
    /// captured, or it is structurally unable to fail in this run. No-op when no scope is active.
    /// </summary>
    /// <param name="assertion">The assertion's name.</param>
    /// <param name="reason">Why it could not decide. Surfaced in the report.</param>
    public static void RecordInconclusive(string assertion, string reason)
        => Record(AssertionResult.Undecidable(assertion, reason));

    /// <summary>
    /// Records an already-built outcome. No-op when no scope is active.
    /// </summary>
    /// <param name="result">The outcome to record.</param>
    public static void Record(AssertionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var scope = _current;
        if (scope is null || scope._disposed) return;
        lock (scope._gate)
        {
            scope._results.Add(result);
        }
    }

    /// <summary>
    /// Materialises every failure that no probe attributed into <c>_results</c>, so the recorded
    /// sequence is complete on its own. Callers must already hold <c>_gate</c>.
    /// </summary>
    private void FlushUnattributedFailures()
    {
        for (var i = _claimedFailures; i < _failures.Count; i++)
        {
            _results.Add(AssertionResult.Fail(UnattributedAssertionName, _failures[i].Message));
        }

        _claimedFailures = _failures.Count;
    }

    internal void EnterProbe() => _probeDepth++;

    internal bool ExitProbe() => --_probeDepth <= 0;

    /// <summary>
    /// Turns one probe's span into recorded results: every failure raised inside it (attributed to
    /// the assertion's name), or an undecidable marker, or a pass.
    /// </summary>
    internal void CompleteProbe(string assertionName, int failuresAtStart, string? inconclusiveReason)
    {
        lock (_gate)
        {
            // Anything that failed before this probe opened and was never attributed still has to
            // reach the report; flush it in order so results and failures stay interleaved.
            for (var i = _claimedFailures; i < failuresAtStart && i < _failures.Count; i++)
            {
                _results.Add(AssertionResult.Fail(UnattributedAssertionName, _failures[i].Message));
            }

            if (_failures.Count > failuresAtStart)
            {
                for (var i = failuresAtStart; i < _failures.Count; i++)
                {
                    _results.Add(AssertionResult.Fail(assertionName, _failures[i].Message));
                }

                _claimedFailures = _failures.Count;
                return;
            }

            _claimedFailures = Math.Max(_claimedFailures, failuresAtStart);

            // A failure outranks an undecidable marker, which outranks a pass.
            _results.Add(inconclusiveReason is null
                ? AssertionResult.Pass(assertionName)
                : AssertionResult.Undecidable(assertionName, inconclusiveReason));
        }
    }

    /// <summary>
    /// Disposes the scope. In <see cref="AgentEvalScopeMode.Throw"/> (the default) this throws an
    /// <see cref="AgentEvalScopeException"/> if any failures were collected. In
    /// <see cref="AgentEvalScopeMode.Collect"/> it never throws — read <see cref="Results"/>.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _current = _parent;

        if (Mode == AgentEvalScopeMode.Collect)
        {
            // Hand results up so the outermost collecting scope holds the whole report.
            if (_parent is { _disposed: false, Mode: AgentEvalScopeMode.Collect })
            {
                var carried = Results;
                lock (_parent._gate)
                {
                    // The parent's own unattributed failures have to keep their place in the
                    // sequence, so materialise them before appending this child's rows.
                    _parent.FlushUnattributedFailures();

                    foreach (var result in carried)
                    {
                        _parent._results.Add(_context is null
                            ? result
                            : result with { Assertion = $"[{_context}] {result.Assertion}" });
                    }

                    // Carry the failures themselves too — a parent whose HasFailures said "no"
                    // while a child had failed would gate the run in the flattering direction.
                    // They are already represented in `carried`, hence claimed.
                    _parent._failures.AddRange(_failures);
                    _parent._claimedFailures = _parent._failures.Count;
                }
            }

            return;
        }

        if (_failures.Count > 0)
        {
            if (_context != null)
            {
                // Prefix each collected failure with the scope context.
                var wrappedFailures = _failures
                    .Select(f => new AgentEvalAssertionException($"[{_context}] {f.Message}"))
                    .ToList();

                throw new AgentEvalScopeException(wrappedFailures);
            }

            throw new AgentEvalScopeException(_failures);
        }
    }

    /// <summary>
    /// Clears all collected failures without throwing. Recorded results are cleared too — a
    /// report built from a cleared scope would otherwise still list the failures it discarded.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            _failures.Clear();
            _results.Clear();
            _claimedFailures = 0;
        }
    }

    /// <summary>
    /// Opens a <b>new child</b> assertion scope annotated with <paramref name="context"/> and makes
    /// it current. This does NOT annotate the existing scope — it returns a fresh scope that the
    /// caller MUST dispose (use it in a <c>using</c> block); failing to dispose it leaves the child
    /// registered as the current scope and never restores the parent (MNT-07).
    /// </summary>
    /// <remarks>The child inherits this scope's <see cref="Mode"/>, so a collecting scope cannot
    /// grow a throwing child.</remarks>
    /// <example>
    /// <code>
    /// using var outer = new AgentEvalScope();
    /// using (outer.WithContext("phase 1")) { /* assertions tagged [phase 1] */ }
    /// </code>
    /// </example>
    public AgentEvalScope WithContext(string context)
    {
        return new AgentEvalScope(Mode, context);
    }
}

/// <summary>
/// Extension methods for creating scopes with fluent syntax.
/// </summary>
public static class AgentEvalScopeExtensions
{
    /// <summary>
    /// Starts a new assertion scope with the given context.
    /// </summary>
    /// <example>
    /// <code>
    /// using (AgentEvalScope.Begin("Verifying weather agent"))
    /// {
    ///     // assertions...
    /// }
    /// </code>
    /// </example>
    public static AgentEvalScope Begin(string? context = null) => new AgentEvalScope(context);

    /// <summary>
    /// Starts a new <b>non-throwing</b> assertion scope with the given context — the eval-run mode.
    /// Equivalent to <see cref="AgentEvalScope.Collecting"/>.
    /// </summary>
    /// <example>
    /// <code>
    /// using var scope = AgentEvalScopeExtensions.BeginCollecting("Nightly eval");
    /// // assertions...
    /// scope.Dispose();
    /// var report = scope.Results;
    /// </code>
    /// </example>
    public static AgentEvalScope BeginCollecting(string? context = null)
        => AgentEvalScope.Collecting(context);
}
