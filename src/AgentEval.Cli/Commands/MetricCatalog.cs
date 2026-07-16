// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Metrics.Agentic;
using AgentEval.Metrics.RAG;
using AgentEval.Metrics.ResponsibleAI;
using AgentEval.Metrics.Safety;
using Microsoft.Extensions.AI;

namespace AgentEval.Cli.Commands;

/// <summary>
/// Item 4 (<c>strategy/CLI-Custom-Benchmarks-CopilotStudio-OpenAI-and-Metrics-Remediation-Design.md</c> §2 D,
/// candidate D1): resolves a bare <c>--metrics</c> name (e.g. <c>llm_relevance</c>) to a real
/// <see cref="IMetric"/> instance. Genuinely new code — nothing in this repo mapped a name string to a
/// metric instance before this; <c>AgentEvalBuilder</c> requires the caller to <c>.AddMetric(new
/// SomeMetric(...))</c> by hand.
/// </summary>
/// <remarks>
/// <b>v1 scope, deliberately:</b> only metrics that (a) implement the plain <see cref="IMetric"/> contract
/// and (b) can be constructed from nothing but an optional evaluator <see cref="IChatClient"/> — no
/// per-invocation configuration the CLI has no generic source for. Excluded, and why:
/// <list type="bullet">
///   <item><c>code_tool_selection</c>/<c>code_tool_arguments</c> — require an explicit expected-tools/
///   required-arguments shape per test case; there's no generic <c>--metrics</c>-level source for that.</item>
///   <item><c>code_mrr_at_N</c>/<c>code_recall_at_K</c> — parametrized, dynamic names (not a fixed string).</item>
///   <item><c>ConversationCompleteness</c> — does not implement <see cref="IMetric"/> at all (a different,
///   bespoke evaluation shape consumed via <c>ConversationRunner</c>).</item>
/// </list>
/// </remarks>
internal static class MetricCatalog
{
    /// <summary>Code-based metrics — free, no LLM call, default-constructible.</summary>
    private static readonly IReadOnlyDictionary<string, Func<IMetric>> CodeBased =
        new Dictionary<string, Func<IMetric>>(StringComparer.OrdinalIgnoreCase)
        {
            ["code_tool_success"] = () => new ToolSuccessMetric(),
            ["code_tool_efficiency"] = () => new ToolEfficiencyMetric(),
            ["code_toxicity"] = () => new ToxicityMetric(),   // parameterless overload = pattern-only, no LLM fallback
            ["code_skill_disclosure_efficiency"] = () => new SkillDisclosureEfficiencyMetric(),
        };

    /// <summary>LLM-as-judge metrics — need an evaluator <see cref="IChatClient"/> to construct.</summary>
    private static readonly IReadOnlyDictionary<string, Func<IChatClient, IMetric>> LlmBased =
        new Dictionary<string, Func<IChatClient, IMetric>>(StringComparer.OrdinalIgnoreCase)
        {
            ["llm_relevance"] = c => new RelevanceMetric(c),
            ["llm_faithfulness"] = c => new FaithfulnessMetric(c),
            ["llm_context_precision"] = c => new ContextPrecisionMetric(c),
            ["llm_context_recall"] = c => new ContextRecallMetric(c),
            ["llm_answer_correctness"] = c => new AnswerCorrectnessMetric(c),
            ["llm_groundedness"] = c => new GroundednessMetric(c),
            ["llm_coherence"] = c => new CoherenceMetric(c),
            ["llm_fluency"] = c => new FluencyMetric(c),
            ["llm_bias"] = c => new BiasMetric(c),
            ["llm_misinformation"] = c => new MisinformationMetric(c),
            ["llm_task_completion"] = c => new TaskCompletionMetric(c),
        };

    /// <summary>Every resolvable metric name, sorted, for error messages and <c>agenteval list --type metrics</c>-style introspection.</summary>
    public static IReadOnlyList<string> AvailableNames { get; } =
        CodeBased.Keys.Concat(LlmBased.Keys).OrderBy(n => n, StringComparer.Ordinal).ToList();

    /// <summary>True if <paramref name="name"/> is a name this catalog can resolve (case-insensitive) — a pure lookup, no <see cref="IChatClient"/> needed.</summary>
    public static bool IsKnown(string name) => CodeBased.ContainsKey(name) || LlmBased.ContainsKey(name);

    /// <summary>
    /// Resolves <paramref name="name"/> to a fresh <see cref="IMetric"/> instance.
    /// </summary>
    /// <param name="name">The bare metric name (case-insensitive).</param>
    /// <param name="evaluatorClient">
    /// The chat client to construct an LLM-based metric with. Required only when <paramref name="name"/>
    /// names an LLM-based metric; ignored for code-based ones.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a known metric name.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="name"/> names an LLM-based metric but <paramref name="evaluatorClient"/> is <see langword="null"/>.
    /// </exception>
    public static IMetric Resolve(string name, IChatClient? evaluatorClient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (CodeBased.TryGetValue(name, out var codeFactory))
        {
            return codeFactory();
        }

        if (LlmBased.TryGetValue(name, out var llmFactory))
        {
            if (evaluatorClient is null)
            {
                throw new InvalidOperationException(
                    $"Metric '{name}' requires an LLM judge to score — pass --judge <url> (or --azure/--endpoint " +
                    "already provides one for the default endpoint path; --sut targets have no model of their own).");
            }

            return llmFactory(evaluatorClient);
        }

        throw new ArgumentException(
            $"Unknown metric name: '{name}'. Available: {string.Join(", ", AvailableNames)}.", nameof(name));
    }
}
