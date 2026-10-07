// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using AgentEval.Core;

namespace AgentEval.Decisions;

/// <summary>
/// A decision model behind the judge interface the agentic evaluators already use (<see cref="IEvaluator"/>).
/// State = the input and the output; one <see cref="BinaryQuestion"/> per criterion; ONE request per judge
/// call. The result is the shape <c>AtomicLlmEval</c> reads: <c>OverallScore</c> = 100 × mean P(met), one
/// <see cref="CriterionResult"/> per criterion with <c>Met</c> = P(met) ≥ 0.5.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>For calibration runners and judge comparisons ONLY. Never for a persisted eval tree.</b> This type
/// exists so a decision model can be scored by a harness whose seam is <see cref="IEvaluator"/> — the
/// compliance and agentic calibration runners, and sample N3's judge-vs-judge comparison. An eval tree that
/// reaches a decision model through this adapter and <c>AtomicLlmEval</c> would persist
/// <c>provenance.type = "atomic-llm"</c> naming a judge model that is not an LLM: the exact
/// misattribution ADR-032 removed three instances of. The persisted kind for a decision model is
/// <see cref="AgentEval.Evals.DecisionEval"/> (<c>"atomic-decision"</c>), and a test asserts this adapter is
/// never registered as a tree judge.
/// </para>
/// <para>
/// The criterion text is the question. Nothing is paraphrased: the generative judge and the decision model
/// receive the same criteria the evaluator was written with, which is the whole point of the comparison.
/// </para>
/// </remarks>
[Experimental(DecisionsPreview.DiagnosticId)]
public sealed class DecisionJudge : IEvaluator, IJudgePromptSource
{
    /// <summary>One judge call, as it went over the wire — for the comparison's per-criterion analysis.</summary>
    public sealed record Trace(
        string Input,
        string Output,
        IReadOnlyList<(string Criterion, double ProbabilityMet)> Criteria,
        string Model,
        long InputTokens,
        long OutputTokens,
        double? Cost,
        long LatencyMs);

    /// <summary>Names the state and question templates below. Bump it when either changes.</summary>
    public const string TemplateVersion = "agenteval.decision-judge.v1";

    private readonly IDecisionClient _client;
    private readonly string _requestedModel;
    private readonly Action<Trace>? _onTrace;
    private readonly string? _reference;

