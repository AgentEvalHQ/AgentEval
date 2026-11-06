// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Cli.Microsoft365;

/// <summary>
/// The Microsoft 365 agent as a <c>bench</c> family that grades answers drives it. A turn that completed with several
/// answer candidates and none selected (<see cref="Microsoft365Evidence.IsAnswerMeasured(AgentResponse)"/> is false)
/// throws <see cref="Microsoft365NoAnswerSelectedException"/> instead of returning its empty text. The text is not the
/// agent's answer, so the family must not grade it: it records the agent's exception as an error or as inconclusive.
/// Every other response passes through unchanged. Session resets and disposal go to the wrapped agent.
/// </summary>
internal sealed class Microsoft365BenchAgent : IEvaluableAgent, ISessionResettableAgent, IAsyncDisposable
{
    /// <summary>Wraps <paramref name="inner"/>.</summary>
    /// <param name="inner">The agent the target built.</param>
    public Microsoft365BenchAgent(IEvaluableAgent inner)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>The wrapped agent.</summary>
    public IEvaluableAgent Inner { get; }

    /// <inheritdoc/>
    public string Name => Inner.Name;

    /// <inheritdoc/>
    public async Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var response = await Inner.InvokeAsync(prompt, cancellationToken).ConfigureAwait(false);
        if (!Microsoft365Evidence.IsAnswerMeasured(response))
        {
            throw new Microsoft365NoAnswerSelectedException();
        }

        return response;
    }

    /// <inheritdoc/>
    public Task ResetSessionAsync(CancellationToken cancellationToken = default) =>
        Inner is ISessionResettableAgent resettable ? resettable.ResetSessionAsync(cancellationToken) : Task.CompletedTask;

    /// <inheritdoc/>
    public ValueTask DisposeAsync() =>
        Inner is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
}
