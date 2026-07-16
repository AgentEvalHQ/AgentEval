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
