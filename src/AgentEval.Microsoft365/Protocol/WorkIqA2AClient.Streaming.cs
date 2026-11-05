// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using A2A;

namespace AgentEval.Microsoft365.Protocol;

/// <summary>
/// The streaming half of <see cref="WorkIqA2AClient"/>: <c>SendStreamingMessage</c> and <c>SubscribeToTask</c> over
/// the SDK's <c>SendStreamingMessageAsync</c> / <c>SubscribeToTaskAsync</c>, with every frame captured (client
/// sequence, connection epoch, receipt time, SSE envelope), exactly-one-of validation of <see cref="StreamResponse"/>,
/// task/context id consistency checks, and JSON-RPC errors inspected inside the stream. Owned by lane C (M13).
/// </summary>
internal sealed partial class WorkIqA2AClient
{
    /// <inheritdoc/>
    public IAsyncEnumerable<WorkIqStreamFrame> StreamAsync(SendMessageRequest request, CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(M13)");

    /// <inheritdoc/>
    public IAsyncEnumerable<WorkIqStreamFrame> SubscribeAsync(string taskId, CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(M13)");
}
