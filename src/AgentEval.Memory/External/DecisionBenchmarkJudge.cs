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

    /// <summary>The instruction for an ordinary question. Kept here so a run can record exactly what was asked.</summary>
    public const string Instructions =
        "The QUESTION was asked about a long conversation. GOLD is the correct answer. " +
        "Does the RESPONSE give that same answer? Wording may differ; the fact must match. " +
        "Extra correct detail is fine. A different fact, a contradiction, or a refusal to answer is not a match.";

    /// <summary>
    /// The instruction for an ABSTENTION question, where the conversation does not contain the answer and
    /// recognising that IS the correct behaviour. The ordinary rubric above says in so many words that a
    /// refusal is not a match, so sending it here would score every correct abstention as wrong — the
    /// shipped <c>LongMemEvalJudge</c> branches on the same flag for the same reason.
    /// </summary>
    public const string AbstentionInstructions =
        "The QUESTION cannot be answered from the conversation, and the correct behaviour is to say so. " +
        "Does the RESPONSE recognise that it does not know, cannot answer, or lacks the information? " +
        "Saying so in any wording is a match. Asserting a specific answer anyway is not.";

    /// <summary>
    /// <c>single-session-preference</c>: GOLD is a rubric for a personalised answer, not a fact to match.
    /// The shipped judge accepts a response that recalls and uses the user's preference even when it does
    /// not cover every point, so matching this strictly would fail correct personalised answers.
    /// </summary>
    public const string PreferenceInstructions =
        "The QUESTION asks for a personalised answer. GOLD is a RUBRIC describing what a good answer does, " +
        "not a literal answer to match. Does the RESPONSE recall and use the user's own preference or " +
        "situation correctly? It need not cover every point of the rubric.";

    /// <summary>
    /// <c>temporal-reasoning</c> and the time-grounded types: answers are dates and intervals derived from
    /// timestamps, and the shipped judge does not penalise an off-by-one day, week or month.
    /// </summary>
    public const string TemporalInstructions =
        "The QUESTION was asked about a long conversation. GOLD is the correct answer. " +
        "Does the RESPONSE give that same answer, or all the steps needed to reach it? " +
        "Do NOT penalise an off-by-one count of days, weeks or months — 19 days for a gold answer of 18 " +
        "is still a match. A different fact or a refusal to answer is not.";

    /// <summary>
    /// <c>knowledge-update</c>: the conversation changed a fact, and a response that mentions the old value
    /// alongside the current one is still correct. The strict rubric would fail it for the extra detail.
    /// </summary>
    public const string KnowledgeUpdateInstructions =
        "The QUESTION is about a fact the conversation UPDATED. GOLD is the current, correct answer. " +
        "Does the RESPONSE give that current answer? Mentioning the earlier value as well is fine, as long " +
        "as the current one is given. Giving only the earlier value is not a match.";

    /// <summary>
    /// The instruction this adapter would send for a question — the shipped judge's dispatch, mirrored.
    /// Public so a run can record exactly which rubric each item was judged under.
    /// </summary>
    public static string InstructionsFor(ExternalBenchmarkQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        // Abstention first: it is a cross-type concern carried by the question id, not the type.
        if (question.IsAbstention) return AbstentionInstructions;

        return question.QuestionType switch
        {
            "single-session-preference" => PreferenceInstructions,
            // The time-grounded probe's types judge like temporal-reasoning for the same reason: their
            // answers are dates and intervals derived from timestamps.
            "temporal-reasoning"
                or LongMemEval.LongMemEvalTimeGroundedCorpus.AsOfQuestionType
                or LongMemEval.LongMemEvalTimeGroundedCorpus.CurrentQuestionType
                or LongMemEval.LongMemEvalTimeGroundedCorpus.ProspectiveQuestionType => TemporalInstructions,
            "knowledge-update" => KnowledgeUpdateInstructions,
            _ => Instructions,
        };
    }

    /// <summary>The exact state a call sends for an item, so a dry run can print it without sending.</summary>
    public static string BuildState(string agentResponse, ExternalBenchmarkQuestion question) =>
        $"QUESTION:\n{question.Question}\n\nGOLD:\n{question.GoldAnswer}\n\nRESPONSE:\n{agentResponse}";

    /// <summary>The exact request a call would send.</summary>
    public DecisionRequest BuildRequest(string agentResponse, ExternalBenchmarkQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);
        var binary = question.IsAbstention
            ? new BinaryQuestion(
                AbstentionInstructions,
                "The response recognises that it cannot answer.",
                "The response asserts an answer instead of recognising it cannot answer.")
            : new BinaryQuestion(
                InstructionsFor(question),
                "The response satisfies the criterion above.",
                "The response does not satisfy the criterion above.");

        return new DecisionRequest(
            BuildState(agentResponse, question),
            new Dictionary<string, DecisionQuestion>(StringComparer.Ordinal) { [QuestionId] = binary },
            _requestedModel);
    }

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
            Explanation = question.IsAbstention
                ? $"P(response recognises it cannot answer) = {probability:F3} (threshold {Threshold:F2}) — {response.Model}"
                : $"P(gold answer present) = {probability:F3} (threshold {Threshold:F2}) — {response.Model}",
            TokensUsed = (int)Math.Min(int.MaxValue, (response.Usage?.InputTokens ?? 0) + (response.Usage?.OutputTokens ?? 0)),
            // One primary call, no retries: the whole accounting contract, not just the total. A consumer
            // reading PrimaryLlmCallCount would otherwise see no primary attempt for a call that happened.
            LlmCallCount = 1,
            PrimaryLlmCallCount = 1,
            AttemptsUsed = 1,
        };
    }
}
