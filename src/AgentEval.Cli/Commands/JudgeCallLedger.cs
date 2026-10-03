// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Wraps a benchmark's judge so a judge that fails cannot disappear into "inconclusive".
/// </summary>
/// <remarks>
/// The attack graders turn a judge exception into an Inconclusive verdict, a category with no conclusive probe becomes
/// a skipped leaf, and the composite ignores skipped leaves. A wrong key, an expired quota or a timed-out judge
/// therefore used to leave a run that read PASS. The ledger counts every call and every failure so the command can
/// report such a run as incomplete, and <see cref="PreflightAsync"/> makes one cheap call before the scan so a
/// misconfigured judge stops the run before anything is spent on the agent.
/// </remarks>
internal sealed class JudgeCallLedger(IChatClient inner) : DelegatingChatClient(inner)
{
    private int _calls;
    private int _failures;

    /// <summary>Judge calls made during the scan (the preflight call is not counted).</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Judge calls that threw, including timeouts: each one is a verdict the judge did not give.</summary>
    public int Failures => Volatile.Read(ref _failures);

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Increment(ref _failures);
            throw;
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        var stream = base.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await stream.MoveNextAsync().ConfigureAwait(false)) yield break;
                    update = stream.Current;
                }
                catch
                {
                    Interlocked.Increment(ref _failures);
                    throw;
                }
                yield return update;
            }
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One short call to the judge. Returns <see langword="null"/> when it answered, or the reason it could not.
    /// </summary>
    public async Task<string?> PreflightAsync(CancellationToken cancellationToken)
    {
        try
        {
            await base.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "Reply with the single word OK.")], null, cancellationToken)
                .ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return $"{ex.GetType().Name}: {ex.Message}";
        }
    }
}
