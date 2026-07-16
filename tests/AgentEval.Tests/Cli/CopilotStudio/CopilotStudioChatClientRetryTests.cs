// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using AgentEval.Cli.CopilotStudio;
using AgentEval.Core;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Tests.Cli.CopilotStudio;

/// <summary>
/// P6 item B (<c>strategy/CopilotStudio/Copilot-Studio-P6-Connector-Health-and-Resilience-Design.md</c> §1B):
/// <c>CopilotStudioChatClient</c>'s retry wiring, exercised against <see cref="MockCopilotStudioConversationClient"/>'s
/// existing rate-limit injection (built Stage 4, unused for this purpose until now — exactly what it was built
/// for). Verified against the mock only, per the class's own honesty boundary — a real Copilot Studio 429
/// response has not been observed (no live tenant available).
/// </summary>
public class CopilotStudioChatClientRetryTests
{
    [Fact]
    public async Task AskQuestion_429ThenSuccess_RetriesAndSucceeds()
    {
        var mock = new MockCopilotStudioConversationClient();
        mock.ThrowOnAskAtCallIndex = (0, RetryableTooManyRequests());
        mock.WithReply("unused — call 0 throws before any script is consulted");
        mock.WithReply("success after retry");

        using var client = new CopilotStudioChatClient(mock);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("success after retry", response.Text);
        Assert.Equal(2, mock.AskCallCount); // 1 failed attempt + 1 successful retry
    }

    [Fact]
    public async Task AskQuestion_NonRetryableAuthFailure_FailsImmediately_NoRetry()
    {
        var mock = new MockCopilotStudioConversationClient();
        mock.ThrowOnAskAtCallIndex = (0, NonRetryableUnauthorized());

        using var client = new CopilotStudioChatClient(mock);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.Equal(1, mock.AskCallCount); // no retry wasted on a permanent failure
    }

    // ── idempotency-safety: only retry when ZERO activities were received before the failure ──
    // A live Copilot Studio agent can execute real side effects mid-turn — if any activity already streamed
    // back before a 429, that's evidence a real answer (or action) may already exist server-side, so
    // CopilotStudioRetryPolicy.ExecuteIdempotentAsync must NOT retry (would resend the identical question).

    [Fact]
    public async Task AskQuestion_ZeroActivitiesThen429_Retries()
    {
        // Named explicitly for this safety property, distinct from AskQuestion_429ThenSuccess_RetriesAndSucceeds
        // above (which exercises the identical zero-activities case incidentally via ThrowOnAskAtCallIndex).
        var mock = new MockCopilotStudioConversationClient();
        mock.ThrowOnAskAtCallIndex = (0, RetryableTooManyRequests()); // throws before any activity is emitted
        mock.WithReply("unused — call 0 throws before any script is consulted");
        mock.WithReply("success after retry");

        using var client = new CopilotStudioChatClient(mock);
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        Assert.Equal("success after retry", response.Text);
        Assert.Equal(2, mock.AskCallCount); // safe to retry — nothing had been received yet
    }

