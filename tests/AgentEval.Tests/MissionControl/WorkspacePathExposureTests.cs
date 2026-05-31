// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MissionControl;
using Xunit;

namespace AgentEval.Tests.MissionControl;

/// <summary>
/// SEC-12: the absolute workspace filesystem path is exposed (via /api/v1/version and the GraphQL
/// Workspace resolver) ONLY in Mode A (loopback, single operator). Both surfaces gate on
/// <see cref="McHost.IsModeA(string?)"/>, so these tests pin that decision.
/// </summary>
public class WorkspacePathExposureTests
{
    [Theory]
    [InlineData(null)]      // unset → default Mode A
    [InlineData("")]        // blank → default Mode A
    [InlineData("   ")]
    [InlineData("local")]   // canonical Mode A
    [InlineData("LOCAL")]   // case-insensitive
    public void IsModeA_LocalOrUnset_AllowsPathExposure(string? mode)
    {
        Assert.True(McHost.IsModeA(mode));
    }

    [Theory]
    [InlineData("aggregator")] // Mode B
    [InlineData("server")]     // Mode C
    [InlineData("Server")]
    [InlineData("multi-tenant")]
    public void IsModeA_NonLocal_RedactsPath(string mode)
    {
        // Anything that is not Mode A must redact the absolute workspace path.
        Assert.False(McHost.IsModeA(mode));
    }
}
