// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
//
// Judge prompt templates adapted from the official LongMemEval evaluation code.
// Reference: https://github.com/xiaowu0162/LongMemEval
// Original License: MIT — Di Wu et al. (ICLR 2025)

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// Four question-family judge templates plus the cross-cutting abstention template,
/// adapted from the official LongMemEval benchmark. Each template expects the judge
/// to respond with "yes" or "no".
/// </summary>
internal static class LongMemEvalJudgePrompts
{
    /// <summary>
    /// Standard judge prompt for: single-session-user, single-session-assistant, multi-session.
    /// Strict: must contain the correct answer. Subset = fail.
    /// </summary>
    public static string Standard(string question, string goldAnswer, string hypothesis) => $"""
        I will give you a question, a correct answer, and a response from a model. Please answer yes if the response contains the correct answer. Otherwise, answer no. If the response is equivalent to the correct answer or contains all the intermediate steps to get the correct answer, you should also answer yes. If the response only contains a subset of the information required by the answer, answer no.

        Question: {question}
        Correct Answer: {goldAnswer}
        Model Response: {hypothesis}

        Is the model response correct? Answer yes or no only.
        """;

    /// <summary>
    /// Preference judge prompt for: single-session-preference.
    /// Flexible: correct as long as it recalls and utilizes the user's personal information.
    /// </summary>
    public static string Preference(string question, string goldAnswer, string hypothesis) => $"""
        I will give you a question, a rubric for desired personalized response, and a response from a model. Please answer yes if the response satisfies the desired response. Otherwise, answer no. The model does not need to reflect all the points in the rubric. The response is correct as long as it recalls and utilizes the user's personal information correctly.

        Question: {question}
        Rubric: {goldAnswer}
        Model Response: {hypothesis}

        Is the model response correct? Answer yes or no only.
        """;

    /// <summary>
    /// Temporal judge prompt for: temporal-reasoning.
    /// Tolerant: allows off-by-one errors on day/week/month counts.
    /// </summary>
    public static string Temporal(string question, string goldAnswer, string hypothesis) => $"""
        I will give you a question, a correct answer, and a response from a model. Please answer yes if the response contains the correct answer. Otherwise, answer no. If the response is equivalent to the correct answer or contains all the intermediate steps to get the correct answer, you should also answer yes. If the response only contains a subset of the information required by the answer, answer no. In addition, do not penalize off-by-one errors for the number of days. If the question asks for the number of days/weeks/months, etc., and the model makes off-by-one errors (e.g., predicting 19 days when the answer is 18), the model's response is still correct.

        Question: {question}
        Correct Answer: {goldAnswer}
        Model Response: {hypothesis}

        Is the model response correct? Answer yes or no only.
        """;

    /// <summary>
    /// Knowledge-update judge prompt for: knowledge-update.
    /// Tolerant: old info alongside updated answer is still correct.
    /// </summary>
    public static string KnowledgeUpdate(string question, string goldAnswer, string hypothesis) => $"""
        I will give you a question, a correct answer, and a response from a model. Please answer yes if the response contains the correct answer. Otherwise, answer no. If the response contains some previous information along with an updated answer, the response should be considered as correct as long as the updated answer is the required answer.

        Question: {question}
        Correct Answer: {goldAnswer}
        Model Response: {hypothesis}

        Is the model response correct? Answer yes or no only.
        """;

    /// <summary>
    /// Abstention judge prompt for questions with _abs suffix in question_id.
    /// Tests whether the model correctly identifies the question as unanswerable.
    /// </summary>
    public static string Abstention(string question, string explanation, string hypothesis) => $"""
        I will give you an unanswerable question, an explanation, and a response from a model. Please answer yes if the model correctly identifies the question as unanswerable. The model could say that the information is incomplete, or some other information is given but the asked information is not.

        Question: {question}
        Explanation: {explanation}
        Model Response: {hypothesis}

        Does the model correctly identify the question as unanswerable? Answer yes or no only.
        """;

    /// <summary>
    /// The trailing instruction every template above shares. Removed before the structured-output
    /// contract is appended, so the prompt does not simultaneously demand bare prose and a JSON object.
    /// </summary>
    internal const string FreeTextClosingInstruction = "Answer yes or no only.";

    /// <summary>
    /// Rewrites a free-text judge prompt into its structured-output form: drops the bare-prose
    /// instruction and appends the JSON contract.
    /// </summary>
    internal static string ForStructuredOutput(string freeTextPrompt)
    {
        var withoutClosing = freeTextPrompt
            .Replace(FreeTextClosingInstruction, string.Empty, StringComparison.Ordinal)
            .TrimEnd();

        return withoutClosing + Environment.NewLine + LongMemEvalStructuredVerdict.PromptSuffix;
    }

    /// <summary>
    /// Single-predicate prompt used by <see cref="Models.JudgeDecompositionMode.PerPredicate"/>. Asks one
    /// narrow supported/not-supported question instead of an overall correctness judgment, so a long
    /// multi-part answer is not collapsed into a single decision.
    /// </summary>
    public static string Predicate(string question, string predicate, string hypothesis) => $"""
        I will give you a question, ONE required fact drawn from the correct answer, and a response from a model. Decide only whether the response supports that one required fact. Ignore anything else the response does or does not contain — other facts are judged separately. Answer yes if the response states the required fact, is equivalent to it, or contains all the intermediate steps to reach it. Answer no if it is absent, contradicted, or only partially present.

        Question: {question}
        Required Fact: {predicate}
        Model Response: {hypothesis}

        Does the model response support the required fact? Answer yes or no only.
        """;
}
