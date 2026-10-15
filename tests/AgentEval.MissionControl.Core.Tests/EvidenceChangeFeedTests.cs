// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MissionControl.Core.Tests;

public sealed class EvidenceChangeFeedTests
{
    [Fact]
    public async Task Subscribe_ConnectChangeAndReconnect_RequiresResyncWithoutReplayingHistory()
    {
        using var feed = new EvidenceChangeFeed();
        using var subscription = feed.Subscribe()!;
        await using var reader = subscription.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync()); var initial = reader.Current;
        Assert.Equal(EvidenceChangeReason.Connected, initial.Reason);
        feed.Invalidate(); Assert.True(await reader.MoveNextAsync());
        Assert.Equal(initial.Epoch, reader.Current.Epoch); Assert.Equal(1, reader.Current.Sequence);
        Assert.Equal(EvidenceChangeReason.Changed, reader.Current.Reason);
        using var reconnect = feed.Subscribe()!;
        await using var next = reconnect.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await next.MoveNextAsync());
        Assert.Equal(EvidenceChangeReason.Connected, next.Current.Reason); Assert.Equal(1, next.Current.Sequence);
    }

    [Fact]
    public async Task Invalidate_SlowSubscriber_ReportsOverflowWithoutAffectingFastSubscriber()
    {
        using var feed = new EvidenceChangeFeed(capacity: 1);
        using var slow = feed.Subscribe()!; using var fast = feed.Subscribe()!;
        await using var fastReader = fast.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await fastReader.MoveNextAsync());
        for (var i = 1; i <= 100; i++)
        {
            feed.Invalidate(); Assert.True(await fastReader.MoveNextAsync());
            Assert.Equal(i, fastReader.Current.Sequence); Assert.Equal(EvidenceChangeReason.Changed, fastReader.Current.Reason);
        }
        await using var slowReader = slow.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await slowReader.MoveNextAsync());
        Assert.Equal(100, slowReader.Current.Sequence); Assert.Equal(EvidenceChangeReason.Overflow, slowReader.Current.Reason);
        feed.Invalidate(EvidenceChangeReason.Reconcile);
        Assert.True(await slowReader.MoveNextAsync()); Assert.Equal(EvidenceChangeReason.Reconcile, slowReader.Current.Reason);
    }

    [Fact]
    public async Task Subscription_CancelOrDispose_ReleasesCapacityAndEndsPendingRead()
    {
        using var feed = new EvidenceChangeFeed(maximumSubscribers: 1);
        using var first = feed.Subscribe()!; Assert.Null(feed.Subscribe());
        using var cancel = new CancellationTokenSource();
        await using var reader = first.ReadAllAsync(cancel.Token).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        var pending = reader.MoveNextAsync().AsTask(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        using var replacement = feed.Subscribe(); Assert.NotNull(replacement);
        await using var next = replacement.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await next.MoveNextAsync());
        var waiting = next.MoveNextAsync().AsTask(); replacement.Dispose();
        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Feed_Shutdown_CompletesReadersAndRejectsFurtherUse()
    {
        var feed = new EvidenceChangeFeed(); using var subscription = feed.Subscribe()!;
        await using var reader = subscription.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync()); var waiting = reader.MoveNextAsync().AsTask();
        feed.Dispose(); feed.Dispose();
        Assert.False(await waiting.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Throws<ObjectDisposedException>(() => feed.Subscribe());
        Assert.Throws<ObjectDisposedException>(() => feed.Invalidate());
    }

    [Fact]
    public async Task Subscription_SecondReader_RefusesCompetingConsumption()
    {
        using var feed = new EvidenceChangeFeed(); using var subscription = feed.Subscribe()!;
        await using var first = subscription.ReadAllAsync().GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());
        await using var second = subscription.ReadAllAsync().GetAsyncEnumerator();
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.MoveNextAsync().AsTask());
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(257, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1025)]
    public void Constructor_UnboundedOrInvalidLimits_Refuses(int subscribers, int capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvidenceChangeFeed(subscribers, capacity));
}
