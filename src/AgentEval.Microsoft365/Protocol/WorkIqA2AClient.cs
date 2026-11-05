// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using A2A;

namespace AgentEval.Microsoft365.Protocol;

/// <summary>
/// The production <see cref="IWorkIqA2AClient"/> over <c>A2A.A2AClient</c> / <c>A2A.A2ACardResolver</c>, built on
/// an <see cref="HttpClient"/> whose pipeline is <c>WorkIqAuthHandler → RawCaptureHandler → inner</c>. The blocking
/// operations live here (lane A, M10); the two streaming members live in <c>WorkIqA2AClient.Streaming.cs</c>
/// (lane C, M13). The constructor shape is defined by M10 together with the trust policy.
/// </summary>
internal sealed partial class WorkIqA2AClient : IWorkIqA2AClient
{
    /// <inheritdoc/>
    public Task<RawAgentCard> DiscoverAsync(CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(M10)");

    /// <inheritdoc/>
    public Task<WorkIqRpcResult<SendMessageResponse>> SendAsync(SendMessageRequest request, CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(M10)");

    /// <inheritdoc/>
    public Task<WorkIqRpcResult<AgentTask>> GetTaskAsync(string taskId, int? historyLength, CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(M10)");

    /// <inheritdoc/>
    public Task<WorkIqRpcResult<AgentTask>> CancelTaskAsync(string taskId, CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(M10)");
}
