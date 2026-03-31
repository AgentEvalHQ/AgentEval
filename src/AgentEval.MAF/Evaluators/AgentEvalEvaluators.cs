// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;
using AgentEval.Core;
using AgentEval.Metrics.Agentic;
using AgentEval.Metrics.RAG;
using AgentEval.Metrics.Safety;
using AgentEval.Metrics.ResponsibleAI;

using MEAIIEvaluator = Microsoft.Extensions.AI.Evaluation.IEvaluator;

namespace AgentEval.MAF.Evaluators;

/// <summary>
/// Factory for creating AgentEval evaluators that implement MEAI's
/// <see cref="MEAIIEvaluator"/> interface.
/// </summary>
/// <remarks>
/// Use these with MAF's <c>agent.EvaluateAsync()</c> extension methods.
/// Mirrors the discoverability pattern of MAF's <c>FoundryEvals.RELEVANCE</c>.
/// </remarks>
public static class AgentEvalEvaluators
{
    // ═══════════════════════════════════════════════════════════════════
    // PRESET BUNDLES
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Quality metrics: faithfulness, relevance, coherence, fluency.
    /// All work fully through the light path (text-only evaluation).
    /// </summary>
    public static AgentEvalEvaluator Quality(IChatClient judgeClient) => new([
        new FaithfulnessMetric(judgeClient),
        new RelevanceMetric(judgeClient),
        new CoherenceMetric(judgeClient),
        new FluencyMetric(judgeClient)]);

    /// <summary>
    /// RAG metrics: faithfulness, relevance, context precision, context recall, answer correctness.
    /// Requires <see cref="AgentEvalRAGContext"/> and/or <see cref="AgentEvalGroundTruthContext"/>
    /// via <c>additionalContext</c>.
    /// </summary>
    public static AgentEvalEvaluator RAG(IChatClient judgeClient) => new([
        new FaithfulnessMetric(judgeClient),
        new RelevanceMetric(judgeClient),
        new ContextPrecisionMetric(judgeClient),
        new ContextRecallMetric(judgeClient),
        new AnswerCorrectnessMetric(judgeClient)]);

    /// <summary>
    /// Agentic metrics: tool success rate.
    /// Tool names/arguments extracted from <see cref="FunctionCallContent"/> in messages.
    /// </summary>
    public static AgentEvalEvaluator Agentic() => new([
        new ToolSuccessMetric()]);

    /// <summary>
    /// Agentic metrics with expected tools: tool success, tool selection.
    /// </summary>
    public static AgentEvalEvaluator Agentic(IEnumerable<string> expectedTools) => new([
        new ToolSuccessMetric(),
        new ToolSelectionMetric(expectedTools)]);

    /// <summary>
    /// Safety metrics: toxicity, bias, misinformation.
    /// All work fully through the light path (text-only evaluation).
    /// </summary>
    public static AgentEvalEvaluator Safety(IChatClient judgeClient) => new([
        new ToxicityMetric(judgeClient),
        new BiasMetric(judgeClient),
        new MisinformationMetric(judgeClient)]);

    /// <summary>
    /// All available metrics (quality + agentic + safety + task completion).
    /// The most comprehensive single-call evaluation.
    /// </summary>
    public static AgentEvalEvaluator Advanced(IChatClient judgeClient) => new([
        new FaithfulnessMetric(judgeClient),
        new RelevanceMetric(judgeClient),
        new CoherenceMetric(judgeClient),
        new FluencyMetric(judgeClient),
        new GroundednessMetric(judgeClient),
        new ToolSuccessMetric(),
        new TaskCompletionMetric(judgeClient),
        new ToxicityMetric(judgeClient),
        new BiasMetric(judgeClient),
        new MisinformationMetric(judgeClient)]);

    // ═══════════════════════════════════════════════════════════════════
    // INDIVIDUAL METRICS AS MEAI IEvaluator
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Faithfulness: is the response grounded in the provided context?</summary>
    public static MEAIIEvaluator Faithfulness(IChatClient c) => Adapt(new FaithfulnessMetric(c));
    /// <summary>Relevance: does the response address the user's question?</summary>
    public static MEAIIEvaluator Relevance(IChatClient c) => Adapt(new RelevanceMetric(c));
    /// <summary>Coherence: is the response logically consistent?</summary>
    public static MEAIIEvaluator Coherence(IChatClient c) => Adapt(new CoherenceMetric(c));
    /// <summary>Fluency: is the response grammatically correct and natural?</summary>
    public static MEAIIEvaluator Fluency(IChatClient c) => Adapt(new FluencyMetric(c));
    /// <summary>Groundedness: does the response avoid unsubstantiated claims?</summary>
    public static MEAIIEvaluator Groundedness(IChatClient c) => Adapt(new GroundednessMetric(c));
    /// <summary>Tool success: did all invoked tools execute without errors?</summary>
    public static MEAIIEvaluator ToolSuccess() => Adapt(new ToolSuccessMetric());
    /// <summary>Task completion: did the agent accomplish the requested task?</summary>
    public static MEAIIEvaluator TaskCompletion(IChatClient c) => Adapt(new TaskCompletionMetric(c));
    /// <summary>Toxicity: does the response contain harmful content?</summary>
    public static MEAIIEvaluator Toxicity(IChatClient c) => Adapt(new ToxicityMetric(c));
    /// <summary>Bias: does the response contain biased content?</summary>
    public static MEAIIEvaluator Bias(IChatClient c) => Adapt(new BiasMetric(c));
    /// <summary>Misinformation: does the response contain false claims?</summary>
    public static MEAIIEvaluator Misinformation(IChatClient c) => Adapt(new MisinformationMetric(c));

    // ═══════════════════════════════════════════════════════════════════
    // CUSTOM COMPOSITION
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Create a custom composite evaluator from specific AgentEval metrics.</summary>
    public static AgentEvalEvaluator Custom(params IMetric[] metrics) => new(metrics);

    /// <summary>Wraps any AgentEval <see cref="IMetric"/> as an individual MEAI evaluator.</summary>
    public static MEAIIEvaluator Adapt(IMetric metric) => new AgentEvalMetricAdapter(metric);
}
