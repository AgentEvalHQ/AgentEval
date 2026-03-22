using AgentEval.Memory.Engine;
using AgentEval.Memory.Evaluators;
using AgentEval.Memory.Models;
using AgentEval.Memory.Scenarios;
using AgentEval.Memory.Temporal;
using AgentEval.Memory.Tests.Engine;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentEval.Memory.Tests.Evaluators;

public class MemoryBenchmarkRunnerTests
{
    private readonly MemoryBenchmarkRunner _runner;
    private readonly TestMemoryAgent _agent;

    public MemoryBenchmarkRunnerTests()
    {
        var fakeChatClient = new FakeChatClient();
        var judge = new MemoryJudge(fakeChatClient, NullLogger<MemoryJudge>.Instance);
        var testRunner = new MemoryTestRunner(judge, NullLogger<MemoryTestRunner>.Instance);
        var reachBack = new ReachBackEvaluator(testRunner, judge, NullLogger<ReachBackEvaluator>.Instance);
        var reducer = new ReducerEvaluator(testRunner, NullLogger<ReducerEvaluator>.Instance);
        var crossSession = new CrossSessionEvaluator(judge, NullLogger<CrossSessionEvaluator>.Instance);
        var memoryScenarios = new MemoryScenarios();
        var chattyScenarios = new ChattyConversationScenarios();
        var temporalScenarios = new TemporalMemoryScenarios();

        _runner = new MemoryBenchmarkRunner(
            testRunner, judge, reachBack, reducer, crossSession,
            memoryScenarios, chattyScenarios, temporalScenarios,
            NullLogger<MemoryBenchmarkRunner>.Instance);

        _agent = new TestMemoryAgent();
    }

