// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;

namespace AgentEval.Assertions;

/// <summary>
/// Collects multiple assertion failures within a scope and throws a single exception
/// containing all failures when disposed. Similar to FluentAssertions' AssertionScope.
/// </summary>
/// <example>
/// <code>
/// using (new AgentEvalScope())
/// {
///     report.Should().HaveCalledTool("SearchTool");
///     report.Should().HaveCalledTool("CalculateTool");
///     response.Should().Contain("result");
/// }
/// // Throws single exception with all failures
/// </code>
/// </example>
public sealed class AgentEvalScope : IDisposable
{
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
    private readonly string? _context;
    private bool _disposed;
    
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
    {
        _context = context;
        _parent = _current;
        _current = this;
    }
    
    /// <summary>
    /// Gets whether this scope has collected any failures.
    /// </summary>
    public bool HasFailures => _failures.Count > 0;
    
    /// <summary>
    /// Gets the number of failures collected in this scope.
    /// </summary>
    public int FailureCount => _failures.Count;
    
    /// <summary>
    /// Gets all failures collected in this scope.
    /// </summary>
    public IReadOnlyList<AgentEvalAssertionException> Failures => _failures.AsReadOnly();
    
    /// <summary>
    /// Records an assertion failure. If no scope is active, throws immediately.
    /// If a scope is active, collects the failure for later.
    /// </summary>
    /// <param name="exception">The assertion exception to record.</param>
    /// <returns>True if the failure was collected (scope active), false if thrown immediately.</returns>
    internal static bool RecordFailure(AgentEvalAssertionException exception)
    {
        if (_current != null)
        {
            _current._failures.Add(exception);
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
    
    /// <summary>
    /// Disposes the scope and throws if any failures were collected.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        _current = _parent;
        
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
    /// Clears all collected failures without throwing.
    /// </summary>
    public void Clear()
    {
        _failures.Clear();
    }
    
    /// <summary>
    /// Opens a <b>new child</b> assertion scope annotated with <paramref name="context"/> and makes
    /// it current. This does NOT annotate the existing scope — it returns a fresh scope that the
    /// caller MUST dispose (use it in a <c>using</c> block); failing to dispose it leaves the child
    /// registered as the current scope and never restores the parent (MNT-07).
    /// </summary>
    /// <example>
    /// <code>
    /// using var outer = new AgentEvalScope();
    /// using (outer.WithContext("phase 1")) { /* assertions tagged [phase 1] */ }
    /// </code>
    /// </example>
    public AgentEvalScope WithContext(string context)
    {
        return new AgentEvalScope(context);
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
}
