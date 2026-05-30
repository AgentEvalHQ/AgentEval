// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;
using Xunit;

namespace AgentEval.Tests.Cli;

/// <summary>
/// Tests for <see cref="RenderCommand"/>.
/// </summary>
public class RenderCommandTests
{
    [Theory]
    [InlineData("../../../escape")]
    [InlineData("..")]
    [InlineData("a/b")]
    public async Task RunAsync_TraversalTimestamp_RejectedAsUnsafePathSegment(string ts)
    {
        // SEC-05: --ts is joined into a filesystem path; a `..`/separator value would escape the
        // benchmark subtree. RenderCommand must validate it as a safe single segment (matching the
        // hardened ComplianceRenderCommand sibling) and reject before any path join.
        var originalErr = Console.Error;
        var captured = new StringWriter();
        int exit;
        try
        {
            Console.SetError(captured);
            exit = await RenderCommand.RunAsync("agentic", "subj", ts, rootOverride: null);
        }
        finally
        {
            Console.SetError(originalErr);
        }

        Assert.Equal(1, exit);
        Assert.Contains("not a safe path segment", captured.ToString());
    }
}
