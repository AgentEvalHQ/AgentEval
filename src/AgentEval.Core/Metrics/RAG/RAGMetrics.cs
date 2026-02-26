// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;
using AgentEval.Core;

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
        MetricCategory.LLMBased;
    
    public FaithfulnessMetric(IChatClient chatClient)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
    }
    
    public async Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(context.Context))
        {
            return MetricResult.Fail(Name, "Faithfulness requires context to be provided.");
        }
        
        var prompt = BuildFaithfulnessPrompt(context.Context, context.Input, context.Output);
        
        var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: cancellationToken);
        var responseText = response.Text ?? "";
        
        return ParseFaithfulnessResult(responseText);
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
            """ + output + """
            
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
    
    private MetricResult ParseFaithfulnessResult(string response)
    {
        var parsed = LlmJsonParser.ParseMetricResponse(response);
        if (parsed == null)
        {
            return MetricResult.Fail(Name, "Unable to evaluate faithfulness - failed to parse response.");
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
        MetricCategory.LLMBased;
    
    public RelevanceMetric(IChatClient chatClient)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
    }
    
    public async Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        var prompt = BuildRelevancePrompt(context.Input, context.Output);
        
        var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: cancellationToken);
        return ParseRelevanceResult(response.Text ?? "");
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
            """ + output + """
            
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
    
    private MetricResult ParseRelevanceResult(string response)
    {
        var parsed = LlmJsonParser.ParseMetricResponse(response);
        if (parsed == null)
        {
            return MetricResult.Fail(Name, "Unable to evaluate relevance - failed to parse response.");
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
    
    public ContextPrecisionMetric(IChatClient chatClient)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
    }
    
    public async Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(context.Context))
        {
            return MetricResult.Fail(Name, "Context precision requires context to be provided.");
        }
        
        var prompt = BuildPrecisionPrompt(context.Input, context.Context);
        
        var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: cancellationToken);
        return ParsePrecisionResult(response.Text ?? "");
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
    
    private MetricResult ParsePrecisionResult(string response)
    {
        var parsed = LlmJsonParser.ParseMetricResponse(response);
        if (parsed == null)
        {
            return MetricResult.Fail(Name, "Unable to evaluate context precision - failed to parse response.");
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
    
    public ContextRecallMetric(IChatClient chatClient)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
    }
    
    public async Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(context.Context))
        {
            return MetricResult.Fail(Name, "Context recall requires context to be provided.");
        }
        
        if (string.IsNullOrEmpty(context.GroundTruth))
        {
            return MetricResult.Fail(Name, "Context recall requires ground truth to be provided.");
        }
        
        var prompt = BuildRecallPrompt(context.Input, context.GroundTruth, context.Context);
        
        var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: cancellationToken);
        return ParseRecallResult(response.Text ?? "");
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
    
    private MetricResult ParseRecallResult(string response)
    {
        var parsed = LlmJsonParser.ParseMetricResponse(response);
        if (parsed == null)
        {
            return MetricResult.Fail(Name, "Unable to evaluate context recall - failed to parse response.");
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
    
    public AnswerCorrectnessMetric(IChatClient chatClient)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
    }
    
    public async Task<MetricResult> EvaluateAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(context.GroundTruth))
        {
            return MetricResult.Fail(Name, "Answer correctness requires ground truth to be provided.");
        }
        
        var prompt = BuildCorrectnessPrompt(context.Input, context.GroundTruth, context.Output);
        
        var response = await _chatClient.GetResponseAsync(prompt, cancellationToken: cancellationToken);
        return ParseCorrectnessResult(response.Text ?? "");
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
            """ + output + """
            
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
    
    private MetricResult ParseCorrectnessResult(string response)
    {
        var parsed = LlmJsonParser.ParseMetricResponse(response);
        if (parsed == null)
        {
            return MetricResult.Fail(Name, "Unable to evaluate answer correctness - failed to parse response.");
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
