// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Cli.Commands;
using Xunit;

namespace AgentEval.Tests.Cli.Classic;

/// <summary>
/// <c>agenteval list --type metrics</c> may not advertise a name <c>--metrics</c> then refuses. It used to print eight
/// such names (the tool-selection/argument metrics, the three embedding metrics, Recall@K, MRR and
/// ConversationCompleteness) as if they were selectable, and it omitted one name the catalog does resolve.
/// </summary>
[Collection("ConsoleTests")]
public class ListCommandMetricCatalogParityTests
{
    private static string Capture()
    {
        var original = Console.Out;
        using var sw = new StringWriter();
        Console.SetOut(sw);
        try { ListCommand.PrintMetrics(); }
        finally { Console.SetOut(original); }
        return sw.ToString();
    }

    [Fact]
    public void EveryResolvableName_IsListed()
    {
        var listed = ListCommand.MetricListing.Select(m => m.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Empty(MetricCatalog.AvailableNames.Where(n => !listed.Contains(n)));
    }

    [Fact]
    public void EveryListedName_ThatIsNotResolvable_IsMarkedLibraryOnly()
    {
        var lines = Capture().Split('\n');

        foreach (var (_, name, _) in ListCommand.MetricListing)
        {
            var line = Assert.Single(lines, l => l.TrimStart().StartsWith(name + " ", StringComparison.Ordinal));
            Assert.Equal(!MetricCatalog.IsKnown(name), line.Contains(ListCommand.LibraryOnlyMarker, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TheEightPreviouslyMisadvertisedNames_AreNowMarked()
    {
        var output = Capture();
        foreach (var name in new[] { "code_tool_selection", "code_tool_arguments", "embed_answer_similarity",
                     "embed_response_context", "embed_query_context", "code_recall_at_k", "code_mrr", "ConversationCompleteness" })
        {
            var line = output.Split('\n').Single(l => l.TrimStart().StartsWith(name + " ", StringComparison.Ordinal));
            Assert.Contains(ListCommand.LibraryOnlyMarker, line, StringComparison.Ordinal);
        }
    }
}
