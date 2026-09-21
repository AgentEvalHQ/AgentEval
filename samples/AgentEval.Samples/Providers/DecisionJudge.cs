// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using AgentEval.Core;
using AgentEval.Decisions;

namespace AgentEval.Samples.Providers;

/// <summary>
/// A decision model behind the judge interface the agentic evaluators already use (<see cref="IEvaluator"/>).
/// State = the input and the output; one <see cref="BinaryQuestion"/> per criterion; ONE request per judge
/// call. The result is the shape <c>AtomicLlmEval</c> reads: <c>OverallScore</c> = 100 × mean P(met), one
/// <see cref="CriterionResult"/> per criterion with <c>Met</c> = P(met) ≥ 0.5.
/// </summary>
/// <remarks>
/// <para>
/// <b>For calibration runs and judge comparisons only.</b> An eval tree that reaches this through
/// <c>AtomicLlmEval</c> carries <c>provenance.type = "atomic-llm"</c> and a judge model that is not an LLM.
/// The persisted kind for a decision model is <c>DecisionEval</c> (<c>"atomic-decision"</c>). Sample N3 keeps
/// every result in memory, prints this caveat, and persists nothing through this adapter.
/// </para>
/// <para>
/// The criterion text is the question. Nothing is paraphrased: the generative judge and the decision model
/// receive the same criteria the evaluator was written with, which is the whole point of the comparison.
/// </para>
/// </remarks>
internal sealed class DecisionJudge : IEvaluator
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

    private readonly IDecisionClient _client;
    private readonly string _requestedModel;
    private readonly Action<Trace>? _onTrace;

    public DecisionJudge(IDecisionClient client, string requestedModel, Action<Trace>? onTrace = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _requestedModel = string.IsNullOrWhiteSpace(requestedModel) ? throw new ArgumentException("requestedModel", nameof(requestedModel)) : requestedModel;
        _onTrace = onTrace;
    }

    /// <summary>The state every question is asked about. The projection is the same for every criterion.</summary>
    public static string BuildState(string input, string output) =>
        $"INPUT (the user's request, or the conversation so far):\n{input}\n\nOUTPUT (the agent's response under evaluation):\n{output}";

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
        new(BuildState(input, output), BuildQuestions(criteria), _requestedModel);

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
