// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Microsoft365.Auth;
using AgentEval.Microsoft365.Readiness;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using static AgentEval.Tests.Microsoft365.Readiness.ReadinessTestRig;

namespace AgentEval.Tests.Microsoft365.Readiness;

/// <summary>
/// The readiness check's stage timers run on the injected clock, the same clock its deadlines are computed on: a test
/// advances a fake clock and the stage stops at once, with no real-time wait. The checks on the token are synchronous
/// (a fake clock fires its timers inside <see cref="FakeTimeProvider.Advance"/>), so nothing races the wall clock.
/// </summary>
public sealed class ReadinessClockTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);
    private static readonly HashSet<ReadinessStage> ConfigAndAuth = [ReadinessStage.Config, ReadinessStage.Auth];

    [Fact]
    public async Task StageTimeout_FiresOnTheInjectedClock()
    {
        using var rig = new ReadinessTestRig();
        using var stop = new CancellationTokenSource();
        var clock = new FakeTimeProvider(Start);
        var tokens = new WaitingTokenProvider();
        var options = rig.Options(stages: ConfigAndAuth) with { StageTimeout = TimeSpan.FromMinutes(10) };

        var run = rig.RunAsync(options, clock, tokens, stop.Token);
        try
        {
            var token = await tokens.Called.WaitAsync(HangGuard);
            clock.Advance(TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1));
            Assert.False(token.IsCancellationRequested);

            clock.Advance(TimeSpan.FromSeconds(2));
            Assert.True(token.IsCancellationRequested);

            var report = await run.WaitAsync(HangGuard);
            var auth = report.Find(ReadinessStage.Auth)!;
            Assert.Equal(ReadinessResult.Fail, auth.Result);
            Assert.Contains("no token within 600 s", auth.Summary, StringComparison.Ordinal);
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAny(run, Task.Delay(HangGuard));
        }
    }

    [Fact]
    public async Task SignInPrompt_ExtendsTheStageToTheCodeExpiry_OnTheInjectedClock()
    {
        using var rig = new ReadinessTestRig();
        using var stop = new CancellationTokenSource();
        var clock = new FakeTimeProvider(Start);
        var tokens = new WaitingTokenProvider { Clock = clock, PromptExpiresIn = TimeSpan.FromMinutes(15) };
        var options = rig.Options(stages: ConfigAndAuth) with { StageTimeout = TimeSpan.FromSeconds(30) };

        var run = rig.RunAsync(options, clock, tokens, stop.Token);
        try
        {
            var token = await tokens.Called.WaitAsync(HangGuard);

            // Past the stage timeout but before the code expires: the sign-in still has its whole lifetime.
            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.False(token.IsCancellationRequested);

            // Past the code's expiry plus the grace: the stage stops.
            clock.Advance(TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(11));
            Assert.True(token.IsCancellationRequested);

            var report = await run.WaitAsync(HangGuard);
            var auth = report.Find(ReadinessStage.Auth)!;
            Assert.Equal(ReadinessResult.Fail, auth.Result);
            Assert.Contains("the device code expired at 09:15:00 UTC", auth.Summary, StringComparison.Ordinal);
            Assert.Contains("doctor.sign_in_wait", Codes(report.TraceFile));
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAny(run, Task.Delay(HangGuard));
        }
    }

    [Fact]
    public async Task ReadyEvent_HasTheShapeOfTheLiveAgentsReadyEvent()
    {
        using var rig = new ReadinessTestRig();
        rig.WithHealthyEndpoint();

        var report = await rig.RunAsync(rig.Options(stages: new HashSet<ReadinessStage> { ReadinessStage.Config }));

        var first = ReadLines(report.TraceFile)[0];
        using var document = JsonDocument.Parse(first);
        var root = document.RootElement;
        Assert.Equal("config.diagnostics_ready", root.GetProperty("code").GetString());
        var data = root.GetProperty("data");
        Assert.Equal("Options", data.GetProperty("directorySource").GetString());
        Assert.Equal(report.TraceFile, data.GetProperty("traceFile").GetString());
        Assert.Equal(report.IssueFile, data.GetProperty("issueFile").GetString());
        Assert.Equal("1.0", data.GetProperty("a2aVersion").GetString());
        var message = root.GetProperty("message").GetString()!;
        Assert.Contains("m365 doctor run " + report.RunId, message, StringComparison.Ordinal);
        Assert.Contains("the readiness report; neither contains a token", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A token provider that never answers: it records the token it was asked with and waits until that token stops it.
    /// With <see cref="PromptExpiresIn"/> it first reports a device-code prompt that expires that long after the clock's now.
    /// </summary>
    private sealed class WaitingTokenProvider : IWorkIqTokenProvider, IWorkIqSignInWaitSource
    {
        private readonly TaskCompletionSource<CancellationToken> _called = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TimeProvider? Clock { get; init; }

        public TimeSpan? PromptExpiresIn { get; init; }

        public Action<DateTimeOffset>? SignInPrompted { get; set; }

        /// <summary>Completes with the token of the first request.</summary>
        public Task<CancellationToken> Called => _called.Task;

        public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            if (PromptExpiresIn is { } expiresIn && Clock is { } clock)
            {
                SignInPrompted?.Invoke(clock.GetUtcNow() + expiresIn);
            }

            _called.TrySetResult(cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return "never";
        }
    }
}
