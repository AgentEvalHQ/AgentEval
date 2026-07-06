// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.MAF.Evaluators;
using Xunit;
using static AgentEval.Tests.MAF.Evaluators.HybridEvalTestHelpers;

namespace AgentEval.Tests.MAF.Evaluators;

/// <summary>
/// Unit coverage for <see cref="CompositeAgentEvaluator"/>: concurrent fan-out, per-source failure
/// isolation, per-inner timeout, caller-cancellation propagation, source-prefixed merge, and the
/// circuit breaker. All in-memory via <c>FakeEvaluator</c> — no LLM / no live Foundry.
/// </summary>
public class CompositeAgentEvaluatorTests
{
    [Fact]
    public async Task Concurrency_InnersStartBeforeEitherCompletes()
    {
        // Deterministic (no wall-clock): both inner bodies signal "started" then block on a shared gate.
        // If the composite ran them sequentially, the second would never start and WhenAll would hang.
        var aStarted = new TaskCompletionSource();
        var bStarted = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        FakeEvaluator Gated(string s, TaskCompletionSource started) => new(s, async (items, _) =>
        {
            started.SetResult();
            await release.Task;
            return Pass(items, s);
        });

        var comp = new CompositeAgentEvaluator([("a", Gated("a", aStarted), null), ("b", Gated("b", bStarted), null)]);
        var eval = comp.EvaluateAsync(Items(1), "t", CancellationToken.None);

        await Task.WhenAll(aStarted.Task, bStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));   // both started
        Assert.False(eval.IsCompleted);          // neither finished — both blocked on the gate -> concurrent
        release.SetResult();
        await eval;
        Assert.Equal(2, comp.CapturedPerSource.Count);
    }

    [Fact]
    public async Task Isolation_ThrowingInner_OtherSurvives()
    {
        var comp = new CompositeAgentEvaluator([("local", Fast("local"), null), ("foundry", Throws("foundry"), null)]);

        var merged = await comp.EvaluateAsync(Items(2), "t", CancellationToken.None);   // must NOT throw

        Assert.Equal(2, comp.CapturedPerSource.Count);
        Assert.NotNull(comp.CapturedPerSource.Single(s => s.Source == "foundry").Result.Error);   // skipped
        Assert.Null(comp.CapturedPerSource.Single(s => s.Source == "local").Result.Error);        // survived
        Assert.Contains(merged.Items[0].Metrics.Keys, k => k.StartsWith("local:", StringComparison.Ordinal));
        Assert.Contains(merged.Items[0].Metrics.Keys, k => k.StartsWith("foundry:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Timeout_PerInnerCeiling_MarksSkipped_OtherSurvives()
    {
        // The slow work is deliberately far longer than the ceiling (a wide window) so a thread-starved CI runner
        // still fires the 50 ms ceiling before the work could finish — the ceiling cancels the work either way, so
        // a long delay does not slow the happy path.
        var comp = new CompositeAgentEvaluator(
            [("fast", Fast("fast"), null), ("slow", Slow("slow", 20000), TimeSpan.FromMilliseconds(50))]);

        await comp.EvaluateAsync(Items(1), "t", CancellationToken.None);

        Assert.NotNull(comp.CapturedPerSource.Single(s => s.Source == "slow").Result.Error);   // timed out -> skipped
        Assert.Null(comp.CapturedPerSource.Single(s => s.Source == "fast").Result.Error);      // survived
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var comp = new CompositeAgentEvaluator([("slow", Slow("slow", 200), null)]);

        // Genuine caller cancellation must bubble, NOT be swallowed as a skipped branch.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => comp.EvaluateAsync(Items(1), "t", cts.Token));
    }

    [Fact]
    public async Task Merge_PrefixesKeys_NoCollision()
    {
        // Both sources emit a metric named "relevance"; the source prefix keeps them distinct.
        var comp = new CompositeAgentEvaluator([("local", Fast("local"), null), ("foundry", Fast("foundry"), null)]);

        var merged = await comp.EvaluateAsync(Items(1), "t", CancellationToken.None);

        Assert.Contains("local:relevance", merged.Items[0].Metrics.Keys);
        Assert.Contains("foundry:relevance", merged.Items[0].Metrics.Keys);
    }

    [Fact]
    public async Task Breaker_OpensAfterThreshold_SkipsFast_ThenReopensAfterCooldown()
    {
        var now = DateTimeOffset.UtcNow;
        var breaker = new CircuitBreaker(threshold: 2, cooldown: TimeSpan.FromMinutes(1), now: () => now);
        var foundry = Throws("foundry");
        var comp = new CompositeAgentEvaluator([("foundry", foundry, null)], breaker: breaker);

        await comp.EvaluateAsync(Items(1), "t");   // 1: invoke -> fail  (failures=1)
        await comp.EvaluateAsync(Items(1), "t");   // 2: invoke -> fail  (failures=2 -> open)
        await comp.EvaluateAsync(Items(1), "t");   // 3: breaker OPEN -> skip fast (no invoke)
        Assert.Equal(2, foundry.Calls);

        now = now.AddMinutes(2);                    // cooldown elapsed
        await comp.EvaluateAsync(Items(1), "t");   // 4: breaker closed -> invoke again
        Assert.Equal(3, foundry.Calls);
    }
}
