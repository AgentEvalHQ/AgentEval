// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Xunit;

namespace AgentEval.Tests.Microsoft365.TestHelpers;

/// <summary>
/// <see cref="WaiterTrackingTimeProvider"/> reports exactly the timers that wait on it: armed by a delay, a timed
/// cancellation source or a raw timer; released when they fire, are cancelled, are disposed or are re-armed with an
/// infinite due time; the earliest due time is the next one.
/// </summary>
public sealed class WaiterTrackingTimeProviderTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Delay_ArmedUntilItFires()
    {
        var clock = new WaiterTrackingTimeProvider(Start);

        var delay = Task.Delay(TimeSpan.FromSeconds(2), clock);

        Assert.Equal(1, clock.ArmedTimers);
        Assert.Equal(Start + TimeSpan.FromSeconds(2), clock.NextDue);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(delay.IsCompleted);
        Assert.Equal(1, clock.ArmedTimers);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(delay.IsCompletedSuccessfully);
        Assert.Equal(0, clock.ArmedTimers);
        Assert.Null(clock.NextDue);
    }

    [Fact]
    public void Delay_CancelledBeforeItFires_Disarmed()
    {
        var clock = new WaiterTrackingTimeProvider(Start);
        using var cts = new CancellationTokenSource();
        var delay = Task.Delay(TimeSpan.FromSeconds(2), clock, cts.Token);
        Assert.Equal(1, clock.ArmedTimers);

        cts.Cancel();

        Assert.True(delay.IsCanceled);
        Assert.Equal(0, clock.ArmedTimers);
        Assert.Null(clock.NextDue);
    }

    [Fact]
    public void CancellationTokenSource_TimeoutArmedUntilItFires()
    {
        var clock = new WaiterTrackingTimeProvider(Start);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);

        Assert.Equal(1, clock.ArmedTimers);
        Assert.Equal(Start + TimeSpan.FromSeconds(5), clock.NextDue);

        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.True(cts.IsCancellationRequested);
        Assert.Equal(0, clock.ArmedTimers);
    }

    [Fact]
    public void CancellationTokenSource_DisposedBeforeTheTimeout_Disarmed()
    {
        var clock = new WaiterTrackingTimeProvider(Start);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
        Assert.Equal(1, clock.ArmedTimers);

        cts.Dispose();

        Assert.Equal(0, clock.ArmedTimers);
    }

    [Fact]
    public void NextDue_IsTheEarliestArmedTimer()
    {
        var clock = new WaiterTrackingTimeProvider(Start);
        var later = Task.Delay(TimeSpan.FromSeconds(7), clock);
        using var sooner = new CancellationTokenSource(TimeSpan.FromSeconds(3), clock);

        Assert.Equal(2, clock.ArmedTimers);
        Assert.Equal(Start + TimeSpan.FromSeconds(3), clock.NextDue);

        clock.Advance(TimeSpan.FromSeconds(3));

        Assert.True(sooner.IsCancellationRequested);
        Assert.False(later.IsCompleted);
        Assert.Equal(1, clock.ArmedTimers);
        Assert.Equal(Start + TimeSpan.FromSeconds(7), clock.NextDue);
    }

    [Fact]
    public void PeriodicTimer_StaysArmedAndMovesOnByItsPeriod()
    {
        var clock = new WaiterTrackingTimeProvider(Start);
        var ticks = 0;
        using var timer = clock.CreateTimer(_ => ticks++, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        Assert.Equal(1, clock.ArmedTimers);
        Assert.Equal(Start + TimeSpan.FromSeconds(1), clock.NextDue);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, ticks);
        Assert.Equal(1, clock.ArmedTimers);
        Assert.Equal(Start + TimeSpan.FromSeconds(2), clock.NextDue);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, ticks);

        timer.Dispose();
        Assert.Equal(0, clock.ArmedTimers);
    }

    [Fact]
    public void Timer_InfiniteDueTime_NotArmedUntilChanged()
    {
        var clock = new WaiterTrackingTimeProvider(Start);
        var ticks = 0;
        using var timer = clock.CreateTimer(_ => ticks++, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Assert.Equal(0, clock.ArmedTimers);

        Assert.True(timer.Change(TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan));
        Assert.Equal(1, clock.ArmedTimers);
        Assert.Equal(Start + TimeSpan.FromSeconds(1), clock.NextDue);

        Assert.True(timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan));
        Assert.Equal(0, clock.ArmedTimers);

        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(0, ticks);
    }

    [Fact]
    public void Timer_ZeroDueTime_FiresAndIsNotLeftArmed()
    {
        var clock = new WaiterTrackingTimeProvider(Start);
        var ticks = 0;
        using var timer = clock.CreateTimer(_ => ticks++, null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);

        clock.Advance(TimeSpan.Zero);

        Assert.Equal(1, ticks);
        Assert.Equal(0, clock.ArmedTimers);
    }

    [Fact]
    public void Timer_CallbackReceivesTheState()
    {
        var clock = new WaiterTrackingTimeProvider(Start);
        object? seen = null;
        var state = new object();
        using var timer = clock.CreateTimer(s => seen = s, state, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Same(state, seen);
    }

    [Fact]
    public void Timer_ArmedInsideAnotherTimersCallback_IsSeen()
    {
        var clock = new WaiterTrackingTimeProvider(Start);
        Task? second = null;
        var first = Task.Delay(TimeSpan.FromSeconds(1), clock).ContinueWith(
            _ => second = Task.Delay(TimeSpan.FromSeconds(1), clock),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.True(first.IsCompletedSuccessfully);
        Assert.NotNull(second);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, clock.ArmedTimers);
        Assert.Equal(Start + TimeSpan.FromSeconds(2), clock.NextDue);
    }
}
