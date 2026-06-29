// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

using MEAIIEvaluator = Microsoft.Extensions.AI.Evaluation.IEvaluator;
using MEAIEvaluationResult = Microsoft.Extensions.AI.Evaluation.EvaluationResult;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// Adapts any AgentEval (MEAI) <see cref="MEAIIEvaluator"/> — flat metrics via
/// <see cref="AgentEvalEvaluator"/> / <see cref="AgentEvalEvaluators"/>, or a whole benchmark composite
/// via <see cref="AgentEvalCompositeEvaluator"/> — into Microsoft Agent Framework's <b>native</b>
/// <see cref="IAgentEvaluator"/> contract, so it can be run with
/// <c>agent.EvaluateAsync(queries, evaluator)</c> (no <see cref="ChatConfiguration"/> at the call site).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why use this instead of the MEAI <see cref="MEAIIEvaluator"/> overload of
/// <c>agent.EvaluateAsync</c>?</b> MAF's built-in MEAI adapter forwards only the <i>query half</i> of
/// each evaluated item (it splits the conversation and passes the query messages plus the final
/// response). Intermediate tool-call messages are therefore not seen by the evaluator, so AgentEval's
/// code-based tool metrics (e.g. <c>ToolSelectionMetric</c>) observe no calls.
/// </para>
/// <para>
/// Because an <see cref="IAgentEvaluator"/> receives the raw <see cref="EvalItem"/>, this adapter
/// forwards <see cref="EvalItem.Conversation"/> — the <b>full</b> conversation, including the assistant
/// tool-call and tool-result turns — to the wrapped evaluator. AgentEval's
/// <see cref="ConversationExtractor"/> can then recover the tool calls, so code-based tool metrics
/// score correctly. LLM-judged metrics behave the same either way.
/// </para>
/// <para>
/// Construct directly, or fluently via <see cref="AgentEvaluatorExtensions.AsAgentEvaluator"/>.
/// </para>
/// </remarks>
public sealed class AgentEvalAgentEvaluator : IAgentEvaluator
{
    private readonly MEAIIEvaluator _evaluator;
    private readonly ChatConfiguration _chatConfiguration;

    /// <summary>Creates a native <see cref="IAgentEvaluator"/> over an AgentEval MEAI evaluator.</summary>
    /// <param name="evaluator">The AgentEval (MEAI) evaluator to run per item.</param>
    /// <param name="chatConfiguration">Chat configuration forwarded to the evaluator (the judge model).</param>
    /// <param name="name">Optional evaluator name; defaults to the wrapped evaluator's type name.</param>
    public AgentEvalAgentEvaluator(MEAIIEvaluator evaluator, ChatConfiguration chatConfiguration, string? name = null)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _chatConfiguration = chatConfiguration ?? throw new ArgumentNullException(nameof(chatConfiguration));
        Name = name ?? evaluator.GetType().Name;
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public async Task<AgentEvaluationResults> EvaluateAsync(
        IReadOnlyList<EvalItem> items,
        string evalName = "AgentEval",
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);

        var results = new List<MEAIEvaluationResult>(items.Count);

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = item.RawResponse
                ?? new ChatResponse(new ChatMessage(ChatRole.Assistant, item.Response));

            // KEY DIFFERENCE vs MAF's built-in MEAI adapter: forward the FULL conversation
            // (item.Conversation includes the assistant tool-call + tool-result turns), not just the
            // query half — so AgentEval's ConversationExtractor can recover the tool calls.
            var result = await _evaluator.EvaluateAsync(
                item.Conversation,
                response,
                _chatConfiguration,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            results.Add(result);
        }

        // Use the caller-supplied evalName (the MAF run/eval name) for the results; the evaluator's
        // own identity is still available via Name. Ignoring evalName would silently drop a name the
        // caller set on agent.EvaluateAsync(...).
        return new AgentEvaluationResults(evalName, results, inputItems: items);
    }
}
