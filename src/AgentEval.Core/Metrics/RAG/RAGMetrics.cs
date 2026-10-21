// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;
using AgentEval.Core;
using AgentEval.Evals;

namespace AgentEval.Metrics.RAG;

/// <summary>
/// Measures whether the response is faithful to the provided context.
/// A faithful response only contains information that can be derived from the context.
/// </summary>
public class FaithfulnessMetric : IRAGMetric, IQualityMetric
{
    private readonly IChatClient _chatClient;
    
    public string Name => "llm_faithfulness";
    public string Description => "Measures if the response is grounded in and faithful to the provided context (no hallucinations).";
    public bool RequiresContext => true;
    public bool RequiresGroundTruth => false;
    
    /// <inheritdoc />
    public MetricCategory Categories => 
        MetricCategory.RAG | 
        MetricCategory.RequiresContext | 
        MetricCategory.Faithfulness | 
        MetricCategory.LLMEvaluated;
    
    public FaithfulnessMetric(IChatClient chatClient)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
    }
    
    public async Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Context))   // not measured, never a fail (B12i)
        {
            return MetricResult.NotMeasured(Name, NotMeasuredCause.MissingInput,
                "Faithfulness requires a retrieved context, and none was supplied: not measured.");
        }

        var prompt = BuildFaithfulnessPrompt(context.Context, context.Input, context.Output);

        // A failed judge call or an unreadable reply is not measured, never a Fail at 0.
        var (response, failed) = await JudgeReplyEvidence.AskAsync(_chatClient, prompt, Name, "Faithfulness", cancellationToken);

        return JudgeReplyEvidence.Model(failed ?? ParseFaithfulnessResult(response!), BuildFaithfulnessPrompt("", "", ""));
    }
    
    private static string BuildFaithfulnessPrompt(string contextText, string input, string output)
    {
        return """
            You are an expert evaluator assessing the faithfulness of a response to a given context.
            
            A faithful response only contains claims that can be directly inferred from the provided context.
            Any claim not supported by the context is considered a hallucination.
            
            CONTEXT:
            """ + contextText + """
            
            QUESTION:
            """ + input + """
            
            RESPONSE:
            <agent_response>
            """ + output + """
            </agent_response>

            Analyze the response for faithfulness. For each claim in the response:
            1. Is it directly supported by the context? 
            2. Is it a reasonable inference from the context?
            3. Is it a hallucination (not in context)?
            
            Respond with a JSON object:
            {
                "score": <0-100 faithfulness score>,
                "faithfulClaims": [<list of claims supported by context>],
                "hallucinatedClaims": [<list of claims NOT in context>],
                "reasoning": "<explanation of your analysis>"
            }
            
            Be strict: any unsupported claim should reduce the score significantly.
            """;
    }
    
    private MetricResult ParseFaithfulnessResult(ChatResponse response)
    {
        if (!LlmJsonParser.TryParseMetricResponse(response.Text, out var parsed, out var failure))
        {
            return JudgeReplyEvidence.NotMeasured(Name, "Faithfulness", response, failure);
        }
        
        var hallucinatedClaims = LlmJsonParser.ExtractStringArray(parsed.Root, "hallucinatedClaims");
        
        var metadata = new Dictionary<string, object>
        {
            ["reasoning"] = parsed.Reasoning ?? "",
            ["hallucinatedClaims"] = hallucinatedClaims
        };
        
        if (parsed.Score >= EvaluationDefaults.PassingScoreThreshold)
        {
            return MetricResult.Pass(Name, parsed.Score, $"Faithful: {parsed.Reasoning}", metadata);
        }
        else
        {
            return MetricResult.Fail(Name, $"Hallucinations detected: {string.Join("; ", hallucinatedClaims)}", parsed.Score, metadata);
        }
    }
}

/// <summary>
/// Measures how relevant the response is to the input query.
/// </summary>
public class RelevanceMetric : IRAGMetric, IQualityMetric
{
    private readonly IChatClient _chatClient;
    
