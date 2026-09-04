// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using AgentEval.Core;
using AgentEval.Evals;

// Alias to avoid conflict with AgentEval.Core.EvaluationContext
using MicrosoftEvaluationContext = Microsoft.Extensions.AI.Evaluation.EvaluationContext;
using MicrosoftIEvaluator = Microsoft.Extensions.AI.Evaluation.IEvaluator;

namespace AgentEval.Adapters;

/// <summary>
/// Adapter that wraps a Microsoft.Extensions.AI.Evaluation evaluator (Fluency, Coherence, Relevance,
/// Groundedness, Equivalence, Completeness, …) as an AgentEval <see cref="IEval"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The <see cref="IEval"/> path is the canonical one</b> (ADR-030 Slice 0.4). It produces an
/// <see cref="EvalResult"/> with <c>Provenance.Type == "atomic-llm"</c>, the judge model actually used,
/// the tokens it actually spent and a real <c>EstimatedCost</c> (via <see cref="JudgeCostMap"/>) — so a
/// first-party M.E.AI evaluator can sit inside a <see cref="CompositeEval"/> next to AgentEval's own leaves
/// and have its cost rolled up. A metric the evaluator could not score (M.E.AI's <c>NumericMetric.Value
/// == null</c>: unparseable judge output, content filter, evaluator error) is reported the way
/// <see cref="AtomicLlmEval"/> reports a judge that could not speak — <c>label:"error"</c>, severity
/// <c>"none"</c>, not passed — rather than as a confident zero.
/// </para>
/// <para>
/// The original <see cref="IMetric"/> path is <b>retained unchanged</b> for compatibility (score
/// normalised 1–5 → 0–100, indeterminate flagged in <c>Details["indeterminate"]</c>). Whether it is
/// marked obsolete for one minor release is ADR-030 §9 Q3 and is not decided here.
/// </para>
/// </remarks>
public class MicrosoftEvaluatorAdapter : IMetric, IEval
{
    /// <summary><see cref="IEval.Category"/> for every M.E.AI-wrapped evaluator.</summary>
    public const string EvalCategory = "quality.meai";

    /// <summary><see cref="IEval.Version"/> of the adapter's result shape.</summary>
    public const string EvalVersion = "1.0.0";

    private readonly MicrosoftIEvaluator _evaluator;
    private readonly IChatClient _chatClient;
    private readonly string _name;
    private readonly string _description;
    private readonly double _passingThreshold;

    /// <inheritdoc cref="IEval.Name"/>
    public string Name => _name;

    /// <inheritdoc />
    public string Description => _description;

    /// <summary>
    /// Machine-readable key, <c>meai_</c> plus the normalised <see cref="Name"/> (e.g. <c>meai_fluency</c>).
    /// </summary>
    public string Key { get; }

    /// <inheritdoc />
    public string Category => EvalCategory;

    /// <inheritdoc />
    public string Version => EvalVersion;

    /// <summary>
    /// Optional judge model identifier recorded in <see cref="EvalProvenance.JudgeModel"/> and used for
    /// cost estimation when the chat client does not report a model id on its responses. When
    /// <see langword="null"/> (default) the model id observed on the judge's responses is used, then the
    /// client's <see cref="ChatClientMetadata.DefaultModelId"/>.
    /// </summary>
    public string? JudgeModel { get; init; }

