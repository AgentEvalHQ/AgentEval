// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using AgentEval.RedTeam;
using Xunit;

namespace AgentEval.Tests.Docs;

/// <summary>
/// Every count of the built-in red-team roster in the docs is <see cref="Attack.All"/>'s count. The CLI reference
/// said "default all 13" after <c>SkillInjection</c> made it 14, understating what a default scan runs and costs.
/// </summary>
public class RedTeamAttackCountDocsTests
{
    private static readonly Regex s_count = new(
        @"default all (?<n>\d+)\b|(?<n>\d+) built-in (?:red-team )?attack|(?<n>\d+) attack types",
        RegexOptions.Compiled);

    [Theory]
    [InlineData("redteam.md")]
    [InlineData("benchmarks.md")]
    public void TheDocs_CountTheBuiltInAttacks_AsAttackAllDoes(string doc)
    {
        var text = File.ReadAllText(Path.Combine(LocateRepoRoot(), "docs", doc));

        var counts = s_count.Matches(text).Select(m => (Text: m.Value, N: int.Parse(m.Groups["n"].Value))).ToList();

        Assert.NotEmpty(counts);
        var wrong = counts.Where(c => c.N != Attack.All.Count).Select(c => $"\"{c.Text}\"").ToList();
        Assert.True(wrong.Count == 0,
            $"docs/{doc} counts the built-in attacks as other than {Attack.All.Count}: {string.Join(", ", wrong)}");
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentEval.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("AgentEval.sln not located.");
    }
}
