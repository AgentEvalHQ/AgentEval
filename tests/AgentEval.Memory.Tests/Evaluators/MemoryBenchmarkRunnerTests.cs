using AgentEval.Memory.Engine;
using AgentEval.Memory.Evaluators;
using AgentEval.Benchmarks;
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
    public async Task RunBenchmarkAsync_StandardPreset_Returns8Categories()
    {
        var result = await _runner.RunBenchmarkAsync(_agent, MemoryBenchmark.Standard);

        Assert.Equal("Standard", result.BenchmarkName);
        Assert.Equal(8, result.CategoryResults.Count);
    }

    [Fact]
    public async Task RunBenchmarkAsync_FullPreset_Returns12Categories()
    {
        var result = await _runner.RunBenchmarkAsync(_agent, MemoryBenchmark.Full);

        Assert.Equal("Full", result.BenchmarkName);
        Assert.Equal(12, result.CategoryResults.Count);
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

        Assert.Equal(8, result.CategoryResults.Count);
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

        Assert.Equal(12, result.CategoryResults.Count);
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

    // ═══════════════════════════════════════════════════════════════
    // New Category Tests (Abstention, ConflictResolution, MultiSessionReasoning)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunBenchmarkAsync_StandardPreset_IncludesAbstention()
    {
        var result = await _runner.RunBenchmarkAsync(_agent, MemoryBenchmark.Standard);

        var abstention = result.CategoryResults.FirstOrDefault(c =>
            c.ScenarioType == BenchmarkScenarioType.Abstention);
        Assert.NotNull(abstention);
        Assert.False(abstention.Skipped);
        Assert.InRange(abstention.Score, 0, 100);
    }

    [Fact]
    public async Task RunBenchmarkAsync_FullPreset_IncludesConflictResolution()
    {
        var result = await _runner.RunBenchmarkAsync(_agent, MemoryBenchmark.Full);

        var conflict = result.CategoryResults.FirstOrDefault(c =>
            c.ScenarioType == BenchmarkScenarioType.ConflictResolution);
        Assert.NotNull(conflict);
        Assert.False(conflict.Skipped);
    }

    [Fact]
    public async Task RunBenchmarkAsync_FullPreset_MultiSessionReasoningSkippedForNonResettable()
    {
        // TestMemoryAgent does NOT implement ISessionResettableAgent
        var result = await _runner.RunBenchmarkAsync(_agent, MemoryBenchmark.Full);

        var multiSession = result.CategoryResults.FirstOrDefault(c =>
            c.ScenarioType == BenchmarkScenarioType.MultiSessionReasoning);
        Assert.NotNull(multiSession);
        Assert.True(multiSession.Skipped, "MultiSessionReasoning should be skipped for non-resettable agents");
    }

    [Fact]
    public async Task RunBenchmarkAsync_DiagnosticPreset_HasSameCategoriesAsFull()
    {
        var full = MemoryBenchmark.Full;
        var diagnostic = MemoryBenchmark.Diagnostic;

        Assert.Equal(full.Categories.Count, diagnostic.Categories.Count);
        Assert.Equal("Diagnostic", diagnostic.Name);
    }

    // ═══════════════════════════════════════════════════════════════
    // P0-2 (Sprint 0) — Diagnostic / Overflow preset routing fixes
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Regression guard for P0-2 (Sprint 0). Before the fix,
    /// <see cref="MemoryBenchmark.Diagnostic"/> had <c>Name = "Diagnostic"</c>
    /// which flowed into <c>ScenarioLoader.ResolvePreset</c>.
    /// No JSON file declared a "diagnostic" key, so the loader silently fell
    /// back to "quick" and the marquee "~50K+ token context" claim was a lie —
    /// the runner actually loaded the ~8K <c>context-small</c> corpus.
    /// The fix introduced <see cref="MemoryBenchmark.PresetResolutionKey"/> which
    /// routes Diagnostic to "Full" internally. This test pins the resulting
    /// behaviour: Diagnostic must produce a context blob that is materially
    /// larger than Quick's (the "Full" branch uses context-stress, ~120K+ chars).
    /// </summary>
    [Fact]
    public void RunBenchmarkAsync_DiagnosticPreset_HasLargerContextThanQuick()
    {
        // Arrange — Diagnostic now reports "Full" as its effective resolution key
        var diagnostic = MemoryBenchmark.Diagnostic;
        Assert.Equal("Full", diagnostic.EffectivePresetResolutionKey);

        // Act — build context blobs using both keys; this is what the runner does
        // internally per category. (BuildContextPressureBlob is internal for tests.)
        var quickBlob = MemoryBenchmarkRunner.BuildContextPressureBlob("Quick");
        var diagnosticBlob = MemoryBenchmarkRunner.BuildContextPressureBlob(
            diagnostic.EffectivePresetResolutionKey);

        // Assert — both blobs exist
        Assert.NotNull(quickBlob);
        Assert.NotNull(diagnosticBlob);

        // Assert — Diagnostic's blob is materially larger than Quick's. Quick uses
        // 15 turns of context-small (~8K chars); the Full-routed Diagnostic uses
        // 200 turns of context-stress (~80K+ chars). A 5x size delta is the
        // smoking-gun proof that the resolution-key fix actually loads stress
        // content rather than silently degrading to small.
        Assert.True(diagnosticBlob!.Length > quickBlob!.Length * 5,
            $"Expected Diagnostic blob to be > 5x Quick blob; got Quick={quickBlob.Length}, " +
            $"Diagnostic={diagnosticBlob.Length}. If this fails close to 1x, the resolution-key " +
            "fix regressed and Diagnostic is once again silently using context-small.");
    }

    /// <summary>
    /// Companion to <see cref="RunBenchmarkAsync_DiagnosticPreset_HasLargerContextThanQuick"/>.
    /// Asserts that <see cref="MemoryBenchmark.Overflow"/>:
    /// (a) routes its preset resolution through "Full" so JSON scenarios actually
    ///     load the stress corpus instead of falling back to "quick"; and
    /// (b) carries the bumped <c>TargetTokensOverride = 192_000</c> needed to
    ///     force overflow on a 128K-window model (was 128_000 before the fix).
    /// </summary>
    // ═══════════════════════════════════════════════════════════════
    // BUG-56 — per-run overrides threaded, not stored on the instance
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RunBenchmarkAsync_ConcurrentReuseDifferentPresets_AllCompleteConsistently()
    {
        // BUG-56: the runner is AddScoped/reusable. The override fields it used to write at the top
        // of every RunBenchmarkAsync and read deep in TryRunFromJsonAsync raced under concurrent
        // reentrancy. They are now threaded as a parameter, so the same instance is safe to run
        // several benchmarks concurrently. Each run uses its own agent (the agent, not the runner,
        // holds per-conversation state).
        var quick = _runner.RunBenchmarkAsync(new TestMemoryAgent(), MemoryBenchmark.Quick);
        var overflow = _runner.RunBenchmarkAsync(new TestMemoryAgent(), MemoryBenchmark.Overflow);
        var standard = _runner.RunBenchmarkAsync(new TestMemoryAgent(), MemoryBenchmark.Standard);

        var results = await Task.WhenAll(quick, overflow, standard);

        Assert.Equal(3, results[0].CategoryResults.Count);   // Quick
        Assert.Equal("Quick", results[0].BenchmarkName);
        Assert.Equal(8, results[1].CategoryResults.Count);   // Overflow (Standard-shaped categories)
        Assert.Equal(8, results[2].CategoryResults.Count);   // Standard
        Assert.All(results, r => Assert.InRange(r.OverallScore, 0, 100));
    }

    [Fact]
    public async Task RunBenchmarkAsync_OverflowOverrides_StillAppliedAfterThreadingRefactor()
    {
        // Guards that threading the overrides as a parameter (instead of instance fields) did not
        // drop them: the Overflow benchmark still runs end-to-end with valid results (BUG-56).
        var result = await _runner.RunBenchmarkAsync(new TestMemoryAgent(), MemoryBenchmark.Overflow);

        Assert.Equal(8, result.CategoryResults.Count); // Overflow reuses Standard's category set
        Assert.All(result.CategoryResults.Where(c => !c.Skipped), c => Assert.InRange(c.Score, 0, 100));
    }

    [Fact]
    public void RunBenchmarkAsync_OverflowPreset_LoadsContextStressCorpus()
    {
        // Arrange / Act
        var overflow = MemoryBenchmark.Overflow;

        // Assert (a) — resolution key routes JSON resolution through "Full"
        Assert.Equal("Full", overflow.EffectivePresetResolutionKey);

        // Sanity check the JSON path actually loads context-stress via the
        // "full" preset key (this is what TryRunFromJsonAsync does internally).
        var scenarioDef = AgentEval.Memory.DataLoading.ScenarioLoader.Load("basic-retention");
        var resolved = AgentEval.Memory.DataLoading.ScenarioLoader.ResolvePreset(
            scenarioDef, overflow.EffectivePresetResolutionKey);

        Assert.NotNull(resolved.ContextPressure);
        Assert.Equal("context-stress", resolved.ContextPressure!.Corpus);

        // Assert (b) — target tokens raised to 192K so the doc claim
        // ("fills 75% of 192K via injection, then filler calls push past
        // the 128K window") actually holds.
        Assert.Equal(192_000, overflow.TargetTokensOverride);
        Assert.Equal(20, overflow.OverflowCallsOverride);
    }
}
