// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace AgentEval.Microsoft365Demo.Tests.TestHelpers;

/// <summary>
/// The judge of the offline benchmark tests: an <see cref="IChatClient"/> that never touches the network and answers
/// every judge prompt with a well-formed verdict in the shape <c>ChatClientEvaluator</c> asks for — each numbered
/// criterion of the prompt's <c>CRITERIA TO EVALUATE:</c> list echoed back as met, and an overall score of
/// <see cref="Score"/>. It grades nothing (the answers come from synthetic fixtures); it only lets a test prove the
/// demo routes criteria to a judge when one exists and reports them as measured.
/// </summary>
internal sealed partial class FakeJudgeChatClient : IChatClient
{
    private int _calls;

    /// <summary>The overall score every verdict carries.</summary>
    public int Score { get; init; } = 95;

    /// <summary>How many judge calls were made.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <inheritdoc/>
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        Interlocked.Increment(ref _calls);
        var prompt = string.Join("\n", messages.Where(m => m.Role == ChatRole.User).Select(m => m.Text));
        var criteria = CriteriaOf(prompt);
        var verdict = new
        {
            criteriaResults = criteria.Select(c => new { criterion = c, met = true, explanation = "offline test judge: met" }).ToArray(),
            overallScore = Score,
            summary = "offline test judge",
            improvements = Array.Empty<string>(),
        };
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(verdict))));
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The offline test judge answers non-streaming calls only.");

    /// <inheritdoc/>
    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    /// <inheritdoc/>
    public void Dispose()
    {
    }

    /// <summary>The numbered criteria after the prompt's <c>CRITERIA TO EVALUATE:</c> heading, without their ordinals.</summary>
    private static string[] CriteriaOf(string prompt)
    {
        var at = prompt.LastIndexOf("CRITERIA TO EVALUATE:", StringComparison.Ordinal);
        if (at < 0)
        {
            return [];
        }

        return prompt[at..]
            .Split('\n')
            .Select(line => NumberedLine().Match(line.Trim()))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value.Trim())
            .ToArray();
    }

    [GeneratedRegex(@"^\d+\.\s+(.+)$")]
    private static partial Regex NumberedLine();
}
