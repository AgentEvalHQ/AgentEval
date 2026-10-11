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

    private int _inputReported;
    private int _outputReported;
    private long _inputTokens;
    private long _outputTokens;

    /// <summary>Whether a run total is known for either count (see <see cref="InputTokens"/>).</summary>
    public bool ReportedUsage => InputTokens is not null || OutputTokens is not null;

    /// <summary>
    /// The judge's input tokens over every call, or null unless every call reported its input count: a call that threw
    /// or reported none leaves the total unknown, never a partial sum taken for the whole ([SUM-7]).
    /// </summary>
    public long? InputTokens => Complete(ref _inputReported) ? Interlocked.Read(ref _inputTokens) : null;

    /// <summary>The judge's output tokens over every call, or null unless every call reported its output count.</summary>
    public long? OutputTokens => Complete(ref _outputReported) ? Interlocked.Read(ref _outputTokens) : null;

    private bool Complete(ref int reported) => Calls > 0 && Volatile.Read(ref reported) == Calls;

    // Adds one usage report to the sums, and says which counts it held.
    private (bool Input, bool Output) Count(UsageDetails? usage)
    {
        if (usage is null)
        {
            return (false, false);
        }

        if (usage.InputTokenCount is { } input)
        {
            Interlocked.Add(ref _inputTokens, input);
        }

        if (usage.OutputTokenCount is { } output)
        {
            Interlocked.Add(ref _outputTokens, output);
        }

        return (usage.InputTokenCount is not null, usage.OutputTokenCount is not null);
    }

    // A completed call that reported a count, once per call.
    private void Reported((bool Input, bool Output) seen)
    {
        if (seen.Input)
        {
            Interlocked.Increment(ref _inputReported);
        }

        if (seen.Output)
        {
            Interlocked.Increment(ref _outputReported);
        }
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        try
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            Reported(Count(response.Usage));
            return response;
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
        (bool Input, bool Output) seen = (false, false);
        try
        {
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await stream.MoveNextAsync().ConfigureAwait(false))
                    {
                        Reported(seen);   // the call completed: it counts once, as reported or not
                        yield break;
                    }

                    update = stream.Current;
                    foreach (var usage in update.Contents.OfType<UsageContent>())
                    {
                        var counted = Count(usage.Details);
                        seen = (seen.Input || counted.Input, seen.Output || counted.Output);
                    }
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
