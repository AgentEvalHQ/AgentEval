// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Tests.Microsoft365.TestHelpers;
using Xunit;

namespace AgentEval.Tests.Microsoft365.Protocol;

/// <summary>
/// <see cref="WorkIqClientHarness.PumpAsync{T}"/> moves the clock only while the task waits on it — exactly to the
/// next due time — and waits real time while the task is between clock waits, so a continuation that is still
/// travelling through the thread pool never sees the clock run ahead of it. Both budgets fail loudly.
/// </summary>
public sealed class WorkIqClientHarnessTests
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task PumpAsync_AdvancesTheClockOnlyWhileTheTaskWaitsOnIt()
    {
        var clock = new WaiterTrackingTimeProvider(WorkIqClientHarness.Start);

        async Task<DateTimeOffset> Work()
        {
            await Task.Delay(20); // real: a thread-pool hop the pump must not run ahead of
            await Task.Delay(TimeSpan.FromSeconds(2), clock);
            await Task.Delay(20);
            await Task.Delay(TimeSpan.FromMilliseconds(2500), clock);
            await Task.Delay(20);
            return clock.GetUtcNow();
        }

        var finishedAt = await WorkIqClientHarness.PumpAsync(Work(), clock, Step);

        Assert.Equal(WorkIqClientHarness.Start + TimeSpan.FromMilliseconds(4500), finishedAt);
        Assert.Equal(WorkIqClientHarness.Start + TimeSpan.FromMilliseconds(4500), clock.GetUtcNow());
        Assert.Equal(0, clock.ArmedTimers);
    }

    [Fact]
    public async Task PumpAsync_FarDeadlineNextToAShortWait_FiresTheShortWaitFirst()
    {
        var clock = new WaiterTrackingTimeProvider(WorkIqClientHarness.Start);

        async Task<bool> Work()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
            await Task.Delay(TimeSpan.FromSeconds(3), clock, CancellationToken.None);
            return deadline.IsCancellationRequested;
        }

        var deadlineHit = await WorkIqClientHarness.PumpAsync(Work(), clock, Step);

        Assert.False(deadlineHit);
        Assert.Equal(WorkIqClientHarness.Start + TimeSpan.FromSeconds(3), clock.GetUtcNow());
    }

    [Fact]
    public async Task PumpAsync_TaskNeverWaitsOnTheClock_ThrowsAfterTheIdleBudget()
    {
        var clock = new WaiterTrackingTimeProvider(WorkIqClientHarness.Start);
        var never = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            WorkIqClientHarness.PumpAsync(never.Task, clock, Step, idleTimeout: TimeSpan.FromMilliseconds(200)));

        Assert.Contains("neither completed nor waited on the clock", ex.Message, StringComparison.Ordinal);
        Assert.Equal(WorkIqClientHarness.Start, clock.GetUtcNow());
    }

    [Fact]
    public async Task PumpAsync_StepBudgetExhausted_Throws()
    {
        var clock = new WaiterTrackingTimeProvider(WorkIqClientHarness.Start);
        var farAway = Task.Delay(TimeSpan.FromHours(1), clock);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            WorkIqClientHarness.PumpAsync(farAway, clock, Step, maxSteps: 10));

        Assert.Contains("did not complete after 10 clock steps", ex.Message, StringComparison.Ordinal);
        Assert.Equal(WorkIqClientHarness.Start + TimeSpan.FromSeconds(10), clock.GetUtcNow());
    }

    [Fact]
    public async Task PumpAsync_FaultedTask_PropagatesTheException()
    {
        var clock = new WaiterTrackingTimeProvider(WorkIqClientHarness.Start);

        async Task<int> Work()
        {
            await Task.Delay(TimeSpan.FromSeconds(1), clock);
            throw new InvalidOperationException("after the wait");
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => WorkIqClientHarness.PumpAsync(Work(), clock, Step));

        Assert.Equal("after the wait", ex.Message);
    }

    [Fact]
    public async Task PumpAsync_CompletedTask_ReturnsWithoutTouchingTheClock()
    {
        var clock = new WaiterTrackingTimeProvider(WorkIqClientHarness.Start);

        var value = await WorkIqClientHarness.PumpAsync(Task.FromResult(42), clock, Step);

        Assert.Equal(42, value);
        Assert.Equal(WorkIqClientHarness.Start, clock.GetUtcNow());
    }
}
