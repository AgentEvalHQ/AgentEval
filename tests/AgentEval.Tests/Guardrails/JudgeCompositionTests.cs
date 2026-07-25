// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Guardrails;
using AgentEval.Guardrails.Judges;
using Xunit;

namespace AgentEval.Tests.Guardrails;

/// <summary>ParallelJudgeFanOut (the panel — fail-closed OR) and JudgeVerdictCache (precedent — allow-only cache).</summary>
public class JudgeCompositionTests
{
    private sealed class StubGate : IChatGate
    {
        private readonly GateVerdict _verdict;
        public int Calls;
        public string PolicyName { get; }
        public StubGate(GateVerdict verdict, string name = "stub") { _verdict = verdict; PolicyName = name; }
        public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            return new ValueTask<GateVerdict>(_verdict);
        }
    }

    private sealed class ThrowingGate : IChatGate
    {
        public string PolicyName => "throwing";
        public ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");
    }

    private sealed class DelayingGate : IChatGate
    {
        public string PolicyName => "delaying";
        public async ValueTask<GateVerdict> InspectAsync(string text, CancellationToken cancellationToken = default)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return GateVerdict.Allow(PolicyName);
        }
    }

    // ── ParallelJudgeFanOut ──

    [Fact]
    public async Task FanOut_AllAllow_Allows()
    {
        var panel = new ParallelJudgeFanOut([new StubGate(GateVerdict.Allow("a")), new StubGate(GateVerdict.Allow("b"))]);
        Assert.Equal(GateAction.Allow, (await panel.InspectAsync("x")).Action);
    }

    [Fact]
    public async Task FanOut_AnyBlock_Blocks_AggregatingEvidence()
    {
        var panel = new ParallelJudgeFanOut(
        [
            new StubGate(GateVerdict.Allow("clean")),
            new StubGate(GateVerdict.Block("pi", "injection found", ["do this instead"]), "pi"),
        ]);

        var v = await panel.InspectAsync("x");

        Assert.Equal(GateAction.Block, v.Action);
        Assert.Contains("pi", v.Reason!);
        Assert.Contains("do this instead", v.Matches!);
    }

    [Fact]
    public async Task FanOut_ThrowingJudge_IsTreatedAsBlock_FailClosed()
    {
        var panel = new ParallelJudgeFanOut([new StubGate(GateVerdict.Allow("ok")), new ThrowingGate()]);
        Assert.Equal(GateAction.Block, (await panel.InspectAsync("x")).Action);
    }

    [Fact]
    public async Task FanOut_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var panel = new ParallelJudgeFanOut([new DelayingGate()]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await panel.InspectAsync("x", cts.Token));
    }

    [Fact]
    public void FanOut_EmptyJudges_Throws()
        => Assert.Throws<ArgumentException>(() => new ParallelJudgeFanOut([]));

    // ── JudgeVerdictCache ──

    [Fact]
    public async Task Cache_IdenticalAllow_CallsInnerOnce()
    {
        var inner = new StubGate(GateVerdict.Allow("ok"));
        var cache = new JudgeVerdictCache(inner);

        await cache.InspectAsync("same input");
        await cache.InspectAsync("same input");

        Assert.Equal(1, inner.Calls);   // second hit served from cache
    }

    [Fact]
    public async Task Cache_Block_IsNotCached_ReEvaluatedEachTime()
    {
        // A transient/fail-closed block must never be cached into a permanent one.
        var inner = new StubGate(GateVerdict.Block("j", "nope"));
        var cache = new JudgeVerdictCache(inner);

        await cache.InspectAsync("same input");
        await cache.InspectAsync("same input");

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Cache_DifferentInputs_NotConflated()
    {
        var inner = new StubGate(GateVerdict.Allow("ok"));
        var cache = new JudgeVerdictCache(inner);

        await cache.InspectAsync("input one");
        await cache.InspectAsync("input two");

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public void Cache_NullInner_Throws()
        => Assert.Throws<ArgumentNullException>(() => new JudgeVerdictCache(null!));

    [Fact]
    public void Cache_ZeroMaxEntries_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new JudgeVerdictCache(new StubGate(GateVerdict.Allow("x")), 0));

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public async Task Cache_EmitsPrecedentEvents_HitCarriesOriginalTimestamp()
    {
        // P3-7: the cache surfaces hit/miss precedent events; a HIT reports when the served allow was FIRST
        // evaluated (its precedent age), so a caller can emit gate.cache.* evidence instead of a silent memoize.
        var clock = new FakeClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var events = new List<JudgeCacheEvent>();
        var inner = new StubGate(GateVerdict.Allow("j"), "judge:x");
        var cache = new JudgeVerdictCache(inner, onLookup: events.Add, timeProvider: clock);

        await cache.InspectAsync("same");                 // miss → evaluates, stamps the original timestamp
        clock.Advance(TimeSpan.FromMinutes(5));
        await cache.InspectAsync("same");                 // hit → served from cache

        Assert.Equal(1, inner.Calls);                     // the inner judge ran exactly once
        Assert.Equal(1, cache.Hits);
        Assert.Equal(1, cache.Misses);

        Assert.Equal(2, events.Count);
        Assert.False(events[0].Hit);
        Assert.Null(events[0].OriginalTimestampUtc);
        Assert.True(events[1].Hit);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), events[1].OriginalTimestampUtc);
    }

    [Fact]
    public async Task Cache_TtlExpiry_ReJudgesAfterLifetime()
    {
        // P5-4: a memoized allow expires after the TTL, so a later-improved judge gets to re-evaluate.
        var clock = new FakeClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var inner = new StubGate(GateVerdict.Allow("j"));
        var cache = new JudgeVerdictCache(inner, timeProvider: clock, ttl: TimeSpan.FromMinutes(10));

        await cache.InspectAsync("x");   // miss → cached
        await cache.InspectAsync("x");   // hit
        Assert.Equal(1, inner.Calls);

        clock.Advance(TimeSpan.FromMinutes(11));
        await cache.InspectAsync("x");   // expired → re-judged
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task Cache_Lru_EvictsLeastRecentlyUsed_WhenFull()
    {
        // P5-4: a full cache evicts the LRU entry (not "stop admitting"). Touching "a" spares it; "b" is evicted.
        var inner = new StubGate(GateVerdict.Allow("j"));
        var cache = new JudgeVerdictCache(inner, maxEntries: 2);

        await cache.InspectAsync("a");   // cache a       (inner calls: 1)
        await cache.InspectAsync("b");   // cache b, full (2)
        await cache.InspectAsync("a");   // hit → a is now MRU, b is LRU
        await cache.InspectAsync("c");   // cache c → evicts LRU (b) (3)
        Assert.Equal(2, cache.Count);

        var calls = inner.Calls;
        await cache.InspectAsync("a");   // still cached → hit
        Assert.Equal(calls, inner.Calls);
        await cache.InspectAsync("b");   // was evicted → miss (re-judged)
        Assert.Equal(calls + 1, inner.Calls);
    }

    [Fact]
    public async Task Cache_InvalidationKey_IsFoldedIntoTheCacheKey()
    {
        // P5-4: the same text under different invalidation keys hashes differently, so a judge/prompt/model change
        // never serves a stale precedent. (Each cache still memoizes correctly under its own key.)
        var innerV1 = new StubGate(GateVerdict.Allow("j"));
        var innerV2 = new StubGate(GateVerdict.Allow("j"));
        var v1 = new JudgeVerdictCache(innerV1, invalidationKey: "judge@v1");
        var v2 = new JudgeVerdictCache(innerV2, invalidationKey: "judge@v2");

        await v1.InspectAsync("same"); await v1.InspectAsync("same");   // v1: miss then hit
        await v2.InspectAsync("same");                                   // v2: independent miss

        Assert.Equal(1, innerV1.Calls);   // v1 memoized under its own key
        Assert.Equal(1, innerV2.Calls);   // v2 did not see v1's precedent
    }

    [Fact]
    public async Task Cache_ThrowingOnLookup_DoesNotTurnAllowIntoBlock()
    {
        // Phase 3 review #2: the precedent hook is pure observability — a throw must not propagate out of
        // InspectAsync (where the gate loop would fail-close it into a Block) and refuse a genuine cached Allow.
        var inner = new StubGate(GateVerdict.Allow("j"), "judge:x");
        var cache = new JudgeVerdictCache(inner, onLookup: _ => throw new InvalidOperationException("hook boom"));

        var miss = await cache.InspectAsync("same");   // miss — hook throws
        var hit = await cache.InspectAsync("same");    // hit — hook throws

        Assert.Equal(GateAction.Allow, miss.Action);
        Assert.Equal(GateAction.Allow, hit.Action);
        Assert.Equal(1, cache.Hits);
    }
}
