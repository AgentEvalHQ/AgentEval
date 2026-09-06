// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Galaxus Interview Demo

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Galaxus.RecommendationAgent.Evals.Controls;

/// <summary>
/// Builds the same message shape a real MAF run produces, so a scripted control feeds the
/// IDENTICAL extraction and grading path as the live agent.
/// </summary>
/// <remarks>
/// <para>
/// This matters more than it looks. A control that reported its behaviour through a side channel
/// would prove only that the side channel works. These controls emit
/// <see cref="FunctionCallContent"/> / <see cref="FunctionResultContent"/> pairs into
/// <c>AgentResponse.RawMessages</c>, exactly as <c>MAFAgentAdapter</c> does, so
/// <c>ToolUsageExtractor.Extract</c> — the real one, not a stub — builds the report the graders
/// then read. If the extraction path were broken, the controls would fail to trip and the wiring
/// self-check would say so.
/// </para>
/// </remarks>
public sealed partial class ScriptedTrace
{
    private readonly List<ChatMessage> _messages = [];
    private readonly StringBuilder _text = new();
    private int _nextCallId = 1;

    /// <summary>Records a tool call AND its result — the shape of a tool that actually ran.</summary>
    /// <param name="toolName">Tool name, spelled exactly as the agent registers it.</param>
    /// <param name="arguments">Arguments, keyed by parameter name.</param>
    /// <param name="result">The tool's return value.</param>
    public ScriptedTrace Call(string toolName, IDictionary<string, object?>? arguments = null, object? result = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        string callId = $"scripted-{_nextCallId++}";
        _messages.Add(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(callId, toolName, arguments)]));
        _messages.Add(new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent(callId, result ?? "{\"status\":\"ok\"}")]));
        return this;
    }

    /// <summary>
    /// Records a tool call with NO paired result — an emitted call that never executed, which is
    /// what an approval-gated tool produces.
    /// </summary>
    /// <param name="toolName">Tool name.</param>
    /// <param name="arguments">Arguments.</param>
    public ScriptedTrace CallWithoutResult(string toolName, IDictionary<string, object?>? arguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        string callId = $"scripted-{_nextCallId++}";
        _messages.Add(new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent(callId, toolName, arguments)]));
        return this;
    }

    /// <summary>Records a <c>PresentRecommendation</c> call with the four frozen argument names.</summary>
    /// <param name="sku">The sku argument.</param>
    /// <param name="reason">The reason argument.</param>
    /// <param name="evidence">The evidence argument.</param>
    /// <param name="outOfStock">The outOfStock argument, emitted as a JSON boolean.</param>
    public ScriptedTrace Present(string sku, string reason, string evidence, bool outOfStock = false) =>
        Call(PresentedCall.ToolName, new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [PresentRecommendationArguments.Sku] = sku,
            [PresentRecommendationArguments.Reason] = reason,
            [PresentRecommendationArguments.Evidence] = evidence,
            [PresentRecommendationArguments.OutOfStock] = outOfStock,
        }, result: $"{{\"status\":\"presented\",\"sku\":\"{sku}\"}}");

    /// <summary>Appends assistant prose. Nothing in this suite grades it; it is here for the log.</summary>
    /// <param name="text">The text.</param>
    public ScriptedTrace Say(string text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            if (_text.Length > 0) _text.Append(' ');
            _text.Append(text);
        }
        return this;
    }

    /// <summary>Freezes the trace into the response shape the harness consumes.</summary>
    /// <param name="modelId">Model id to stamp on the response, or null for a no-model control.</param>
    /// <param name="usage">
    /// PROVIDER-reported token usage for the work this trace replays, or null when there is none.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Why usage is a parameter and not a computation.</b> A scripted trace's text is REPLAYED —
    /// it is assembled from state a workflow already produced — so <c>MAFEvaluationHarness</c>'s
    /// fallback (<c>ModelPricing.EstimateTokensFromText</c> over <c>ActualOutput</c>) measures the
    /// length of a string no model was ever billed for. Handing the harness the real usage block
    /// replaces an estimate of the wrong text with a measurement; handing it a computed one would
    /// replace it with a better-dressed estimate. So the only thing that may be passed here is a
    /// figure a provider returned.
    /// </para>
    /// <para>
    /// ⚠ <b>Pass null unless the figure is COMPLETE.</b> Setting <c>TokenUsage</c> makes the harness
    /// stamp <c>TokensAreEstimated = false</c>, i.e. "this is a measurement" — so a partial total,
    /// from a run where some call returned no usage block, would be published as a whole one. That is
    /// the flattering direction. The caller decides; see <c>Eval08LiveWorkflowArm.InvokeAsync</c>.
    /// </para>
    /// </remarks>
    public AgentResponse ToResponse(string? modelId = null, TokenUsage? usage = null)
    {
        string text = _text.Length > 0 ? _text.ToString() : "(scripted control — no prose)";
        _messages.Add(new ChatMessage(ChatRole.Assistant, text));

        return new AgentResponse
        {
            Text = text,
            RawMessages = [.. _messages.Cast<object>()],
            ModelId = modelId,
            TokenUsage = usage,
            FinishReason = "Stop",
        };
    }

    /// <summary>The customer id the eval frame carries, or null when the prompt has no frame.</summary>
    /// <remarks>
    /// The controls read their subject from the PROMPT, like the real agent does, rather than
    /// being handed it by the harness. A control configured out of band would be a different
    /// experiment from the one the live agent runs.
    /// </remarks>
    /// <param name="prompt">The framed prompt.</param>
    public static string? PersonaIdFrom(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return null;
        var match = PersonaIdPattern().Match(prompt);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"\b(USR-[A-Z]{2}-\d{2})\b", RegexOptions.CultureInvariant)]
    private static partial Regex PersonaIdPattern();
}