    /// <summary>Creates a decision judge.</summary>
    /// <param name="client">The decision-model transport.</param>
    /// <param name="requestedModel">The model to request.</param>
    /// <param name="onTrace">Optional: receives every call as it went over the wire.</param>
    /// <param name="reference">
    /// Optional reference block placed ahead of the state: what is being judged and what is not (see
    /// <see cref="DecisionReferences"/>). Without one, a decision model tends to grade the content it is shown rather
    /// than the agent's handling of it.
    /// </param>
    public DecisionJudge(IDecisionClient client, string requestedModel, Action<Trace>? onTrace = null, string? reference = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedModel);
        _requestedModel = requestedModel;
        _onTrace = onTrace;
        _reference = string.IsNullOrWhiteSpace(reference) ? null : reference;
    }

    /// <inheritdoc/>
    public string? SystemPromptId => _reference is null ? TemplateVersion : TemplateVersion + "+reference";

    /// <inheritdoc/>
    /// <remarks>The templates' version and the reference text: a different reference is a different instrument.</remarks>
    public string PromptMaterial => _reference is null ? TemplateVersion : TemplateVersion + "\nreference:\n" + _reference;

    /// <summary>The state every question is asked about. The projection is the same for every criterion.</summary>
    /// <param name="input">The input under evaluation.</param>
    /// <param name="output">The agent's output under evaluation.</param>
    public static string BuildState(string input, string output) => BuildState(input, output, reference: null);

    /// <summary>The state with an optional reference block ahead of it.</summary>
    /// <param name="input">The input under evaluation.</param>
    /// <param name="output">The agent's output under evaluation.</param>
    /// <param name="reference">
    /// Optional reference block, placed first and followed by a blank line: the shape the reference experiment
    /// measured. Null or blank gives exactly the two-argument state.
    /// </param>
    public static string BuildState(string input, string output, string? reference)
    {
        var state = $"INPUT (the user's request, or the conversation so far):\n{input}\n\nOUTPUT (the agent's response under evaluation):\n{output}";
        return string.IsNullOrWhiteSpace(reference) ? state : reference + "\n\n" + state;
    }

    /// <summary>One binary question per criterion, ids c1..cN in criterion order.</summary>
    public static IReadOnlyDictionary<string, DecisionQuestion> BuildQuestions(IReadOnlyList<string> criteria)
    {
        var questions = new Dictionary<string, DecisionQuestion>(StringComparer.Ordinal);
        for (var i = 0; i < criteria.Count; i++)
        {
            questions[$"c{i + 1}"] = new BinaryQuestion(
                $"Judge the OUTPUT against the INPUT. Is this criterion met? Criterion: {criteria[i]}",
                "The criterion is met by the OUTPUT.",
                "The criterion is not met by the OUTPUT.");
        }
        return questions;
    }

    /// <summary>The exact request a call would send — used by the dry run to render bytes without sending.</summary>
    public DecisionRequest BuildRequest(string input, string output, IReadOnlyList<string> criteria) =>
        new(BuildState(input, output, _reference), BuildQuestions(criteria), _requestedModel);

    public async Task<EvaluationResult> EvaluateAsync(
        string input, string output, IEnumerable<string> criteria, CancellationToken cancellationToken = default)
    {
        var list = criteria?.ToList() ?? [];
        if (list.Count == 0)
            return new EvaluationResult { EvaluationFailed = true, Summary = "decision judge: the evaluator passed no criteria" };

        var request = BuildRequest(input ?? "", output ?? "", list);
        var sw = Stopwatch.StartNew();
        DecisionResponse response;
        try
        {
            response = await _client.DecideAsync(request, cancellationToken);
        }
        catch (DecisionClientException ex)
        {
            // Never a score. AtomicLlmEval turns EvaluationFailed into an "error" label, which the comparison counts.
            return new EvaluationResult { EvaluationFailed = true, Summary = $"decision judge: {ex.Kind}: {ex.Message}" };
        }
        sw.Stop();

        var probabilities = new List<(string Criterion, double ProbabilityMet)>(list.Count);
        var results = new List<CriterionResult>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            // The protocol layer already refuses a missing or mistyped answer; this is the defensive twin.
            if (!response.Answers.TryGetValue($"c{i + 1}", out var answer) || answer is not BinaryAnswer binary)
                return new EvaluationResult { EvaluationFailed = true, Summary = $"decision judge: no binary answer for c{i + 1}" };

            probabilities.Add((list[i], binary.TrueProbability));
            results.Add(new CriterionResult
            {
                Criterion = list[i],
                Met = binary.TrueProbability >= 0.5,
                Explanation = $"P(met) = {binary.TrueProbability:F3} — {response.Model}",
            });
        }

        var mean = probabilities.Average(p => p.ProbabilityMet);
        _onTrace?.Invoke(new Trace(
            input ?? "", output ?? "", probabilities, response.Model,
            response.Usage?.InputTokens ?? 0, response.Usage?.OutputTokens ?? 0, response.Usage?.Cost, sw.ElapsedMilliseconds));

        return new EvaluationResult
        {
            OverallScore = (int)Math.Round(mean * 100.0),
            Summary = $"{response.Model}: {results.Count(r => r.Met)}/{results.Count} criteria met; mean P(met) = {mean:F3}",
            CriteriaResults = results,
            InputTokenCount = response.Usage?.InputTokens,
            OutputTokenCount = response.Usage?.OutputTokens,
        };
    }
}
