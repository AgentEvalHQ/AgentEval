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

    // ═══════════════════════════════════════════════════════════════
    // LoadToTargetTokens — bounded repetition (PERF-07)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void LoadToTargetTokens_ModestTarget_RepeatsToReachTarget()
    {
        var baseTurns = CorpusLoader.Load("context-small");
        // A target a few times the base size should produce more than one copy.
        var baseChars = baseTurns.Sum(t => t.UserMessage.Length + t.AssistantResponse.Length);
        var result = CorpusLoader.LoadToTargetTokens("context-small", targetTokens: (baseChars / 4) * 3);

        Assert.True(result.Count > baseTurns.Count);
        Assert.Equal(0, result.Count % baseTurns.Count); // whole-corpus repeats
    }

    [Fact]
    public void LoadToTargetTokens_HugeTargetTinyMaxRepeats_IsCappedNotUnbounded()
    {
        var baseTurns = CorpusLoader.Load("context-small");

        // A target large enough to overflow int if multiplied out — must be bounded by maxRepeats,
        // NOT allocate an astronomically large list (PERF-07).
        var result = CorpusLoader.LoadToTargetTokens("context-small", targetTokens: int.MaxValue, maxRepeats: 3);

        Assert.Equal(baseTurns.Count * 3, result.Count);
    }

    [Fact]
    public void LoadToTargetTokens_DefaultCap_BoundsResult()
    {
        var baseTurns = CorpusLoader.Load("context-small");
        var result = CorpusLoader.LoadToTargetTokens("context-small", targetTokens: int.MaxValue);

        // Capped at the default maximum (never the runaway int.MaxValue / tinyTokens repeats).
        Assert.Equal(baseTurns.Count * CorpusLoader.DefaultMaxRepeats, result.Count);
    }

    [Fact]
    public void LoadToTargetTokens_NegativeTarget_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CorpusLoader.LoadToTargetTokens("context-small", targetTokens: -1));
    }

    [Fact]
    public void Load_MediumIsSupersetOfSmallTopics()
    {
        // Medium should have more diverse content than small
        var small = CorpusLoader.Load("context-small");
        var medium = CorpusLoader.Load("context-medium");

        Assert.True(medium.Count > small.Count);
    }

    // ═══════════════════════════════════════════════════════════════
    // Large and Stress corpus tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Load_ContextLarge_Returns100Turns()
    {
        var turns = CorpusLoader.Load("context-large");
        Assert.Equal(100, turns.Count);
    }

    [Fact]
    public void Load_ContextStress_Returns250Turns()
    {
        var turns = CorpusLoader.Load("context-stress");
        Assert.Equal(250, turns.Count);
    }

    [Fact]
    public void Load_ContextLarge_TurnsHaveContent()
    {
        var turns = CorpusLoader.Load("context-large");
        Assert.All(turns, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.UserMessage));
            Assert.False(string.IsNullOrWhiteSpace(t.AssistantResponse));
        });
    }

    [Fact]
    public void Load_ContextStress_TurnsHaveContent()
    {
        var turns = CorpusLoader.Load("context-stress");
        Assert.All(turns, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.UserMessage));
            Assert.False(string.IsNullOrWhiteSpace(t.AssistantResponse));
        });
    }

    [Fact]
    public void Load_ContextLarge_NoOverlapWithBenchmarkFacts()
    {
        var benchmarkPhrases = new[] { "José", "peanut allergy", "EpiPen", "golden retriever", "from Barcelona" };
        var turns = CorpusLoader.Load("context-large");
        var allContent = turns.SelectMany(t => new[] { t.UserMessage, t.AssistantResponse });

        foreach (var content in allContent)
        {
            foreach (var phrase in benchmarkPhrases)
            {
                Assert.DoesNotContain(phrase, content, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void Load_GraduatedSizes_AreOrdered()
    {
        var small = CorpusLoader.Load("context-small");
        var medium = CorpusLoader.Load("context-medium");
        var large = CorpusLoader.Load("context-large");
        var stress = CorpusLoader.Load("context-stress");

        Assert.True(small.Count < medium.Count, "small < medium");
        Assert.True(medium.Count < large.Count, "medium < large");
        Assert.True(large.Count < stress.Count, "large < stress");
    }

    [Fact]
    public void Load_WithMaxTurns_GreaterThanAvailable_ReturnsAll()
    {
        var turns = CorpusLoader.Load("context-small", maxTurns: 9999);
        Assert.Equal(15, turns.Count); // Only 15 available
    }

    [Fact]
    public void ListAvailable_IncludesAllFourCorpora()
    {
        var available = CorpusLoader.ListAvailable();
        // ListAvailable returns resource names (may include namespace prefix)
        Assert.Contains(available, n => n.Contains("context-small"));
        Assert.Contains(available, n => n.Contains("context-medium"));
        Assert.Contains(available, n => n.Contains("context-large"));
        Assert.Contains(available, n => n.Contains("context-stress"));
    }
}
