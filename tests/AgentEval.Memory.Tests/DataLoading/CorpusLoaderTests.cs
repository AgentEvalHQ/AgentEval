// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.DataLoading;
using Xunit;

namespace AgentEval.Memory.Tests.DataLoading;

public class CorpusLoaderTests
{
    [Fact]
    public void Load_ContextSmall_Returns15Turns()
    {
        var turns = CorpusLoader.Load("context-small");
        Assert.Equal(15, turns.Count);
    }

    [Fact]
    public void Load_ContextMedium_ReturnsExpectedTurns()
    {
        var turns = CorpusLoader.Load("context-medium");
        Assert.True(turns.Count >= 40, $"Medium corpus should have at least 40 turns, got {turns.Count}");
    }

    [Fact]
    public void Load_WithMaxTurns_ReturnsLimitedCount()
    {
        var turns = CorpusLoader.Load("context-small", maxTurns: 5);
        Assert.Equal(5, turns.Count);
    }

    [Fact]
    public void Load_TurnsHaveContent()
    {
        var turns = CorpusLoader.Load("context-small");
        Assert.All(turns, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.UserMessage));
            Assert.False(string.IsNullOrWhiteSpace(t.AssistantResponse));
        });
    }

    [Fact]
    public void Load_NoOverlapWithBenchmarkFacts()
    {
        // Corpus content must NOT mention key benchmark facts
        // Use distinctive multi-word phrases to avoid false positives
        // (e.g., "max" appears in cooking contexts but "golden retriever named Max" doesn't)
        var benchmarkPhrases = new[] { "José", "peanut allergy", "EpiPen", "golden retriever", "from Barcelona" };

        var smallTurns = CorpusLoader.Load("context-small");
        var mediumTurns = CorpusLoader.Load("context-medium");

        var allContent = smallTurns.Concat(mediumTurns)
            .SelectMany(t => new[] { t.UserMessage, t.AssistantResponse });

        foreach (var content in allContent)
        {
            foreach (var phrase in benchmarkPhrases)
            {
                Assert.DoesNotContain(phrase, content, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Load_NonExistentCorpus_ThrowsFileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(() => CorpusLoader.Load("non-existent-corpus"));
    }

    [Fact]
    public void ListAvailable_ReturnsCorpusNames()
    {
        var available = CorpusLoader.ListAvailable();
        Assert.True(available.Count >= 2);
    }

    [Fact]
    public void Load_MediumIsSupersetOfSmallTopics()
    {
        // Medium should have more diverse content than small
        var small = CorpusLoader.Load("context-small");
        var medium = CorpusLoader.Load("context-medium");

        Assert.True(medium.Count > small.Count);
    }
}