    [Fact]
    public async Task AskQuestion_OneActivityThen429_DoesNotRetry_SurfacesPartialTurnExceptionHonestly()
    {
        var mock = new MockCopilotStudioConversationClient();
        mock.ThrowAfterActivitiesOnAskAtCallIndex = (
            0,
            [MockCopilotStudioConversationClient.Message("a real partial answer already streamed back")],
            RetryableTooManyRequests());

        using var client = new CopilotStudioChatClient(mock);

        var ex = await Assert.ThrowsAsync<CopilotStudioPartialTurnException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.Equal(1, mock.AskCallCount); // NOT retried — a real side effect may already have happened
        Assert.Equal(1, ex.PartialActivityCount);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task StartConversation_ZeroActivitiesThen429_Retries()
    {
        var mock = new MockCopilotStudioConversationClient();
        mock.ThrowOnStart = RetryableTooManyRequests(); // throws before any activity is emitted — but only once
        mock.WithReply("success after start retry");

        using var client = new CopilotStudioChatClient(mock);

        // ThrowOnStart always throws (no call-index gating like ThrowOnAskAtCallIndex), so the retry itself
        // would also hit it and exhaust — this test only needs to prove the RETRY is attempted (safe to
        // retry a zero-activity failure), not that it eventually succeeds.
        var ex = await Assert.ThrowsAsync<RetryExhaustedException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.True(ex.Attempts.Count > 1, "a zero-activity Start failure must be retried, not fail on the first attempt");
    }

    [Fact]
    public async Task StartConversation_OneActivityThen429_DoesNotRetry_SurfacesPartialTurnExceptionHonestly()
    {
        var mock = new MockCopilotStudioConversationClient();
        mock.ThrowAfterActivitiesOnStart = (
            [MockCopilotStudioConversationClient.Message(text: null, overrideConversationId: "conv-already-created")],
            RetryableTooManyRequests());

        using var client = new CopilotStudioChatClient(mock);

        var ex = await Assert.ThrowsAsync<CopilotStudioPartialTurnException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.Equal(1, ex.PartialActivityCount);
        Assert.Equal(0, mock.AskCallCount); // must never reach Ask — Start itself failed and was not retried
    }

    [Fact]
    public async Task AskQuestion_OneActivityThenNonRetryableFailure_StillDoesNotRetry_SurfacesPartialTurnException()
    {
        // The idempotency gate applies REGARDLESS of whether the failure itself is 429-shaped — any partial
        // activity means "don't touch this turn again automatically," full stop.
        var mock = new MockCopilotStudioConversationClient();
        mock.ThrowAfterActivitiesOnAskAtCallIndex = (
            0,
            [MockCopilotStudioConversationClient.Message("partial")],
            NonRetryableUnauthorized());

        using var client = new CopilotStudioChatClient(mock);

        var ex = await Assert.ThrowsAsync<CopilotStudioPartialTurnException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.IsType<HttpRequestException>(ex.InnerException);
        Assert.Equal(1, mock.AskCallCount);
    }

    // ── Dispose() racing an in-flight call ──

    [Fact]
    public async Task Dispose_WhileCallInFlight_DoesNotMaskTheInFlightCallsRealException()
    {
        var mock = new MockCopilotStudioConversationClient { HangUntilCancelled = true };
        var client = new CopilotStudioChatClient(mock);

        using var cts = new CancellationTokenSource();
        var inFlightTask = client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: cts.Token);

        // Give the in-flight call time to reach the network call (and acquire the turn lock) before racing Dispose.
        await Task.Delay(50);
        var disposeTask = Task.Run(() => client.Dispose());

        // Let the in-flight call's OWN cancellation end it — proves Dispose() coordinated with the lock
        // instead of disposing out from under it (which would surface ObjectDisposedException here instead).
        await Task.Delay(50);
        cts.Cancel();

        await disposeTask; // Dispose() must complete promptly, not hang or throw

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlightTask);
        Assert.IsNotType<ObjectDisposedException>(ex); // the real outcome, not a masking exception from the lock release
    }

    [Fact]
    public async Task AskQuestion_429OnEveryAttempt_ExhaustsRetries_ThrowsRetryExhausted()
    {
        var mock = new MockCopilotStudioConversationClient();
        // RetryPolicy.Default.MaxRetries == 3 -> 4 total attempts; make every one throw.
        for (var i = 0; i < 4; i++)
        {
            mock.WithTurn((_, _) => throw RetryableTooManyRequests());
        }

        using var client = new CopilotStudioChatClient(mock);

        var ex = await Assert.ThrowsAsync<RetryExhaustedException>(
            () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]));

        Assert.Equal(4, mock.AskCallCount);
        Assert.Equal(4, ex.Attempts.Count);
    }

    // ── classifier unit tests ──

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public void IsRetryable_ClassifiesByStatusCode(HttpStatusCode statusCode, bool expectedRetryable)
    {
        var ex = new HttpRequestException("test", null, statusCode);
        Assert.Equal(expectedRetryable, CopilotStudioRetryPolicy.IsRetryable(ex));
    }

    [Fact]
    public void IsRetryable_NonHttpException_ReturnsFalse()
    {
        Assert.False(CopilotStudioRetryPolicy.IsRetryable(new InvalidOperationException("not an http error")));
    }

    [Fact]
    public void IsRetryable_HttpRequestExceptionWithNoStatusCode_ReturnsFalse()
    {
        // A transport-level failure (DNS/connect refused) carries no StatusCode — treated as non-retryable
        // by this classifier (it only targets the 429-shaped case per the design doc's scope).
        Assert.False(CopilotStudioRetryPolicy.IsRetryable(new HttpRequestException("connection refused")));
    }

    [Fact]
    public void Default_HasShouldRetrySetToIsRetryable()
    {
        Assert.NotNull(CopilotStudioRetryPolicy.Default.ShouldRetry);
        Assert.True(CopilotStudioRetryPolicy.Default.ShouldRetry!(RetryableTooManyRequests()));
        Assert.False(CopilotStudioRetryPolicy.Default.ShouldRetry!(NonRetryableUnauthorized()));
    }

    // ── generic RetryPolicy.ShouldRetry regression guard (Item 3's additive change to shared code) ──

    [Fact]
    public async Task RetryPolicy_WithoutShouldRetry_RetriesAnyException_UnchangedFromBefore()
    {
        // Regression guard: RetryPolicy.ShouldRetry defaults to null, which must reproduce the pre-Item-3
        // catch-all behavior byte-for-byte for every OTHER existing caller (metric evaluation retries, etc.).
        var policy = new RetryPolicy { MaxRetries = 1, InitialDelayMs = 1 };
        var attempts = 0;

        var result = await policy.ExecuteAsync(_ =>
        {
            attempts++;
            if (attempts == 1)
            {
                throw new InvalidOperationException("transient");
            }

            return Task.FromResult("ok");
        });

        Assert.Equal("ok", result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task RetryPolicy_WithShouldRetryFalse_RethrowsImmediately_WithoutWaitingOrWrapping()
    {
        var policy = new RetryPolicy { MaxRetries = 3, InitialDelayMs = 1, ShouldRetry = _ => false };
        var attempts = 0;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => policy.ExecuteAsync<string>(_ =>
        {
            attempts++;
            throw new InvalidOperationException("permanent");
        }));

        Assert.Equal("permanent", ex.Message); // the ORIGINAL exception, not wrapped in RetryExhaustedException
        Assert.Equal(1, attempts); // no retry attempted
    }

    private static HttpRequestException RetryableTooManyRequests() =>
        new("Too Many Requests", null, HttpStatusCode.TooManyRequests);

    private static HttpRequestException NonRetryableUnauthorized() =>
        new("Unauthorized", null, HttpStatusCode.Unauthorized);
}
