// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics;
using AgentEval.Decisions;
using AgentEval.Memory.External.Models;

namespace AgentEval.Memory.External;

/// <summary>
/// A decision model behind <see cref="IExternalBenchmarkJudge"/>: the memory benchmarks' judge seam.
/// </summary>
/// <remarks>
/// <para>
/// The memory judges answer one narrow question per item — <i>does this response contain the gold answer?</i>
/// — which is the shape a decision model is built for: no explanation is stored, no partial credit is given,
/// and the verdict is binary. This adapter asks exactly that as a <see cref="BinaryQuestion"/> and maps
/// P(yes) ≥ <see cref="Threshold"/> to <see cref="JudgeOutcomeStatus.Yes"/>.
/// </para>
/// <para>
/// <b>An empty response is not a wrong answer.</b> The memory benchmarks distinguish an abstention from an
/// incorrect answer, and a judge that scores silence as "No" quietly converts one into the other — the
/// defect that made 78% of one probe's answers uncitable. A blank or whitespace response is returned as
/// <see cref="JudgeOutcomeStatus.Empty"/> without a provider call.
/// </para>
/// <para>
/// <b>A transport failure is not a verdict.</b> <see cref="DecisionClientException"/> propagates: the caller
/// decides whether to retry or fail the run. Returning "incorrect" for a 429 would silently depress a score.
/// </para>
/// <para>
/// ⚠ <b>Comparison and calibration only, until a per-shape agreement study says otherwise.</b> TypedMemEval's
/// shipped judge agreement bar is 0.999; nothing here has been measured against it. Use this to
/// <i>measure</i> a decision model against the incumbent judge, not to grade a citable run with it.
/// </para>
/// </remarks>
public sealed class DecisionBenchmarkJudge : IExternalBenchmarkJudge
{
    /// <summary>The question id used on the wire; the protocol pairs answers to it.</summary>
    private const string QuestionId = "contains_gold";

    private readonly IDecisionClient _client;
    private readonly string _requestedModel;

    /// <summary>P(yes) at or above this counts as correct. 0.5 unless a calibration says otherwise.</summary>
    public double Threshold { get; }

    /// <summary>The model id the provider echoed on the last call, for provenance.</summary>
    public string? LastEchoedModel { get; private set; }

    /// <summary>Creates the adapter.</summary>
    /// <param name="client">The decision-model transport.</param>
    /// <param name="requestedModel">The model id to request (the provider may resolve it to a build).</param>
    /// <param name="threshold">P(yes) at or above which the answer counts as correct.</param>
    public DecisionBenchmarkJudge(IDecisionClient client, string requestedModel, double threshold = 0.5)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentException.ThrowIfNullOrWhiteSpace(requestedModel);
        if (!double.IsFinite(threshold) || threshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "threshold must be a finite value in [0, 1].");
        _requestedModel = requestedModel;
        Threshold = threshold;
    }

    /// <summary>The instruction sent for every item. Kept here so a run can record exactly what was asked.</summary>
    public const string Instructions =
        "The QUESTION was asked about a long conversation. GOLD is the correct answer. " +
        "Does the RESPONSE give that same answer? Wording may differ; the fact must match. " +
        "Extra correct detail is fine. A different fact, a contradiction, or a refusal to answer is not a match.";

    /// <summary>The exact state a call sends for an item, so a dry run can print it without sending.</summary>
    public static string BuildState(string agentResponse, ExternalBenchmarkQuestion question) =>
        $"QUESTION:\n{question.Question}\n\nGOLD:\n{question.GoldAnswer}\n\nRESPONSE:\n{agentResponse}";

    /// <summary>The exact request a call would send.</summary>
    public DecisionRequest BuildRequest(string agentResponse, ExternalBenchmarkQuestion question) =>
        new(BuildState(agentResponse, question),
            new Dictionary<string, DecisionQuestion>(StringComparer.Ordinal)
            {
                [QuestionId] = new BinaryQuestion(
                    Instructions,
                    "The response gives the gold answer.",
                    "The response does not give the gold answer."),
            },
            _requestedModel);

    /// <inheritdoc/>
    public async Task<ExternalJudgmentResult> JudgeAsync(
        string agentResponse, ExternalBenchmarkQuestion question, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(question);

        // Silence is its own outcome. Judging it would spend a call to turn an abstention into a wrong answer.
        if (string.IsNullOrWhiteSpace(agentResponse))
        {
            return new ExternalJudgmentResult
            {
                Status = JudgeOutcomeStatus.Empty,
                Correct = null,
                RawScore = null,
                Explanation = "The response was empty; no judgement was requested.",
                LlmCallCount = 0,
            };
        }

        var sw = Stopwatch.StartNew();
        var response = await _client.DecideAsync(BuildRequest(agentResponse, question), ct).ConfigureAwait(false);
        sw.Stop();

        if (!response.Answers.TryGetValue(QuestionId, out var answer) || answer is not BinaryAnswer binary)
        {
            // The protocol layer already refuses this; the defensive twin keeps a malformed reply from
            // becoming a verdict here either.
            throw new DecisionClientException(
                DecisionFailureKind.InvalidResponse,
                $"The decision judge expected a binary answer for '{QuestionId}' and did not receive one.");
        }

        LastEchoedModel = response.Model;
        var probability = binary.TrueProbability;
        var correct = probability >= Threshold;

        return new ExternalJudgmentResult
        {
            Status = correct ? JudgeOutcomeStatus.Yes : JudgeOutcomeStatus.No,
            Correct = correct,
            // The probability IS the score, on the 0-100 scale this result type uses. It is kept because a
            // threshold sweep needs the number, not the verdict it was collapsed into.
            RawScore = probability * 100.0,
            Explanation = $"P(gold answer present) = {probability:F3} (threshold {Threshold:F2}) — {response.Model}",
            TokensUsed = (int)Math.Min(int.MaxValue, (response.Usage?.InputTokens ?? 0) + (response.Usage?.OutputTokens ?? 0)),
            LlmCallCount = 1,
        };
    }
}
