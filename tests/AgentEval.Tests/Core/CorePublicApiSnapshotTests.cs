// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Tests.Snapshots;
using Xunit;

namespace AgentEval.Tests.Core;

/// <summary>
/// Core assembly v1 API freeze: the public surface of <c>AgentEval.Core</c> is snapshotted.
/// Any change to a public type or member fails this test until the new snapshot is reviewed and
/// accepted, so the surface cannot move silently. Each type is listed with abstract/sealed, its base class and its
/// public interfaces, inherited ones included; members include the protected ones a subclass can reach.
/// <para>
/// Note: <c>AgentEval.Guardrails*</c> namespaces are excluded here because they are already
/// frozen by <see cref="AgentEval.Tests.MAF.Gatekeeper.GatekeeperPublicApiSnapshotTests"/>.
/// </para>
/// Types marked <c>[Experimental]</c> are listed with that marker and are preview, outside the v1 promise.
/// </summary>
public class CorePublicApiSnapshotTests
{
    // Exclude Guardrails (covered by GatekeeperPublicApiSnapshotTests)
    private static bool InScope(Type t) =>
        t.Namespace is { } ns
        && !ns.Equals("AgentEval.Guardrails", StringComparison.Ordinal)
        && !ns.StartsWith("AgentEval.Guardrails.", StringComparison.Ordinal);

    [Fact]
    public Task ThePublicSurface_MatchesTheApprovedSnapshot()
    {
        var asm = typeof(AgentEval.Assertions.ResponseAssertions).Assembly;
        return Verify(PublicApiSurface.Render(asm.GetExportedTypes().Where(InScope), InScope));
    }
}
