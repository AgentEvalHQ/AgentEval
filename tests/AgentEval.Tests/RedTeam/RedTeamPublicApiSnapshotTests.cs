// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using AgentEval.Tests.Snapshots;
using Xunit;

namespace AgentEval.Tests.RedTeam;

/// <summary>
/// Red-team API freeze: the complete public surface of the red-team assemblies (<c>AgentEval.RedTeam</c>, which also
/// holds its benchmarks and DI registration, and <c>AgentEval.RedTeam.Gatekeeper</c>) is snapshotted. The README
/// promises red team as Stable at 1.0, so any change to a public type or member fails this test until the new
/// snapshot is reviewed and accepted, and the surface cannot move silently.
/// Types marked <c>[Experimental]</c> are listed with that marker and are preview, outside the 1.0 promise.
/// </summary>
public class RedTeamPublicApiSnapshotTests
{
    private static readonly Assembly[] _assemblies =
    [
        typeof(AgentEval.RedTeam.RedTeamResult).Assembly,
        typeof(AgentEval.RedTeam.Gatekeeper.CanaryToolGate).Assembly,
    ];

    [Fact]
    public Task ThePublicSurface_MatchesTheApprovedSnapshot() =>
        Verify(PublicApiSurface.Render(_assemblies.SelectMany(a => a.GetExportedTypes()), t => _assemblies.Contains(t.Assembly)));
}
