// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Core;   // IEvaluableAgent, AgentResponse
using Microsoft.Extensions.AI;

namespace AgentEval.RedTeam;

/// <summary>
/// <see cref="SutTier.InstrumentedAgent"/> SUT we fully control (Wave B, Pillars 1 &amp; 4). Drives a hand-rolled
/// function-invocation loop over an <see cref="IChatClient"/>: when the model emits a <c>FunctionCallContent</c> for
/// a canary, the matching <see cref="CanaryTool.Execute"/> actually RUNS and its result is fed back, so the call is
/// recorded as executed (paired <c>FunctionResultContent</c> ⇒ <see cref="EvidenceFidelity.Behavioral"/>). Because
/// <see cref="CanaryTool.Execute"/> controls what a tool "returns", Pillar-4 tool-output injection is just a canary
/// whose <c>Execute</c> returns the adversarial string — no separate machinery. Deterministic over a scripted client.
/// </summary>
public sealed class InstrumentedCanaryAgent : IToolCapableAgent
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyArgs = new Dictionary<string, object?>();

    private readonly IChatClient _client;
    private readonly string? _systemPrompt;
    private readonly int _maxToolTurns;

    /// <summary>Creates a Tier-2 instrumented SUT over <paramref name="client"/>.</summary>
    /// <param name="client">The chat client the loop drives.</param>
    /// <param name="name">Agent name for reports.</param>
    /// <param name="systemPrompt">Optional system prompt prepended to the conversation.</param>
    /// <param name="maxToolTurns">Safety bound on the call→execute→result loop (default 5).</param>
    public InstrumentedCanaryAgent(IChatClient client, string name = "InstrumentedCanaryAgent", string? systemPrompt = null, int maxToolTurns = 5)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        Name = name;
        _systemPrompt = systemPrompt;
        _maxToolTurns = maxToolTurns < 1 ? 1 : maxToolTurns;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public AgentToolCapability Capabilities =>
        AgentToolCapability.FunctionCalling | AgentToolCapability.InstrumentedTools | AgentToolCapability.ToolOutputInjection;

    /// <summary>No canary tools ⇒ a single text turn.</summary>
    public Task<AgentResponse> InvokeAsync(string prompt, CancellationToken cancellationToken = default)
        => InvokeWithToolsAsync(prompt, [], cancellationToken);

    /// <inheritdoc />
    public async Task<AgentResponse> InvokeWithToolsAsync(string prompt, IReadOnlyList<CanaryTool> tools, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(tools);

        var byName = new Dictionary<string, CanaryTool>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tools)
            byName[t.Name] = t;

        var conversation = new List<ChatMessage>();
        if (!string.IsNullOrEmpty(_systemPrompt))
            conversation.Add(new ChatMessage(ChatRole.System, _systemPrompt));
        conversation.Add(new ChatMessage(ChatRole.User, prompt));

        // RawMessages accumulates each assistant message (FunctionCallContent) and each tool message
        // (FunctionResultContent) so ToolUsageExtractor pairs them by CallId → ToolCallRecord.WasExecuted = true.
        var raw = new List<object>();
        ChatOptions? options = tools.Count == 0
            ? null
            : new ChatOptions { Tools = [.. tools.Select(ToAdvertisedSchema)] };

        var lastText = string.Empty;
        var completed = false;

        for (var turn = 0; turn < _maxToolTurns; turn++)
        {
            var response = await _client.GetResponseAsync(conversation, options, cancellationToken).ConfigureAwait(false);
            foreach (var message in response.Messages)
            {
                conversation.Add(message);
                raw.Add(message);
            }
            if (!string.IsNullOrEmpty(response.Text))
                lastText = response.Text;

            var calls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();
            if (calls.Count == 0)
            {
                completed = true;
                break;  // the model requested no tool → the conversation is complete
            }

            var resultContents = new List<AIContent>(calls.Count);
            foreach (var call in calls)
            {
                // Honesty invariant (Wave B / D6): pair a result ONLY when a real tool body runs. An unknown or
                // schema-only (Execute == null) tool is NOT executed — leave its call unpaired so ToolUsageExtractor
                // keeps WasExecuted = false and the verdict honestly reads IntentToAct (emitted), never Behavioral.
                if (!byName.TryGetValue(call.Name, out var canary) || canary.Execute is null)
                    continue;

                IReadOnlyDictionary<string, object?> args = call.Arguments is null
                    ? EmptyArgs
                    : new Dictionary<string, object?>(call.Arguments);
                try
                {
                    var output = await canary.Execute(args, cancellationToken).ConfigureAwait(false);
                    resultContents.Add(new FunctionResultContent(call.CallId, output));
                }
                catch (Exception ex)
                {
                    // The body DID run (and threw) → still executed; the exception detail rides in the result.
                    resultContents.Add(new FunctionResultContent(call.CallId, $"[tool '{call.Name}' threw: {ex.Message}]"));
                }
            }

            // Only append a tool message when at least one tool actually executed; an all-unpaired turn leaves the
            // emitted calls unanswered (correctly recorded as intent, not effect).
            if (resultContents.Count > 0)
            {
                var toolMessage = new ChatMessage(ChatRole.Tool, resultContents);
                conversation.Add(toolMessage);
                raw.Add(toolMessage);
            }
        }

        // Honesty: never silently swallow a cut-short loop (mirrors RedTeamResult.WasTruncated). If we exhausted
        // maxToolTurns with the model still requesting tools, mark the response so the evaluator/report can see it.
        if (!completed)
        {
            var note = $"[truncated: reached maxToolTurns={_maxToolTurns} with tool calls still pending]";
            lastText = string.IsNullOrEmpty(lastText) ? note : $"{lastText}\n{note}";
        }

        return new AgentResponse { Text = lastText, ModelId = "instrumented-canary", RawMessages = raw };
    }

    private static AITool ToAdvertisedSchema(CanaryTool canary)
    {
        // Execution happens in THIS agent's loop (not via MEAI middleware), so the advertised delegate is a stub —
        // the schema is what the model sees; the canary's real Execute body is invoked deterministically above.
        Func<string> stub = () => string.Empty;
        return AIFunctionFactory.Create(stub, canary.Name, canary.Description);
    }
}
