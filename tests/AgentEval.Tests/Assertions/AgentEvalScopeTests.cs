// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Assertions;
using Xunit;

namespace AgentEval.Tests.Assertions;

/// <summary>
/// Regression coverage for MNT-07: AgentEvalScope used a <c>[ThreadStatic]</c> current-scope, so an
/// assertion that ran on a continuation/pool thread after an await did not see the scope and threw
/// immediately. It now uses an AsyncLocal so the scope flows across awaits and onto pool threads.
/// </summary>
public class AgentEvalScopeTests
{
    [Fact]
    public async Task Scope_IsVisibleOnAnotherThread_SoFailureIsRecordedNotThrown()
    {
        using var scope = new AgentEvalScope();

        // Run the assertion on a guaranteed-different (pool) thread. With AsyncLocal the scope flows
        // via the captured ExecutionContext; with the old [ThreadStatic] it would be invisible here
        // and FailWith would throw immediately instead of recording a soft failure (MNT-07).
        await Task.Run(() =>
            AgentEvalScope.FailWith("soft failure raised on a pool thread"));

        Assert.True(scope.HasFailures);
        Assert.Equal(1, scope.FailureCount);
        scope.Clear(); // avoid throwing the aggregate exception on dispose
    }

    [Fact]
    public void Scope_SynchronousUsage_StillCollectsAndThrowsOnDispose()
    {
        // The common synchronous using-block path is unchanged by the AsyncLocal switch.
        Assert.Throws<AgentEvalScopeException>(() =>
        {
            using var scope = new AgentEvalScope();
            AgentEvalScope.FailWith("failure A");
            AgentEvalScope.FailWith("failure B");
            Assert.Equal(2, scope.FailureCount);
        });
    }

    [Fact]
    public void WithContext_OpensChildScope_TaggingFailuresWithContext()
    {
        using var outer = new AgentEvalScope();

        var ex = Assert.Throws<AgentEvalScopeException>(() =>
        {
            using var child = outer.WithContext("phase-1");
            AgentEvalScope.FailWith("inner failure");
        });

        Assert.Contains("phase-1", ex.Message);
    }
}
