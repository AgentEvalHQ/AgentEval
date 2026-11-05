// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using Microsoft.Extensions.AI;

namespace AgentEval.Microsoft365Demo.Tests.TestHelpers;

/// <summary>
/// A judge whose every call fails the way a real provider's client does: an <see cref="HttpRequestException"/> with the
/// status <see cref="Status"/> (401 for a mistyped key by default). No network. Counts the calls, so a test can prove the
/// judge was reached exactly once per graded case.
/// </summary>
internal sealed class ThrowingJudgeChatClient : IChatClient
{
    private int _calls;

    /// <summary>The HTTP status every call fails with.</summary>
    public HttpStatusCode Status { get; init; } = HttpStatusCode.Unauthorized;

    /// <summary>How many judge calls were made.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>The exception message (the shape .NET's HttpClient produces).</summary>
    public string Message => $"Response status code does not indicate success: {(int)Status} ({Status}).";

    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        throw new HttpRequestException(Message, inner: null, Status);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The throwing test judge answers non-streaming calls only.");

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc/>
    public void Dispose()
    {
    }
}
