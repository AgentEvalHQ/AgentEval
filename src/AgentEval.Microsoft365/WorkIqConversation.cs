// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365.Protocol;

namespace AgentEval.Microsoft365;

/// <summary>
/// The production <see cref="IWorkIqConversation"/>: owns the server <c>contextId</c> and the current task handle,
/// serialises turns behind a single-active-turn guard, runs the blocking turn path (send → bounded
/// <c>GetTask</c> reconciliation), the streaming path, recovery after a drop, reset and abandonment, and
/// normalises evidence exactly once per turn. Card discovery runs once per conversation and the card digest is
/// recorded. Constructed by <see cref="Microsoft365AgentFactory.BuildLive"/> (internal constructor); tests use the
/// <see cref="IWorkIqConversation"/> seam instead.
/// </summary>
public sealed class WorkIqConversation : IWorkIqConversation
{
    internal WorkIqConversation(IWorkIqA2AClient client, Microsoft365Config config, WorkIqOptions options, TimeProvider clock)
    {
        Client = client ?? throw new ArgumentNullException(nameof(client));
        Config = config ?? throw new ArgumentNullException(nameof(config));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        config.Validate();
        options.Validate();
    }

    /// <summary>The transport seam.</summary>
    internal IWorkIqA2AClient Client { get; }

    /// <summary>The validated connection settings.</summary>
    internal Microsoft365Config Config { get; }

    /// <summary>The validated runtime options.</summary>
    internal WorkIqOptions Options { get; }

    /// <summary>The clock used for every local timestamp (injectable for deterministic tests).</summary>
    internal TimeProvider Clock { get; }

    /// <inheritdoc/>
    public string? ContextId => throw new NotImplementedException("TODO(M20)");

    /// <inheritdoc/>
    public WorkIqTaskHandle? ActiveTask => throw new NotImplementedException("TODO(M20)");

    /// <inheritdoc/>
    public bool IsTurnActive => throw new NotImplementedException("TODO(M20)");

    /// <inheritdoc/>
    public Task<WorkIqTurnResult> RunTurnAsync(WorkIqTurnRequest request, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(M20)");

    /// <inheritdoc/>
    public IAsyncEnumerable<WorkIqStreamEvent> StreamTurnAsync(WorkIqTurnRequest request, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(M20)");

    /// <inheritdoc/>
    public Task<WorkIqTaskSnapshot> GetTaskAsync(WorkIqTaskHandle task, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(M20)");

    /// <inheritdoc/>
    public Task<WorkIqTaskSnapshot> CancelTaskAsync(WorkIqTaskHandle task, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(M20)");

    /// <inheritdoc/>
    public IAsyncEnumerable<WorkIqStreamEvent> SubscribeAsync(WorkIqTaskHandle task, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(M20)");

    /// <inheritdoc/>
    public Task<WorkIqTurnResult> ContinueAsync(WorkIqPendingInteraction interaction, WorkIqInteractionResponse response, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(M20)");

    /// <inheritdoc/>
    public Task ResetSessionAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(M20)");

    /// <inheritdoc/>
    public Task<WorkIqAbandonmentReceipt> AbandonAndResetAsync(string reason, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("TODO(M20)");

    /// <inheritdoc/>
    public ValueTask DisposeAsync() =>
        throw new NotImplementedException("TODO(M20)");
}