    /// <summary>
    /// Creates an adapter for a Microsoft evaluator.
    /// </summary>
    /// <param name="evaluator">The Microsoft evaluator to wrap.</param>
    /// <param name="chatClient">The chat client for the evaluator to use.</param>
    /// <param name="name">Override name (defaults to evaluator type name).</param>
    /// <param name="description">Override description.</param>
    /// <param name="passingThreshold">Passing score threshold (0-100 scale).</param>
    public MicrosoftEvaluatorAdapter(
        MicrosoftIEvaluator evaluator,
        IChatClient chatClient,
        string? name = null,
        string? description = null,
        double passingThreshold = EvaluationDefaults.PassingScoreThreshold)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _name = name ?? evaluator.GetType().Name.Replace("Evaluator", "");
        _description = description ?? $"Microsoft.Extensions.AI.Evaluation {_name} metric.";
        _passingThreshold = passingThreshold;
        Key = "meai_" + NormaliseKey(_name);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // IEval — the canonical path
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Runs the wrapped M.E.AI evaluator against <paramref name="input"/> and returns an
    /// <see cref="EvalResult"/> with <c>atomic-llm</c> provenance and the judge's real token spend.
    /// </summary>
    /// <param name="input">The eval input. <see cref="EvalInput.Response"/> is required.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException"><see cref="EvalInput.Response"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// Cancellation propagates. Any other failure of the evaluator or the judge client is returned as an
    /// <c>"error"</c> leaf (severity <c>"none"</c>, not passed) so a composite still completes and the
    /// failure is visible in the tree instead of aborting the run.
    /// </remarks>
    public async Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Response is null)
            throw new InvalidOperationException($"{nameof(MicrosoftEvaluatorAdapter)} requires EvalInput.Response to be set.");

        var meter = new UsageMeteringChatClient(_chatClient);
        try
        {
            var (messages, response) = BuildConversation(input.Query, input.Response, input.Context);

            var result = await _evaluator.EvaluateAsync(
                messages,
                response,
                new ChatConfiguration(meter),
                additionalContext: new List<MicrosoftEvaluationContext>(),
                cancellationToken: ct).ConfigureAwait(false);

            return ToEvalResult(result, meter);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ErrorResult($"Microsoft evaluator failed: {ex.Message}", meter);
        }
    }

    private EvalResult ToEvalResult(Microsoft.Extensions.AI.Evaluation.EvaluationResult result, UsageMeteringChatClient meter)
    {
        var metrics = result.Metrics;
        if (metrics.Count == 0)
            return ErrorResult("Evaluator returned no metrics.", meter);

        var firstMetric = metrics.First();
        var metricValue = firstMetric.Value;

        double value;   // 0..1
        double rawScore;
        switch (metricValue)
        {
            case NumericMetric numeric when numeric.Value is { } v:
                // Microsoft uses a 1-5 scale; 1 → 0.0, 5 → 1.0.
                rawScore = v;
                value = ScoreNormalizer.FromOneToFive(v) / 100.0;
                break;
            case NumericMetric:
                // M.E.AI's first-class "no value produced". Not a zero: an error leaf, like AtomicLlmEval.
                return ErrorResult(
                    "Evaluator produced no numeric score (indeterminate: unparseable output, content filter, or evaluator error).",
                    meter, metricValue);
            case BooleanMetric boolean when boolean.Value is { } b:
                rawScore = b ? 5 : 1;
                value = b ? 1.0 : 0.0;
                break;
            case BooleanMetric:
                return ErrorResult("Evaluator produced no boolean verdict (indeterminate).", meter, metricValue);
            default:
                return ErrorResult($"Unsupported metric type: {metricValue?.GetType().Name}", meter, metricValue);
        }

        var passThreshold = Math.Clamp(_passingThreshold / 100.0, 0.0, 1.0);
        var passed = value >= passThreshold;
        var severity = passed ? "none" : (value < 0.40 ? "high" : "medium");
        var reason = metricValue.Reason ?? metricValue.Interpretation?.Reason;

        var evidence = DiagnosticsAsEvidence(metricValue);
        if (!string.IsNullOrWhiteSpace(reason))
            evidence.Insert(0, new EvalEvidence(Source: "judge", Reference: firstMetric.Key, Message: reason));

        return new EvalResult(
            Metric: new(Key, Name, Category, Version),
            Score: new(Math.Clamp(value, 0.0, 1.0), null, passed ? "pass" : "fail", passed, passThreshold, severity, null),
            Details: new(
                Dimensions: new Dictionary<string, double>
                {
                    ["raw_score"] = rawScore,
                    ["normalised_0_100"] = value * 100.0,
                },
                Evidence: evidence.Count > 0 ? evidence : null,
                Recommendations: null,
                SubResults: null,
                AggregationStrategy: null)
            {
                Summary = string.IsNullOrWhiteSpace(reason) ? null : reason,
            },
            Provenance: BuildProvenance(meter),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

    private EvalResult ErrorResult(string message, UsageMeteringChatClient meter, EvaluationMetric? metric = null)
    {
        // Mirrors AtomicLlmEval's shape for a judge that could not speak: value 0, not passed,
        // label "error", severity "none" so it never masquerades as a confirmed violation.
        var evidence = new List<EvalEvidence>
        {
            new(Source: "evaluation-error", Reference: Key, Message: message),
        };
        if (metric is not null)
            evidence.AddRange(DiagnosticsAsEvidence(metric));

        return new EvalResult(
            Metric: new(Key, Name, Category, Version),
            Score: new(0.0, null, "error", false, Math.Clamp(_passingThreshold / 100.0, 0.0, 1.0), "none", null),
            Details: new(null, evidence, null, null, null) { Summary = message },
            Provenance: BuildProvenance(meter),
            EvaluatedAt: DateTimeOffset.UtcNow);
    }

    private EvalProvenance BuildProvenance(UsageMeteringChatClient meter)
    {
        var judgeModel = JudgeModel ?? meter.ObservedModelId ?? _chatClient.GetService<ChatClientMetadata>()?.DefaultModelId;

        int? tokensUsed = null;
        double estimatedCost = 0;
        if (meter.HasUsage)
        {
            var total = meter.InputTokens + meter.OutputTokens;
            tokensUsed = total > int.MaxValue ? int.MaxValue : (int)total;
            estimatedCost = JudgeCostMap.EstimateCost(judgeModel, meter.InputTokens, meter.OutputTokens);
        }

        return new EvalProvenance(
            Type: "atomic-llm",
            JudgeModel: judgeModel,
            PromptId: null,
            PromptHash: null,
            TokensUsed: tokensUsed,
            EstimatedCost: estimatedCost,
            CacheHit: false);
    }

    private static List<EvalEvidence> DiagnosticsAsEvidence(EvaluationMetric metric)
    {
        var list = new List<EvalEvidence>();
        if (metric.Diagnostics is null) return list;
        foreach (var d in metric.Diagnostics)
            list.Add(new EvalEvidence(Source: "diagnostic", Reference: d.Severity.ToString(), Message: d.Message));
        return list;
    }

    private static (List<ChatMessage> Messages, ChatResponse Response) BuildConversation(string query, string output, string? context)
    {
        // Build chat messages for Microsoft evaluator format
        var messages = new List<ChatMessage> { new(ChatRole.User, query) };

        // Add context if available (for grounded evaluations)
        if (!string.IsNullOrEmpty(context))
        {
            messages.Insert(0, new ChatMessage(ChatRole.System, $"Use the following context to answer:\n\n{context}"));
        }

        var response = new ChatResponse([new ChatMessage(ChatRole.Assistant, output)]);
        return (messages, response);
    }

    private static string NormaliseKey(string name)
    {
        var key = Regex.Replace(name.Trim().ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        return key.Length == 0 ? "evaluator" : key;
    }

    /// <summary>
    /// Pass-through <see cref="IChatClient"/> that records the token usage and model id of every judge
    /// call the wrapped M.E.AI evaluator makes, so the result can carry a real cost. Never disposes the
    /// inner client — it does not own it.
    /// </summary>
    private sealed class UsageMeteringChatClient(IChatClient inner) : IChatClient
    {
        private readonly object _gate = new();

        public long InputTokens { get; private set; }
        public long OutputTokens { get; private set; }
        public bool HasUsage { get; private set; }
        public string? ObservedModelId { get; private set; }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var response = await inner.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            Record(response.Usage, response.ModelId);
            return response;
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var update in inner.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                foreach (var content in update.Contents)
                {
                    if (content is UsageContent usage)
                        Record(usage.Details, update.ModelId);
                }
                if (update.ModelId is not null)
                    Record(null, update.ModelId);
                yield return update;
            }
        }

        private void Record(UsageDetails? usage, string? modelId)
        {
            lock (_gate)
            {
                if (modelId is not null) ObservedModelId ??= modelId;
                if (usage is null) return;
                if (usage.InputTokenCount is { } i) { InputTokens += i; HasUsage = true; }
                if (usage.OutputTokenCount is { } o) { OutputTokens += o; HasUsage = true; }
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

        public void Dispose()
        {
            // Deliberately empty: the inner client is owned by the adapter's caller.
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // IMetric — retained for compatibility (ADR-030 §9 Q3 decides its obsolescence)
    // ═══════════════════════════════════════════════════════════════════════

    /// <inheritdoc />
    /// <remarks>
    /// Compatibility path. Prefer <see cref="EvaluateAsync(EvalInput, CancellationToken)"/>, which
    /// carries provenance, cost and an honest <c>"error"</c> label for an indeterminate verdict.
    /// </remarks>
    public async Task<MetricResult> EvaluateAsync(
        Core.EvaluationContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var (messages, response) = BuildConversation(context.Input, context.Output, context.Context);

            // Create chat configuration
            var chatConfig = new ChatConfiguration(_chatClient);

            // Build additional context for evaluators that need it
            var additionalContext = new List<MicrosoftEvaluationContext>();

            // Run the Microsoft evaluator
            var result = await _evaluator.EvaluateAsync(
                messages,
                response,
                chatConfig,
                additionalContext: additionalContext,
                cancellationToken: cancellationToken);

            // Extract and normalize the score
            var metrics = result.Metrics;
            if (metrics.Count == 0)
            {
                return MetricResult.Fail(Name, "Evaluator returned no metrics.");
            }

            // Get the first (usually only) metric
            var firstMetric = metrics.First();
            var metricValue = firstMetric.Value;

            double score;
            string? reasoning = null;

            if (metricValue is NumericMetric numericMetric)
            {
                if (numericMetric.Value is null)
                {
                    // The evaluator produced NO numeric score (unparseable judge output, content
                    // filter, or evaluator error). Coercing null → 1.0 (lowest 1/5) reported a
                    // genuinely indeterminate result as a confident low-quality Fail, corrupting
                    // pass/fail aggregation (BUG-26). Surface it explicitly instead.
                    return MetricResult.Fail(
                        Name,
                        "Evaluator produced no numeric score (indeterminate: unparseable output, content filter, or evaluator error).",
                        score: 0,
                        new Dictionary<string, object>
                        {
                            ["microsoftMetricName"] = firstMetric.Key,
                            ["indeterminate"] = true,
                        });
                }

                // Microsoft uses a 1-5 scale; convert to 0-100.
                score = ScoreNormalizer.FromOneToFive(numericMetric.Value.Value);
                reasoning = numericMetric.Interpretation?.ToString();
            }
            else if (metricValue is BooleanMetric boolMetric)
            {
                score = boolMetric.Value == true ? 100 : 0;
                reasoning = boolMetric.Interpretation?.ToString();
            }
            else
            {
                return MetricResult.Fail(Name, $"Unsupported metric type: {metricValue?.GetType().Name}");
            }

            var metadata = new Dictionary<string, object>
            {
                ["microsoftMetricName"] = firstMetric.Key,
                ["rawScore"] = metricValue is NumericMetric nm ? (nm.Value ?? 0.0) : (metricValue is BooleanMetric bm ? (bm.Value == true ? 5 : 1) : 0),
                ["interpretation"] = ScoreNormalizer.Interpret(score)
            };

            if (!string.IsNullOrEmpty(reasoning))
            {
                metadata["reasoning"] = reasoning;
            }

            if (score >= _passingThreshold)
            {
                return MetricResult.Pass(Name, score, reasoning ?? $"Score: {score:F0}/100", metadata);
            }
            else
            {
                return MetricResult.Fail(Name, reasoning ?? $"Score below threshold: {score:F0}/100", score, metadata);
            }
        }
        catch (Exception ex)
        {
            return MetricResult.Fail(
                Name,
                $"Microsoft evaluator failed: {ex.Message}",
                details: new Dictionary<string, object> { ["error"] = ex.Message });
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // FACTORY METHODS FOR COMMON EVALUATORS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a Fluency evaluator (grammar, vocabulary, sentence structure).
    /// </summary>
    public static MicrosoftEvaluatorAdapter CreateFluencyEvaluator(IChatClient chatClient)
        => new(new FluencyEvaluator(), chatClient,
            "Fluency",
            "Evaluates linguistic fluency including grammar, vocabulary, and sentence structure.");

    /// <summary>
    /// Creates a Coherence evaluator (logical flow and organization).
    /// </summary>
    public static MicrosoftEvaluatorAdapter CreateCoherenceEvaluator(IChatClient chatClient)
        => new(new CoherenceEvaluator(), chatClient,
            "Coherence",
            "Evaluates logical flow, organization, and consistency of the response.");

    /// <summary>
    /// Creates a Relevance evaluator (answer addresses the question).
    /// </summary>
    public static MicrosoftEvaluatorAdapter CreateRelevanceEvaluator(IChatClient chatClient)
        => new(new RelevanceEvaluator(), chatClient,
            "Relevance",
            "Evaluates how well the response addresses the user's question.");

    /// <summary>
    /// Creates a Groundedness evaluator (answer is grounded in context).
    /// </summary>
    public static MicrosoftEvaluatorAdapter CreateGroundednessEvaluator(IChatClient chatClient)
        => new(new GroundednessEvaluator(), chatClient,
            "Groundedness",
            "Evaluates whether the response is grounded in the provided context (no hallucinations).");

    /// <summary>
    /// Creates an Equivalence evaluator (answer matches ground truth).
    /// </summary>
    public static MicrosoftEvaluatorAdapter CreateEquivalenceEvaluator(IChatClient chatClient)
        => new(new EquivalenceEvaluator(), chatClient,
            "Equivalence",
            "Evaluates semantic equivalence between the response and expected answer.");

    /// <summary>
    /// Creates a Completeness evaluator (answer is complete).
    /// </summary>
    public static MicrosoftEvaluatorAdapter CreateCompletenessEvaluator(IChatClient chatClient)
        => new(new CompletenessEvaluator(), chatClient,
            "Completeness",
            "Evaluates whether the response fully addresses all aspects of the question.");
}

/// <summary>
/// Extension methods for easily adding Microsoft evaluators to an evaluation suite.
/// </summary>
public static class MicrosoftEvaluatorExtensions
{
    /// <summary>
    /// Create all standard Microsoft quality evaluators, typed as <see cref="IMetric"/> (compatibility path).
    /// </summary>
    public static IEnumerable<IMetric> CreateAllQualityEvaluators(IChatClient chatClient)
    {
        foreach (var adapter in CreateAll(chatClient))
            yield return adapter;
    }

    /// <summary>
    /// Create all standard Microsoft quality evaluators, typed as <see cref="IEval"/> — the canonical
    /// path (ADR-030 Slice 0.4), ready to be placed as components of a <see cref="CompositeEval"/>.
    /// </summary>
    public static IEnumerable<IEval> CreateAllQualityEvals(IChatClient chatClient)
    {
        foreach (var adapter in CreateAll(chatClient))
            yield return adapter;
    }

    private static IEnumerable<MicrosoftEvaluatorAdapter> CreateAll(IChatClient chatClient)
    {
        yield return MicrosoftEvaluatorAdapter.CreateFluencyEvaluator(chatClient);
        yield return MicrosoftEvaluatorAdapter.CreateCoherenceEvaluator(chatClient);
        yield return MicrosoftEvaluatorAdapter.CreateRelevanceEvaluator(chatClient);
        yield return MicrosoftEvaluatorAdapter.CreateGroundednessEvaluator(chatClient);
        yield return MicrosoftEvaluatorAdapter.CreateEquivalenceEvaluator(chatClient);
        yield return MicrosoftEvaluatorAdapter.CreateCompletenessEvaluator(chatClient);
    }
}
