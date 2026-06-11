// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Core;   // IEvaluableAgent, AgentResponse
using Microsoft.Extensions.AI;

namespace AgentEval.RedTeam;

/// <summary>
/// <see cref="SutTier.FunctionCalling"/> SUT (Wave B, Pillar 1): wraps a raw <see cref="IChatClient"/>, advertises
/// canary tool <b>schemas</b>, and returns the model's emitted <c>FunctionCallContent</c> WITHOUT executing them.
/// The client is called directly (NOT wrapped in <c>UseFunctionInvocation</c>), so an emitted call is returned in
/// <c>ChatResponse.Messages</c> instead of being auto-invoked; each advertised tool is also a throwing stub, so any
/// accidental invocation fails loudly rather than silently producing an effect. Port of garak/PyRIT generator-target
/// adapters, done natively.
/// </summary>
/// <remarks>
/// <b>Precondition for the no-exec guarantee:</b> the supplied <see cref="IChatClient"/> must be a RAW endpoint, not
/// one already wrapped in function-invocation middleware (<c>UseFunctionInvocation</c>). If the caller pre-wraps the
/// client, that middleware — not this agent — would execute the emitted call; the throwing stub then surfaces the
/// violation as an exception rather than a silent effect.
/// </remarks>
public sealed class CanaryToolChatClientAgent : IToolCapableAgent
{
    private readonly IChatClient _client;
    private readonly string? _systemPrompt;

    /// <summary>Creates a Tier-1 SUT over <paramref name="client"/>.</summary>
    public CanaryToolChatClientAgent(IChatClient client, string name = "CanaryToolChatClient", string? systemPrompt = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Name = name;
        _systemPrompt = systemPrompt;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public AgentToolCapability Capabilities => AgentToolCapability.FunctionCalling;

    /// <summary>No canary tools advertised ⇒ an ordinary text turn (Tier-0 behavior).</summary>
    public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        => InvokeWithToolsAsync(prompt, [], cancellationToken);

    /// <inheritdoc />
    public async Task<AgentResponse> InvokeWithToolsAsync(string prompt, IReadOnlyList<CanaryTool> tools, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(tools);

        var messages = new List<ChatMessage>();
        if (!string.IsNullOrEmpty(_systemPrompt))
            messages.Add(new ChatMessage(ChatRole.System, _systemPrompt));
        messages.Add(new ChatMessage(ChatRole.User, prompt));

        // Advertise schemas only. No UseFunctionInvocation ⇒ the model's emitted FunctionCallContent is RETURNED,
        // never executed. The throwing stub is a belt-and-suspenders guard against accidental invocation.
        ChatOptions? options = tools.Count == 0
            ? null
            : new ChatOptions { Tools = [.. tools.Select(ToSchemaOnlyTool)] };

        var response = await _client.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);

        return new AgentResponse
        {
            Text = response.Text ?? string.Empty,
            ModelId = response.ModelId,
            RawMessages = response.Messages.Cast<object>().ToList(),
        };
    }

    private static AITool ToSchemaOnlyTool(CanaryTool canary)
    {
        Func<string> throwIfInvoked = () =>
            throw new InvalidOperationException(
                $"Canary tool '{canary.Name}' must never execute at Tier 1 — emitting the call IS the signal.");
        return AIFunctionFactory.Create(throwIfInvoked, canary.Name, canary.Description);
    }
}
