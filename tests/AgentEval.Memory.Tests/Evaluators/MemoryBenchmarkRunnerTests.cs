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
    public async Task RunBenchmarkAsync_StandardPreset_Returns6Categories()
    {
        var result = await _runner.RunBenchmarkAsync(_agent, MemoryBenchmark.Standard);

        Assert.Equal("Standard", result.BenchmarkName);
        Assert.Equal(6, result.CategoryResults.Count);
    }

    [Fact]
    public async Task RunBenchmarkAsync_FullPreset_Returns8Categories()
    {
        var result = await _runner.RunBenchmarkAsync(_agent, MemoryBenchmark.Full);

        Assert.Equal("Full", result.BenchmarkName);
        Assert.Equal(8, result.CategoryResults.Count);
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
            Assert.True(cat.Duration >= TimeSpan.Zero);
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
}
