// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365.Diagnostics;

namespace AgentEval.Microsoft365Demo.Tests;

/// <summary>
/// The demo tells the tester who can fix an issue and what to do, from its category. Every category the provider can
/// register has its own text, so a new category never falls through to "not classified".
/// </summary>
public class IssueTextTests
{
    public static TheoryData<WorkIqIssueCategory> ClassifiedCategories()
    {
        var data = new TheoryData<WorkIqIssueCategory>();
        foreach (var category in Enum.GetValues<WorkIqIssueCategory>())
        {
            if (category != WorkIqIssueCategory.Unknown)
                data.Add(category);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ClassifiedCategories))]
    public void EveryClassifiedCategory_HasItsOwnWhoAndAction(WorkIqIssueCategory category)
    {
        Assert.NotEqual(IssueText.WhoFixes(WorkIqIssueCategory.Unknown), IssueText.WhoFixes(category));
        Assert.NotEqual(IssueText.ActionFor(WorkIqIssueCategory.Unknown), IssueText.ActionFor(category));
    }

    [Fact]
    public void APausedCase_PointsAtTheCase_NotAtTheService()
    {
        Assert.Contains("case", IssueText.WhoFixes(WorkIqIssueCategory.Case), StringComparison.Ordinal);
        Assert.Contains("Rephrase", IssueText.ActionFor(WorkIqIssueCategory.Case), StringComparison.Ordinal);
    }
}
