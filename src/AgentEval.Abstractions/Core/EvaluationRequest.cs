// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Core;

/// <summary>The source of the context actually supplied to a judge.</summary>
public enum EvaluationContextSource
{
    /// <summary>No context was available.</summary>
    None,
    /// <summary>Explicit context from the test case or caller.</summary>
    Fixture,
    /// <summary>Retrieval captured by the agent for this invocation.</summary>
    Retrieved
}

/// <summary>An immutable, invocation-owned judge request. Context is evidence, never instructions.</summary>
public sealed class EvaluationRequest
{
    /// <summary>Per-invocation prompt selection for a judge supporting declarative instruments.</summary>
    public Prompts.JudgePromptOptions? JudgePrompts { get; init; }

    /// <summary>Reference answer, separate from retrieved context and the target input.</summary>
    public string? GroundTruth { get; init; }

    /// <summary>Creates a request, copying the criteria and ordered context passages.</summary>
    /// <param name="input">The original agent input.</param>
    /// <param name="output">The agent output.</param>
    /// <param name="criteria">The declared evaluation criteria.</param>
    /// <param name="context">Ordered passages; null means unavailable, an empty collection means captured empty.</param>
    /// <param name="contextSource">Source of supplied passages; must not be None when context is supplied.</param>
    /// <param name="requiresContext">Whether unavailable context prevents a judgement.</param>
    public EvaluationRequest(string input, string output, IEnumerable<string> criteria,
        IEnumerable<string>? context = null,
        EvaluationContextSource contextSource = EvaluationContextSource.Fixture,
        bool requiresContext = false)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(criteria);
        if (!Enum.IsDefined(contextSource) || (context is not null && contextSource == EvaluationContextSource.None))
            throw new ArgumentException("Expected: a defined source for supplied context. Actual: invalid context source. " +
                "Suggestions: use Fixture or Retrieved. Because: evidence must have an explicit origin.", nameof(contextSource));

        Input = input;
        Output = output;
        Criteria = Array.AsReadOnly(criteria.ToArray());
        Context = context is null ? null : Array.AsReadOnly(context.ToArray());
        if (Context?.Any(p => p is null) == true)
            throw new ArgumentException("Expected: non-null context passages. Actual: a null passage. " +
                "Suggestions: supply an empty collection for captured-empty retrieval. Because: missing evidence is not text.", nameof(context));
        ContextSource = Context is null ? EvaluationContextSource.None : contextSource;
        RequiresContext = requiresContext;
    }

    /// <summary>The original agent input, unchanged by context propagation.</summary>
    public string Input { get; }
    /// <summary>The output being judged.</summary>
    public string Output { get; }
    /// <summary>The snapshotted criteria.</summary>
    public IReadOnlyList<string> Criteria { get; }
    /// <summary>Snapshotted passages; null is unavailable, empty is captured empty.</summary>
    public IReadOnlyList<string>? Context { get; }
    /// <summary>The actual source, or None when context is unavailable.</summary>
    public EvaluationContextSource ContextSource { get; }
    /// <summary>Whether unavailable context prevents a judgement.</summary>
    public bool RequiresContext { get; }

    /// <summary>Stable criterion identities, when supplied. Legacy requests carry only Criteria.</summary>
    public IReadOnlyList<JudgeCriterion>? JudgeCriteria { get; private init; }

    /// <summary>Copies this request with invocation-owned prompt selection, preserving all evidence and criterion IDs.</summary>
    public EvaluationRequest WithPrompts(Prompts.JudgePromptOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new(Input, Output, Criteria, Context, ContextSource, RequiresContext)
        { JudgeCriteria = JudgeCriteria, GroundTruth = GroundTruth, JudgePrompts = options };
    }

    /// <summary>Creates a request with snapshotted, unique criterion identities and localized text.</summary>
    public static EvaluationRequest ForCriteria(string input, string output, IEnumerable<JudgeCriterion> criteria,
        IEnumerable<string>? context = null, EvaluationContextSource contextSource = EvaluationContextSource.Fixture,
        bool requiresContext = false)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        var snapshot = criteria.ToArray();
        if (snapshot.Any(c => c is null) || snapshot.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
            throw new ArgumentException("Expected: non-null criteria with unique IDs. Actual: null or duplicate criteria. " +
                "Suggestions: assign a distinct stable ID to each criterion. Because: verdicts must join unambiguously.", nameof(criteria));
        return new EvaluationRequest(input, output, snapshot.Select(c => c.Text), context, contextSource, requiresContext)
        {
            JudgeCriteria = Array.AsReadOnly(snapshot)
        };
    }
}