    public string Name => "llm_relevance";
    public string Description => "Measures how relevant and on-topic the response is to the user's question.";
    public bool RequiresContext => false;
    public bool RequiresGroundTruth => false;
    
    /// <inheritdoc />
    public MetricCategory Categories => 
        MetricCategory.RAG | 
        MetricCategory.Relevance | 
        MetricCategory.LLMEvaluated;
    
    public RelevanceMetric(IChatClient chatClient)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
    }
    
    public async Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        var prompt = BuildRelevancePrompt(context.Input, context.Output);

        var (response, failed) = await JudgeReplyEvidence.AskAsync(_chatClient, prompt, Name, "Relevance", cancellationToken);
        return JudgeReplyEvidence.Model(failed ?? ParseRelevanceResult(response!), BuildRelevancePrompt("", ""));
    }
    
    private static string BuildRelevancePrompt(string input, string output)
    {
        return """
            You are an expert evaluator assessing the relevance of a response to a question.
            
            A relevant response:
            - Directly addresses the question asked
            - Stays on topic without unnecessary tangents
            - Provides information the user actually needs
            - Doesn't include irrelevant information
            
            QUESTION:
            """ + input + """
            
            RESPONSE:
            <agent_response>
            """ + output + """
            </agent_response>

            Evaluate the relevance of this response.
            
            Respond with a JSON object:
            {
                "score": <0-100 relevance score>,
                "addressesQuestion": <true/false>,
                "staysOnTopic": <true/false>,
                "irrelevantParts": [<list of off-topic content if any>],
                "reasoning": "<explanation>"
            }
            """;
    }
    
    private MetricResult ParseRelevanceResult(ChatResponse response)
    {
        if (!LlmJsonParser.TryParseMetricResponse(response.Text, out var parsed, out var failure))
        {
            return JudgeReplyEvidence.NotMeasured(Name, "Relevance", response, failure);
        }
        
        var addressesQuestion = LlmJsonParser.GetBoolean(parsed.Root, "addressesQuestion");
        
        var metadata = new Dictionary<string, object>
        {
            ["reasoning"] = parsed.Reasoning ?? "",
            ["addressesQuestion"] = addressesQuestion
        };
        
        if (parsed.Score >= EvaluationDefaults.PassingScoreThreshold)
        {
            return MetricResult.Pass(Name, parsed.Score, $"Relevant: {parsed.Reasoning}", metadata);
        }
        else
        {
            return MetricResult.Fail(Name, $"Low relevance: {parsed.Reasoning}", parsed.Score, metadata);
        }
    }
}

/// <summary>
/// Measures the precision of context retrieval - how much of the retrieved context is actually useful.
/// </summary>
public class ContextPrecisionMetric : IRAGMetric
{
    private readonly IChatClient _chatClient;
    
    public string Name => "llm_context_precision";
    public string Description => "Measures how much of the retrieved context is relevant and useful for answering the question.";
    public bool RequiresContext => true;
    public bool RequiresGroundTruth => false;

    /// <inheritdoc />
    public MetricCategory Categories =>
        MetricCategory.RAG |
        MetricCategory.RequiresContext |
        MetricCategory.LLMEvaluated;

