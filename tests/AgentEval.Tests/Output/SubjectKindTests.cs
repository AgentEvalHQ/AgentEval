// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;

namespace AgentEval.Tests.Output;

/// <summary>Tests for the SubjectKind enum and its extension methods.</summary>
public class SubjectKindTests
{
    [Theory]
    [InlineData(SubjectKind.Agent, "agents")]
    [InlineData(SubjectKind.Workflow, "workflows")]
    public void Folder_ReturnsExpectedString(SubjectKind kind, string expected)
        => Assert.Equal(expected, kind.Folder());
}
