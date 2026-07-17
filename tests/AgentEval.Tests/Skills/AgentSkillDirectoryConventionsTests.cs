// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Skills;
using Xunit;

namespace AgentEval.Tests.Skills;

/// <summary>Agent Skills Wave 1 (§4.3) — the repo-wide discovery seed list.</summary>
public class AgentSkillDirectoryConventionsTests
{
    [Fact]
    public void KnownConventions_IsNonEmpty()
        => Assert.NotEmpty(AgentSkillDirectoryConventions.KnownConventions);

    [Fact]
    public void KnownConventions_IncludesTheAgentSkillsIoUniversalPath()
        => Assert.Contains(".agents/skills", AgentSkillDirectoryConventions.KnownConventions);

    [Fact]
    public void KnownConventions_IncludesClaudeSkills()
        => Assert.Contains(".claude/skills", AgentSkillDirectoryConventions.KnownConventions);

    [Fact]
    public void KnownConventions_HasNoDuplicates()
    {
        var distinct = AgentSkillDirectoryConventions.KnownConventions.Distinct(StringComparer.Ordinal).Count();
        Assert.Equal(AgentSkillDirectoryConventions.KnownConventions.Count, distinct);
    }
}
