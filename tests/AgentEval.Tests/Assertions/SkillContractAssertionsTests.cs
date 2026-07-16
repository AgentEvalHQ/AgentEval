// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Assertions;
using AgentEval.Skills;
using Xunit;

namespace AgentEval.Tests.Assertions;

public class SkillContractAssertionsTests
{
    private static SkillManifest WellFormed() =>
        new("expense-report", "Summarizes expense reports.", [], [], null, [], SkillSourceKind.File, "expense-report");

    [Fact]
    public void AssertSkillWellFormed_CleanManifest_DoesNotThrow()
    {
        var exception = Record.Exception(() => SkillContractAssertions.AssertSkillWellFormed(WellFormed()));
        Assert.Null(exception);
    }

    [Fact]
    public void AssertSkillWellFormed_MissingName_Throws()
    {
        var bad = WellFormed() with { Name = "" };
        var exception = Assert.Throws<AgentEvalAssertionException>(() => SkillContractAssertions.AssertSkillWellFormed(bad));
        Assert.Contains("NameMissing", exception.Message);
    }

    [Fact]
    public void AssertSkillWellFormed_MediumOnlyFinding_DoesNotThrow()
    {
        // AllowedTools/CompatibilityTooLong are Low/Medium, not High — must not fail this zero-cost check.
        var manifest = WellFormed() with { CompatibilityLength = 999 };
        var exception = Record.Exception(() => SkillContractAssertions.AssertSkillWellFormed(manifest));
        Assert.Null(exception);
    }

    [Fact]
    public void AssertSkillWellFormed_NullManifest_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SkillContractAssertions.AssertSkillWellFormed(null!));
    }
}
