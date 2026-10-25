// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using AgentEval.Tests.Snapshots;
using Xunit;

namespace AgentEval.Tests.Abstractions;

/// <summary>
/// Abstractions assembly v1 API freeze: the complete public surface of <c>AgentEval.Abstractions</c>
/// is snapshotted. Any change to a public type or member fails this test until the new snapshot is
/// reviewed and accepted, so the surface cannot move silently. Each type is listed with abstract/sealed, its base
/// class and its public interfaces, inherited ones included; members include the protected ones a subclass can reach.
/// Types marked <c>[Experimental]</c> are listed with that marker and are preview, outside the v1 promise.
/// </summary>
public class AbstractionsPublicApiSnapshotTests
{
    private static readonly Assembly _asm = typeof(AgentEval.Calibration.ICalibratedJudge).Assembly;

    [Fact]
    public Task ThePublicSurface_MatchesTheApprovedSnapshot() =>
        Verify(PublicApiSurface.Render(_asm.GetExportedTypes(), t => t.Assembly == _asm));
}
