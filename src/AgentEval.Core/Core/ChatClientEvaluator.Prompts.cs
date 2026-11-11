// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Prompts;
using Microsoft.Extensions.AI;

namespace AgentEval.Core;

public partial class ChatClientEvaluator : IJudgePromptBindable, IResolvedJudgePromptSource
{
    private readonly bool _customerSystemPrompt;
    private readonly JudgePromptOptions? _promptOptions;
    private readonly JudgePromptDefinition? _promptDefinition;

    /// <inheritdoc />
    public ResolvedJudgePrompt? ResolvedPrompt { get; }

    /// <inheritdoc />
    public bool HasPromptConfiguration => _promptOptions is not null;

    private ChatClientEvaluator(ChatClientEvaluator source, EvalRubric? rubric, string? dimension,
        JudgePromptOptions? options, JudgePromptDefinition? definition)
        : this(source._chatClient,
            source._customerSystemPrompt ? source._systemPrompt : rubric is null ? null : SystemPromptFor(rubric, dimension),
            source._customerSystemPrompt ? source._systemPromptId : rubric?.Id)
    {
        _customerSystemPrompt = source._customerSystemPrompt;
        _rubric = rubric;
        _dimension = rubric?.Dimensional == true && !string.IsNullOrWhiteSpace(dimension) ? dimension : null;
        _promptOptions = options;
        _promptDefinition = definition;
        ProviderName = source.ProviderName;
        // A factory may configure a pack before an AtomicLlmEval binds its descriptor. Resolve only
        // once the evaluator key is known, or at invocation for the generic criteria judge.
        if (options is not null && definition is not null)
            ResolvedPrompt = new JudgePromptResolver().Resolve(definition, options);
    }

    /// <summary>Returns an independent judge with explicit prompt selection; the original judge remains unchanged.</summary>
    public ChatClientEvaluator WithPrompts(JudgePromptOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(this, _rubric, _dimension, options, _promptDefinition);
    }

    /// <inheritdoc />
    public IEvaluator WithPromptDefinition(JudgePromptDefinition definition, JudgePromptOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        definition.Validate();
        var rubric = definition.ResponseContract == JudgeResponseContract.RubricV1 ? definition.ToRubric() : null;
        return new ChatClientEvaluator(this, rubric, definition.Dimension, options ?? _promptOptions, definition);
    }

    private async Task<EvaluationResult> EvaluateConfiguredAsync(string input, string output, IEnumerable<string> criteria,
        EvaluationRequest? request, JudgePromptOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var declared = criteria.ToArray();
        var baseline = _promptDefinition ?? BuiltInJudgePrompts.ForEvaluator("judge.criteria", declared, _rubric);
        if (request?.JudgeCriteria is { } identified && _promptDefinition is null) baseline = baseline with { Criteria = identified };
        var resolved = new JudgePromptResolver().Resolve(baseline, options);
        var definition = resolved.Definition;
        if (definition.ResponseContract is not (JudgeResponseContract.CriteriaV1 or JudgeResponseContract.RubricV1))
            throw new ArgumentException("Expected: CriteriaV1 or RubricV1 for ChatClientEvaluator. Actual: a metric response contract. Suggestions: use the corresponding RAG metric. Because: each evaluator owns its response parser.");
        var user = JudgePromptRenderer.Render(definition, input, output,
            request?.Context is { } passages ? string.Join("\n\n", passages) : null,
            request?.GroundTruth, definition.Dimension, evidence: request?.Evidence);
        List<ChatMessage> messages = [new(ChatRole.System, definition.SystemText), new(ChatRole.User, user)];
        long? inputTokens = 0, outputTokens = 0, cachedTokens = 0;
        var (parsed, response) = await InvokeAndParseAsync(messages, ct, definition).ConfigureAwait(false);
        Accumulate(ref inputTokens, ref outputTokens, ref cachedTokens, response?.Usage);
        if (parsed.EvaluationFailed)
        {
            messages.Add(new(ChatRole.User, definition.RetryText));
            var (retry, retryResponse) = await InvokeAndParseAsync(messages, ct, definition).ConfigureAwait(false);
            Accumulate(ref inputTokens, ref outputTokens, ref cachedTokens, retryResponse?.Usage);
            parsed = retry;
        }
        return new()
        {
            OverallScore = parsed.OverallScore, Summary = parsed.Summary, Improvements = parsed.Improvements,
            CriteriaResults = parsed.CriteriaResults, CriteriaResultsValid = parsed.CriteriaResultsValid,
            EvaluationFailed = parsed.EvaluationFailed, RubricScore = parsed.RubricScore,
            RubricSeverity = parsed.RubricSeverity, JudgeLabel = parsed.JudgeLabel, Evidence = parsed.Evidence,
            RubricDigest = resolved.Provenance.Digest, JudgePrompt = JudgePromptRenderer.Provenance(resolved, user),
            InputTokenCount = inputTokens, OutputTokenCount = outputTokens, CachedInputTokenCount = cachedTokens
        };
    }
}