    public ContextPrecisionMetric(IChatClient chatClient)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
    }
    
    public async Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Context))   // not measured, never a fail (B12i)
        {
            return MetricResult.NotMeasured(Name, NotMeasuredCause.MissingInput,
                "Context precision requires a retrieved context, and none was supplied: not measured.");
        }

        var prompt = BuildPrecisionPrompt(context.Input, context.Context);

        var (response, failed) = await JudgeReplyEvidence.AskAsync(_chatClient, prompt, Name, "Context precision", cancellationToken);
        return JudgeReplyEvidence.Model(failed ?? ParsePrecisionResult(response!), BuildPrecisionPrompt("", ""));
    }
    
    private static string BuildPrecisionPrompt(string input, string contextText)
    {
        return """
            You are an expert evaluator assessing the precision of retrieved context for a RAG system.
            
            Context precision measures: What fraction of the retrieved context is actually useful 
            for answering the question?
            
            QUESTION:
            """ + input + """
            
            RETRIEVED CONTEXT:
            """ + contextText + """
            
            Analyze the context and determine:
            1. Which parts are directly relevant to answering the question?
            2. Which parts are irrelevant noise?
            3. What's the ratio of useful to total content?
            
            Respond with a JSON object:
            {
                "score": <0-100 precision score>,
                "relevantParts": [<summaries of relevant sections>],
                "irrelevantParts": [<summaries of irrelevant sections>],
                "reasoning": "<explanation>"
            }
            
            Score guidance:
            - 100: All context is highly relevant
            - 70-99: Mostly relevant with some noise
            - 40-69: Mixed relevance
            - 0-39: Mostly irrelevant
            """;
    }
    
    private MetricResult ParsePrecisionResult(ChatResponse response)
    {
        if (!LlmJsonParser.TryParseMetricResponse(response.Text, out var parsed, out var failure))
        {
            return JudgeReplyEvidence.NotMeasured(Name, "Context precision", response, failure);
        }
        
        var metadata = new Dictionary<string, object> { ["reasoning"] = parsed.Reasoning ?? "" };
        
        if (parsed.Score >= EvaluationDefaults.PassingScoreThreshold)
        {
            return MetricResult.Pass(Name, parsed.Score, $"High precision: {parsed.Reasoning}", metadata);
        }
        else
        {
            return MetricResult.Fail(Name, $"Low precision: {parsed.Reasoning}", parsed.Score, metadata);
        }
    }
}

/// <summary>
/// Measures the recall of context - whether all necessary information is present.
/// </summary>
public class ContextRecallMetric : IRAGMetric
{
    private readonly IChatClient _chatClient;
    
    public string Name => "llm_context_recall";
    public string Description => "Measures whether the retrieved context contains all information needed to answer the question correctly.";
    public bool RequiresContext => true;
    public bool RequiresGroundTruth => true;

    /// <inheritdoc />
    public MetricCategory Categories =>
        MetricCategory.RAG |
        MetricCategory.RequiresContext |
        MetricCategory.RequiresGroundTruth |
        MetricCategory.LLMEvaluated;

    public ContextRecallMetric(IChatClient chatClient)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
    }
    
    public async Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(context.Context))   // not measured, never a fail (B12i)
        {
            return MetricResult.NotMeasured(Name, NotMeasuredCause.MissingInput,
                "Context recall requires a retrieved context, and none was supplied: not measured.");
        }

        if (!ReferenceText.HasWords(context.GroundTruth))   // a wordless reference ("?") is none (round 17)
        {
            return MetricResult.NotMeasured(Name, NotMeasuredCause.MissingInput,
                "Context recall requires a reference answer, and none was supplied: not measured.");
        }

        var prompt = BuildRecallPrompt(context.Input, context.GroundTruth, context.Context);

        var (response, failed) = await JudgeReplyEvidence.AskAsync(_chatClient, prompt, Name, "Context recall", cancellationToken);
        return JudgeReplyEvidence.Model(failed ?? ParseRecallResult(response!), BuildRecallPrompt("", "", ""));
    }
    
    private static string BuildRecallPrompt(string input, string groundTruth, string contextText)
    {
        return """
            You are an expert evaluator assessing context recall for a RAG system.
            
            Context recall measures: Does the retrieved context contain all the information 
            needed to produce the correct/expected answer?
            
            QUESTION:
            """ + input + """
            
            EXPECTED ANSWER (Ground Truth):
            """ + groundTruth + """
            
            RETRIEVED CONTEXT:
            """ + contextText + """
            
            Analyze whether the context contains enough information to derive the expected answer.
            
            Respond with a JSON object:
            {
                "score": <0-100 recall score>,
                "informationPresent": [<facts from ground truth found in context>],
                "informationMissing": [<facts from ground truth NOT in context>],
                "reasoning": "<explanation>"
            }
            
            Score guidance:
            - 100: All information needed is in context
            - 70-99: Most critical information present
            - 40-69: Some key information missing
            - 0-39: Most information missing
            """;
    }
    
    private MetricResult ParseRecallResult(ChatResponse response)
    {
        if (!LlmJsonParser.TryParseMetricResponse(response.Text, out var parsed, out var failure))
        {
            return JudgeReplyEvidence.NotMeasured(Name, "Context recall", response, failure);
        }
        
        var missingInfo = LlmJsonParser.ExtractStringArray(parsed.Root, "informationMissing");
        
        var metadata = new Dictionary<string, object>
        {
            ["reasoning"] = parsed.Reasoning ?? "",
            ["missingInformation"] = missingInfo
        };
        
        if (parsed.Score >= EvaluationDefaults.PassingScoreThreshold)
        {
            return MetricResult.Pass(Name, parsed.Score, $"Good recall: {parsed.Reasoning}", metadata);
        }
        else
        {
            return MetricResult.Fail(Name, $"Missing information: {string.Join("; ", missingInfo)}", parsed.Score, metadata);
        }
    }
}

