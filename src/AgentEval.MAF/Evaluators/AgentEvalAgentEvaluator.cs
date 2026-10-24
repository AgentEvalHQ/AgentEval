// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Adapters;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

using MEAIQuality = Microsoft.Extensions.AI.Evaluation.Quality;
using MEAIIEvaluator = Microsoft.Extensions.AI.Evaluation.IEvaluator;
using MEAIEvaluationResult = Microsoft.Extensions.AI.Evaluation.EvaluationResult;
using MEAIEvaluationContext = Microsoft.Extensions.AI.Evaluation.EvaluationContext;
using AgentEval.Evals;

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
/// score correctly, and a wrapped <see cref="AgentEvalCompositeEvaluator"/> hands them to its checks.
/// </para>
/// <para>
/// The rest of the item travels as MEAI additional context: the reference and the retrieved context;
/// <see cref="EvalItem.Tools"/> as M.E.AI's tool contexts (<c>ToolCallAccuracyEvaluatorContext</c>,
/// <c>TaskAdherenceEvaluatorContext</c>, <c>IntentResolutionEvaluatorContext</c>), which M.E.AI's own tool
/// evaluators and the composite read; and the names of <see cref="EvalItem.ExpectedToolCalls"/> as
/// <see cref="AgentEvalExpectedToolsContext"/>. MAF does not put the agent's instructions on the item; set
/// <see cref="AgentInstructions"/> to send them.
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

    /// <summary>
    /// The instructions the evaluated agent was given, when the caller knows them; <see langword="null"/> (the default)
    /// sends none. MAF keeps them on the agent, not on the <see cref="EvalItem"/>: read them with
    /// <c>agent.GetService&lt;ChatOptions&gt;()?.Instructions</c>, which returns the agent-level instructions only (a
    /// run's own options and an <c>AIContextProvider</c>'s instructions are merged at run time and not seen there).
    /// </summary>
    /// <remarks>
    /// They are forwarded as the conversation's first, system, message, as the agent was given them, unless the
    /// conversation already carries a system message of its own: M.E.AI's task-adherence evaluator reads them from the
    /// conversation, and <see cref="AgentEvalCompositeEvaluator"/> passes them on as <c>EvalInput.SystemMessage</c>. A
    /// system prompt can be proprietary; setting this sends it to the judges' provider.
    /// </remarks>
    public string? AgentInstructions { get; init; }

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
            // MAF puts the reference answer and the retrieved context on the item (agent.EvaluateAsync(...,
            // expectedOutput:) / EvalItem.Context); MAF's own adapter forwards no additional context, so AgentEval's
            // evaluators never saw them — similarity / F1 read "none was supplied", faithfulness had no context (#203
            // review round 15, B12g). They travel as the carriers AgentEval's MEAI evaluators read, and as the contexts
            // M.E.AI's own Groundedness / Equivalence / Completeness evaluators read, which got none (round 16, B12n).
            var additionalContext = new List<MEAIEvaluationContext>();
            if (ReferenceText.HasWords(item.ExpectedOutput))   // a wordless reference ("?") is none (round 17)
                additionalContext.Add(new AgentEvalGroundTruthContext(item.ExpectedOutput!));
            if (!string.IsNullOrWhiteSpace(item.Context))
                additionalContext.Add(new AgentEvalRAGContext(item.Context!));
            additionalContext.AddRange(MicrosoftEvaluatorAdapter.BuildAdditionalContext(item.Context, item.ExpectedOutput));

            // The tools the agent was offered and the tools the case expected were on the item and never forwarded, so
            // M.E.AI's tool-call-accuracy evaluator had no definitions to check against and AgentEval's checks graded tool
            // use from the reply text. MAF sets Tools only when the agent has at least one. M.E.AI marks these three
            // contexts experimental (AIEVAL001); they are the carriers its own tool evaluators read, and
            // AgentEvalCompositeEvaluator reads the tools back from them.
            if (item.Tools is { Count: > 0 } tools)
            {
#pragma warning disable AIEVAL001
                additionalContext.Add(new MEAIQuality.ToolCallAccuracyEvaluatorContext(tools));
                additionalContext.Add(new MEAIQuality.TaskAdherenceEvaluatorContext(tools));
                additionalContext.Add(new MEAIQuality.IntentResolutionEvaluatorContext(tools));
#pragma warning restore AIEVAL001
            }
            if (item.ExpectedToolCalls is { } expectedCalls)   // the names only: an expected call's arguments have no carrier
                additionalContext.Add(new AgentEvalExpectedToolsContext(expectedCalls.Select(c => c.Name).ToList()));

            IReadOnlyList<ChatMessage> conversation = item.Conversation;
            if (!string.IsNullOrWhiteSpace(AgentInstructions) && !conversation.Any(m => m.Role == ChatRole.System))
                conversation = [new ChatMessage(ChatRole.System, AgentInstructions), .. conversation];

            // AgentEval's flat evaluators read this to treat a turn with no tool call as a recorded none, as the composite
            // does. Only they get it: any other evaluator sees the context it always saw.
            if (_evaluator is AgentEvalEvaluator or AgentEvalMetricAdapter)
                additionalContext.Add(new AgentEvalWholeConversationContext());
            var context = additionalContext.Count > 0 ? additionalContext : null;
            var result = _evaluator is AgentEvalCompositeEvaluator composite
                // The composite is told the messages are the whole conversation, so a turn with no tool call is a
                // measured none rather than "nothing recorded".
                ? await composite.EvaluateItemAsync(conversation, response, context, wholeConversation: true, cancellationToken).ConfigureAwait(false)
                : await _evaluator.EvaluateAsync(conversation, response, _chatConfiguration, context, cancellationToken).ConfigureAwait(false);

            // The composite owns its root verdict and deliberately keeps absent atomic leaves informational.
            // The generic missing-value guard would turn those leaves into independent item failures.
            if (_evaluator is not AgentEvalCompositeEvaluator)
                FailWhatWasNotScored(result);
            results.Add(result);
        }

        // Use the caller-supplied evalName (the MAF run/eval name) for the results; the evaluator's
        // own identity is still available via Name. Ignoring evalName would silently drop a name the
        // caller set on agent.EvaluateAsync(...).
        return new AgentEvaluationResults(evalName, results, inputItems: items);
    }

    /// <summary>
    /// Fails a metric that produced no value and no failing verdict. MAF fails an item only on
    /// <c>Interpretation.Failed</c> or a false <see cref="BooleanMetric"/>, so an M.E.AI evaluator that could not score
    /// (its context or reference missing, its judge's reply unparseable) — no value, an error diagnostic, no
    /// interpretation — passed the item (#203 review round 18). Its reason is kept; MeaiToEvalResultBridge reads it as
    /// <c>error</c>, as before.
    /// </summary>
    private static void FailWhatWasNotScored(MEAIEvaluationResult result)
    {
        foreach (var metric in result.Metrics.Values)
        {
            var noValue = metric switch
            {
                NumericMetric n => n.Value is null,
                BooleanMetric b => b.Value is null,
                _ => false,
            };
            if (!noValue || metric.Interpretation?.Failed == true)
                continue;

            var why = metric.Diagnostics?
                          .Where(d => d.Severity == EvaluationDiagnosticSeverity.Error)
                          .Select(d => d.Message)
                          .FirstOrDefault()
                      ?? metric.Interpretation?.Reason ?? metric.Reason ?? "the evaluator produced no value";
            metric.Interpretation = new EvaluationMetricInterpretation(
                EvaluationRating.Inconclusive, failed: true, reason: $"No value, so no verdict: {why}");
        }
    }
}
