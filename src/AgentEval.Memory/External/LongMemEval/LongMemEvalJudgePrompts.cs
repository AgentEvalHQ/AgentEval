// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
//
// Judge prompt templates adapted from the official LongMemEval evaluation code.
// Reference: https://github.com/xiaowu0162/LongMemEval
// Original License: MIT — Di Wu et al. (ICLR 2025)

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// The 5 type-specific judge prompt templates matching the official LongMemEval benchmark.
/// Each template expects the judge to respond with "yes" or "no".
/// </summary>
internal static class LongMemEvalJudgePrompts
{
    /// <summary>
    /// Standard judge prompt for: single-session-user, single-session-assistant, multi-session.
    /// Strict: must contain the correct answer. Subset = fail.
    /// </summary>
    public static string Standard(string question, string goldAnswer, string hypothesis) => $"""
        I will give you a question, the correct answer to the question, and a response from a model. Please answer yes if the response from the model contains the correct answer to the question, and no if it does not. If the response only contains a subset of the information required by the answer, answer no.

        Question: {question}
        Correct answer: {goldAnswer}
        Model response: {hypothesis}

        Does the model response contain the correct answer? Answer yes or no only.
        """;

    /// <summary>
    /// Preference judge prompt for: single-session-preference.
    /// Flexible: correct as long as it recalls and utilizes the user's personal information.
    /// </summary>
    public static string Preference(string question, string goldAnswer, string hypothesis) => $"""
        I will give you a question, a rubric for desired personalized response, and a response from a model. Please answer yes if the response satisfies the desired response, and no if it does not. The model does not need to reflect all the points in the rubric. The response is correct as long as it recalls and utilizes the user's personal information correctly.

        Question: {question}
        Desired response rubric: {goldAnswer}
        Model response: {hypothesis}

        Does the model response satisfy the desired response? Answer yes or no only.
        """;

    /// <summary>
    /// Temporal judge prompt for: temporal-reasoning.
    /// Tolerant: allows off-by-one errors on day/week/month counts.
    /// </summary>
    public static string Temporal(string question, string goldAnswer, string hypothesis) => $"""
        I will give you a question, the correct answer to the question, and a response from a model. Please answer yes if the response from the model contains the correct answer to the question, and no if it does not. If the response only contains a subset of the information required by the answer, answer no. Do not penalize off-by-one errors for the number of days. If the question asks for the number of days/weeks/months, etc., and the model makes off-by-one errors (e.g., predicting 19 days when the answer is 18), the model's response is still correct.

        Question: {question}
        Correct answer: {goldAnswer}
        Model response: {hypothesis}

        Does the model response contain the correct answer? Answer yes or no only.
        """;

    /// <summary>
    /// Knowledge-update judge prompt for: knowledge-update.
    /// Tolerant: old info alongside updated answer is still correct.
    /// </summary>
    public static string KnowledgeUpdate(string question, string goldAnswer, string hypothesis) => $"""
        I will give you a question, the correct answer to the question, and a response from a model. Please answer yes if the response from the model contains the correct answer to the question, and no if it does not. If the response contains some previous information along with an updated answer, the response should be considered as correct as long as the updated answer is the required answer.

        Question: {question}
        Correct answer: {goldAnswer}
        Model response: {hypothesis}

        Does the model response contain the correct answer? Answer yes or no only.
        """;

    /// <summary>
    /// Abstention judge prompt for questions with _abs suffix in question_id.
    /// Tests whether the model correctly identifies the question as unanswerable.
    /// </summary>
    public static string Abstention(string question, string hypothesis) => $"""
        I will give you a question and a response from a model. The question asks about events or information that do not exist in the conversation history. Please answer yes if the model correctly identifies the question as unanswerable. The model could say that the information is incomplete, or some other information is given but the asked information is not.

        Question: {question}
        Model response: {hypothesis}

        Does the model correctly identify the question as unanswerable? Answer yes or no only.
        """;
}
