// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentEval.Core;

/// <summary>
/// Sends every request at the lowest reasoning level the provider accepts, unless the caller set one:
/// <see cref="ReasoningEffort.None"/> first, <see cref="ReasoningEffort.Low"/> when the provider rejects None, and the
/// provider's own default when it rejects both (a model with no reasoning setting, such as gpt-4o).
/// </summary>
/// <remarks>
/// <para>
/// Why: a reasoning model left at its provider's default thinks before every answer. GLM-5.3-Flash on Bitdeer
/// spent two thirds of its output tokens on reasoning and took 2.2 times as long on an article-length answer
/// (2026-10-21), so a four-agent workflow missed its own time limit. AgentEval's provider factories wrap their
/// clients in this one, so agents and judges run with reasoning off where the provider allows it.
/// </para>
/// <para>
/// A request whose <see cref="ChatOptions.Reasoning"/> is set is sent as it is and never retried: code that sets
/// reasoning keeps it. Otherwise a request the provider rejects as invalid (HTTP 400 or 422) is sent again at the
/// next level; any other error is not retried. The level that succeeded is remembered for the provider, endpoint and
/// model (<see cref="ChatClientMetadata"/>) for the rest of the process, so the rejected levels cost one failed call
/// per model, not one per request. A request rejected at every level fails with the provider's error from the last
/// attempt (sent at the provider's default), and nothing is remembered.
/// </para>
/// <para>
/// A streaming request moves to the next level only when the provider rejects it before the first update.
/// </para>
/// </remarks>
public sealed class MinimalReasoningChatClient : DelegatingChatClient
{
    // The levels tried in order; null sends no reasoning setting (the provider's default).
    private static readonly ReasoningEffort?[] s_levels = [ReasoningEffort.None, ReasoningEffort.Low, null];

    // Index into s_levels of the level that last succeeded, per provider/endpoint/model.
    private static readonly ConcurrentDictionary<string, int> s_accepted = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<Type, PropertyInfo?> s_statusProperty = new();

    private readonly string _key;

    /// <summary>Wraps <paramref name="innerClient"/>.</summary>
    /// <param name="innerClient">The provider's chat client.</param>
    public MinimalReasoningChatClient(IChatClient innerClient)
        : base(innerClient)
    {
        // No metadata: remember the level for this instance only.
        _key = innerClient.GetService<ChatClientMetadata>() is { } m
            ? $"{m.ProviderName}|{m.ProviderUri}|{m.DefaultModelId}"
            : $"instance:{Guid.NewGuid():N}";
    }

    /// <summary>The level a request without its own reasoning setting is sent at now: None, Low, or null (provider default).</summary>
    public ReasoningEffort? CurrentLevel => s_levels[Start];

    /// <inheritdoc/>
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (options?.Reasoning is not null)
            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        var sent = messages as IReadOnlyCollection<ChatMessage> ?? messages.ToList();
        for (var level = Start; ; level++)
        {
            try
            {
                var response = await base.GetResponseAsync(sent, At(options, level), cancellationToken).ConfigureAwait(false);
                s_accepted[_key] = level;
                return response;
            }
            catch (Exception ex) when (level < s_levels.Length - 1 && IsRejectedRequest(ex))
            {
                // The provider rejected this level: try the next one.
            }
        }
    }

    /// <inheritdoc/>
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (options?.Reasoning is not null)
        {
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
                yield return update;
            yield break;
        }

        var sent = messages as IReadOnlyCollection<ChatMessage> ?? messages.ToList();
        for (var level = Start; ; level++)
        {
            var updates = base.GetStreamingResponseAsync(sent, At(options, level), cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                bool any;
                try
                {
                    any = await updates.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (level < s_levels.Length - 1 && IsRejectedRequest(ex))
                {
                    continue; // rejected before the first update: try the next level
                }

                s_accepted[_key] = level;
                if (!any)
                    yield break;
                yield return updates.Current;
                while (await updates.MoveNextAsync().ConfigureAwait(false))
                    yield return updates.Current;
                yield break;
            }
            finally
            {
                await updates.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private int Start => s_accepted.TryGetValue(_key, out var level) ? level : 0;

    private static ChatOptions? At(ChatOptions? options, int level)
    {
        if (s_levels[level] is not { } effort)
            return options;
        var copy = options?.Clone() ?? new ChatOptions();
        copy.Reasoning = new ReasoningOptions { Effort = effort };
        return copy;
    }

    /// <summary>
    /// True for a request the provider rejected as invalid: HTTP 400 or 422 from <see cref="HttpRequestException"/>,
    /// or from the <c>Status</c> of the OpenAI and Azure SDKs' <c>System.ClientModel.ClientResultException</c>
    /// (read by name: Core does not reference those SDKs).
    /// </summary>
    internal static bool IsRejectedRequest(Exception ex)
    {
        if (ex is HttpRequestException { StatusCode: HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity })
            return true;
        var status = s_statusProperty.GetOrAdd(ex.GetType(), static t =>
            t.GetProperty("Status", BindingFlags.Public | BindingFlags.Instance) is { PropertyType: var p } property && p == typeof(int)
                ? property
                : null);
        return status?.GetValue(ex) is 400 or 422;
    }

    /// <summary>Forgets every remembered level. For tests.</summary>
    internal static void ResetRememberedLevels() => s_accepted.Clear();
}