/// <summary>
/// Measures the answer correctness compared to ground truth.
/// </summary>
public class AnswerCorrectnessMetric : IRAGMetric
{
    private readonly IChatClient _chatClient;
    
    public string Name => "llm_answer_correctness";
    public string Description => "Measures how correct the answer is compared to the expected ground truth.";
    public bool RequiresContext => false;
    public bool RequiresGroundTruth => true;

    /// <inheritdoc />
    public MetricCategory Categories =>
        MetricCategory.RAG |
        MetricCategory.RequiresGroundTruth |
        MetricCategory.LLMEvaluated;

    public AnswerCorrectnessMetric(IChatClient chatClient)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
    }
    
    public async Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        if (!ReferenceText.HasWords(context.GroundTruth))   // not measured, never a fail (B12i); "?" is none (round 17)
        {
            return MetricResult.NotMeasured(Name, NotMeasuredCause.MissingInput,
                "Answer correctness requires a reference answer, and none was supplied: not measured.");
        }

        var prompt = BuildCorrectnessPrompt(context.Input, context.GroundTruth, context.Output);

        var (response, failed) = await JudgeReplyEvidence.AskAsync(_chatClient, prompt, Name, "Answer correctness", cancellationToken);
        return JudgeReplyEvidence.Model(failed ?? ParseCorrectnessResult(response!), BuildCorrectnessPrompt("", "", ""));
    }
    
    private static string BuildCorrectnessPrompt(string input, string groundTruth, string output)
    {
        return """
            You are an expert evaluator assessing the correctness of an answer.
            
            Compare the generated answer to the expected answer (ground truth).
            Consider both factual accuracy and completeness.
            
            QUESTION:
            """ + input + """
            
            EXPECTED ANSWER (Ground Truth):
            """ + groundTruth + """
            
            GENERATED ANSWER:
            <agent_response>
            """ + output + """
            </agent_response>

            Evaluate the correctness:
            1. Are the facts accurate?
            2. Is anything incorrect?
            3. Is anything missing?
            4. Is anything extra (but correct)?
            
            Respond with a JSON object:
            {
                "score": <0-100 correctness score>,
                "factsCorrect": [<correct facts in answer>],
                "factsIncorrect": [<incorrect facts in answer>],
                "factsMissing": [<facts from ground truth not in answer>],
                "reasoning": "<explanation>"
            }
            """;
    }
    
    private MetricResult ParseCorrectnessResult(ChatResponse response)
    {
        if (!LlmJsonParser.TryParseMetricResponse(response.Text, out var parsed, out var failure))
        {
            return JudgeReplyEvidence.NotMeasured(Name, "Answer correctness", response, failure);
        }
        
        var metadata = new Dictionary<string, object> { ["reasoning"] = parsed.Reasoning ?? "" };
        
        if (parsed.Score >= EvaluationDefaults.PassingScoreThreshold)
        {
            return MetricResult.Pass(Name, parsed.Score, $"Correct: {parsed.Reasoning}", metadata);
        }
        else
        {
            return MetricResult.Fail(Name, $"Incorrect: {parsed.Reasoning}", parsed.Score, metadata);
        }
    }
}