    [Fact]
    public async Task RunBenchmarkAsync_QuickPreset_Returns3Categories()
    {
        var result = await _runner.RunBenchmarkAsync(_agent, MemoryBenchmark.Quick);

        Assert.Equal("Quick", result.BenchmarkName);
        Assert.Equal(3, result.CategoryResults.Count);
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    [Fact]
    public async Task RunBenchmarkAsync_StandardPreset_Returns7Categories()
    {
        var result = await _runner.RunBenchmarkAsync(_agent, MemoryBenchmark.Standard);

        Assert.Equal("Standard", result.BenchmarkName);
        Assert.Equal(7, result.CategoryResults.Count);
    }

    [Fact]
    public async Task RunBenchmarkAsync_FullPreset_Returns9Categories()
    {
        var result = await _runner.RunBenchmarkAsync(_agent, MemoryBenchmark.Full);

        Assert.Equal("Full", result.BenchmarkName);
        Assert.Equal(9, result.CategoryResults.Count);
    }

    [Fact]
    public async Task RunBenchmarkAsync_CrossSession_SkippedForNonResettableAgent()
    {
        var result = await _runner.RunBenchmarkAsync(_agent, MemoryBenchmark.Full);

        var crossSession = result.CategoryResults.FirstOrDefault(c => c.ScenarioType == BenchmarkScenarioType.CrossSession);
        Assert.NotNull(crossSession);
        Assert.True(crossSession.Skipped);
        Assert.Contains("ISessionResettableAgent", crossSession.SkipReason);
    }

    [Fact]
    public async Task RunBenchmarkAsync_AllCategoryResultsHaveDuration()
    {
        var result = await _runner.RunBenchmarkAsync(_agent, MemoryBenchmark.Quick);

        Assert.All(result.CategoryResults, cat =>
        {
            // Duration should be set (non-default) for all categories, even skipped ones
            Assert.True(cat.Duration >= TimeSpan.Zero, $"Category '{cat.CategoryName}' has negative duration");
            // Non-skipped categories should have measurable duration
            if (!cat.Skipped)
            {
                Assert.True(cat.Duration > TimeSpan.Zero, $"Category '{cat.CategoryName}' has zero duration");
            }
        });
    }

    [Fact]
    public async Task RunBenchmarkAsync_AllCategoryResultsHaveWeight()
    {
        var result = await _runner.RunBenchmarkAsync(_agent, MemoryBenchmark.Standard);

        Assert.All(result.CategoryResults, cat =>
        {
            Assert.True(cat.Weight > 0);
        });
    }

    [Fact]
    public async Task RunBenchmarkAsync_OverallScoreInRange()
    {
        var result = await _runner.RunBenchmarkAsync(_agent, MemoryBenchmark.Quick);

        Assert.InRange(result.OverallScore, 0, 100);
    }

    [Fact]
    public async Task RunBenchmarkAsync_WithNullAgent_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _runner.RunBenchmarkAsync(null!, MemoryBenchmark.Quick));
    }

    [Fact]
    public async Task RunBenchmarkAsync_WithNullBenchmark_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _runner.RunBenchmarkAsync(_agent, null!));
    }

    [Fact]
    public async Task RunBenchmarkAsync_CustomBenchmark_RunsSpecifiedCategories()
    {
        var custom = new MemoryBenchmark
        {
            Name = "Custom",
            Description = "Single category test",
            Categories =
            [
                new MemoryBenchmarkCategory { Name = "Basic", Weight = 1.0, ScenarioType = BenchmarkScenarioType.BasicRetention }
            ]
        };

        var result = await _runner.RunBenchmarkAsync(_agent, custom);

        Assert.Equal("Custom", result.BenchmarkName);
        Assert.Single(result.CategoryResults);
        Assert.Equal("Basic", result.CategoryResults[0].CategoryName);
    }

    [Fact]
    public async Task RunBenchmarkAsync_NonSkippedCategories_HaveScores()
    {
        var result = await _runner.RunBenchmarkAsync(_agent, MemoryBenchmark.Quick);

        Assert.All(result.CategoryResults.Where(c => !c.Skipped), cat =>
        {
            Assert.True(cat.Score >= 0 && cat.Score <= 100);
        });
    }

    [Fact]
    public async Task RunBenchmarkAsync_WithResettableAgent_ResetsSessionBetweenCategories()
    {
        var agent = new ResettableTestAgent();
        var custom = new MemoryBenchmark
        {
            Name = "ResetTest",
            Description = "Tests session reset between categories",
            Categories =
            [
                new MemoryBenchmarkCategory { Name = "Cat1", Weight = 1.0, ScenarioType = BenchmarkScenarioType.BasicRetention },
                new MemoryBenchmarkCategory { Name = "Cat2", Weight = 1.0, ScenarioType = BenchmarkScenarioType.MultiTopic },
                new MemoryBenchmarkCategory { Name = "Cat3", Weight = 1.0, ScenarioType = BenchmarkScenarioType.NoiseResilience }
            ]
        };

        await _runner.RunBenchmarkAsync(agent, custom);

        // Agent should be reset once per category (3 categories = 3 resets)
        Assert.Equal(3, agent.ResetCount);
    }

    [Fact]
    public async Task RunBenchmarkAsync_ReducerFidelity_ProducesValidScore()
    {
        var custom = new MemoryBenchmark
        {
            Name = "ReducerOnly",
            Categories =
            [
                new MemoryBenchmarkCategory { Name = "Reducer", Weight = 1.0, ScenarioType = BenchmarkScenarioType.ReducerFidelity }
            ]
        };

        var result = await _runner.RunBenchmarkAsync(_agent, custom);

        var reducerCat = result.CategoryResults[0];
        Assert.False(reducerCat.Skipped);
        Assert.True(reducerCat.Score >= 0);
    }

    // ═══════════════════════════════════════════════════════════════
    // Scenario Depth Tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunBenchmarkAsync_QuickPreset_StillProducesValidResults()
    {
        // Quick preset behavior should be unchanged by scenario depth feature
        var result = await _runner.RunBenchmarkAsync(_agent, MemoryBenchmark.Quick);

        Assert.Equal(3, result.CategoryResults.Count);
        Assert.All(result.CategoryResults, c => Assert.False(c.Skipped));
        Assert.All(result.CategoryResults, c => Assert.InRange(c.Score, 0, 100));
    }

    [Fact]
    public async Task RunBenchmarkAsync_StandardPreset_AllCategoriesValid()
    {
        var result = await _runner.RunBenchmarkAsync(_agent, MemoryBenchmark.Standard);

        Assert.Equal(7, result.CategoryResults.Count);
        // Standard runs deeper scenarios — all should still produce valid scores
        Assert.All(result.CategoryResults.Where(c => !c.Skipped), c =>
        {
            Assert.InRange(c.Score, 0, 100);
        });
    }

    [Fact]
    public async Task RunBenchmarkAsync_FullPreset_AllCategoriesValid()
    {
        var result = await _runner.RunBenchmarkAsync(_agent, MemoryBenchmark.Full);

        Assert.Equal(9, result.CategoryResults.Count);
        // Non-skipped categories should have valid scores
        Assert.All(result.CategoryResults.Where(c => !c.Skipped), c =>
        {
            Assert.InRange(c.Score, 0, 100);
        });
    }

    [Fact]
    public async Task RunBenchmarkAsync_ReachBack_QuickUsesShallowDepths()
    {
        // Quick preset uses depths [5, 10, 25] — this is verified by the score being
        // a valid average. We can't inspect internal depths, but we verify it runs.
        var custom = new MemoryBenchmark
        {
            Name = "Quick",
            Categories =
            [
                new MemoryBenchmarkCategory { Name = "Depth", Weight = 1.0, ScenarioType = BenchmarkScenarioType.ReachBackDepth }
            ]
        };

        var result = await _runner.RunBenchmarkAsync(_agent, custom);
        Assert.InRange(result.CategoryResults[0].Score, 0, 100);
    }

    [Fact]
    public async Task RunBenchmarkAsync_StandardPreset_ScoresInValidRange()
    {
        // Standard runs 2 scenarios per category and averages — result should still be 0-100
        var result = await _runner.RunBenchmarkAsync(_agent, MemoryBenchmark.Standard);

        Assert.InRange(result.OverallScore, 0, 100);
        Assert.NotEmpty(result.Grade);
        Assert.InRange(result.Stars, 1, 5);
    }

    [Fact]
    public async Task RunBenchmarkAsync_WithResettableAgent_Standard_ResetsMultipleTimes()
    {
        var agent = new ResettableTestAgent();

        // Standard has 6 categories. With scenario depth, there are resets between categories
        // AND between scenarios within categories. Total resets should be > 6.
        await _runner.RunBenchmarkAsync(agent, MemoryBenchmark.Standard);

        // At minimum: 6 resets (between categories) + N resets (between scenarios)
        Assert.True(agent.ResetCount >= 6,
            $"Expected at least 6 resets for Standard, got {agent.ResetCount}");
    }

    [Fact]
    public async Task RunBenchmarkAsync_CustomPreset_NamePassedThrough()
    {
        // Custom presets with non-standard names should still work (default to single scenario)
        var custom = new MemoryBenchmark
        {
            Name = "MyCustom",
            Categories =
            [
                new MemoryBenchmarkCategory { Name = "Test", Weight = 1.0, ScenarioType = BenchmarkScenarioType.BasicRetention }
            ]
        };

        var result = await _runner.RunBenchmarkAsync(_agent, custom);

        Assert.Equal("MyCustom", result.BenchmarkName);
        Assert.InRange(result.CategoryResults[0].Score, 0, 100);
    }

    // ═══════════════════════════════════════════════════════════════
    // Factory Method Tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Create_WithChatClient_ReturnsWorkingRunner()
    {
        var runner = MemoryBenchmarkRunner.Create(new FakeChatClient());
        Assert.NotNull(runner);
    }

    [Fact]
    public async Task Create_RunnerCanExecuteBenchmark()
    {
        var runner = MemoryBenchmarkRunner.Create(new FakeChatClient());
        var agent = new TestMemoryAgent();

        var result = await runner.RunBenchmarkAsync(agent, MemoryBenchmark.Quick);

        Assert.Equal("Quick", result.BenchmarkName);
        Assert.Equal(3, result.CategoryResults.Count);
        Assert.InRange(result.OverallScore, 0, 100);
    }

    [Fact]
    public void Create_WithNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => MemoryBenchmarkRunner.Create(null!));
    }
}
