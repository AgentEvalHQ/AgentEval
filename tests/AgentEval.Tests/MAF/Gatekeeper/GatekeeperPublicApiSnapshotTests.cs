// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using AgentEval.Tests.Snapshots;
using Xunit;

namespace AgentEval.Tests.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper v1 API freeze: the public surface of Gatekeeper (<c>AgentEval.MAF.Gatekeeper*</c>,
/// <c>AgentEval.Guardrails*</c>, and the AgentHooks adapter) is snapshotted. Any change to a public type or member
/// fails this test until the new snapshot is reviewed and accepted, so the surface cannot move silently.
/// Types marked <c>[Experimental]</c>, directly or by their assembly, are listed with that marker: they are preview,
/// outside the v1 promise. Parameter names and default values are part of the surface (renaming a parameter breaks
/// named-argument callers; changing a default changes compiled callers), so both are in the snapshot.
/// </summary>
public class GatekeeperPublicApiSnapshotTests
{
    private static readonly string[] Namespaces = ["AgentEval.MAF.Gatekeeper", "AgentEval.Guardrails", "AgentEval.MAF.AgentHooks"];

    internal static IEnumerable<Assembly> Assemblies() =>
    [
        typeof(AgentEval.MAF.Gatekeeper.IToolGate).Assembly,
        typeof(AgentEval.Guardrails.GateRegexTimeouts).Assembly,
        typeof(AgentEval.MAF.AgentHooks.GatekeeperInterceptor).Assembly,
    ];

    internal static bool InScope(Type t) =>
        t.Namespace is { } ns && Namespaces.Any(n => ns == n || ns.StartsWith(n + ".", StringComparison.Ordinal));

    [Fact]
    public Task ThePublicSurface_MatchesTheApprovedSnapshot() => Verify(RenderSurface());

    [Fact]
    public void RenderSurface_ConcreteGate_PreservesSealingAndPublicInterface()
    {
        var line = RenderSurface().Split('\n').Single(l => l.Contains("class AgentEval.MAF.Gatekeeper.SequenceGate", StringComparison.Ordinal));

        Assert.StartsWith("sealed class ", line, StringComparison.Ordinal);
        Assert.Contains("AgentEval.MAF.Gatekeeper.IToolGate", line, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderSurface_ExtensibleSessionGate_PreservesProtectedContract()
    {
        Assert.Contains("protected ValueTask<AgentEval.Guardrails.GateVerdict> CheckSessionAsync(",
            RenderSurface(), StringComparison.Ordinal);
    }

    private static string RenderSurface() => PublicApiSurface.Render(
        Assemblies().Distinct().SelectMany(assembly => assembly.GetExportedTypes()).Where(InScope), InScope);
}
