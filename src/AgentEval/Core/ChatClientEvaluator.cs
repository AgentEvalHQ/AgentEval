// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace AgentEval.Core;

/// <summary>
/// Default implementation of IEvaluator using an IChatClient.
/// </summary>
public class ChatClientEvaluator : IEvaluator
{
    private readonly IChatClient _chatClient;
    private readonly string _systemPrompt;

    public ChatClientEvaluator(IChatClient chatClient, string? systemPrompt = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _systemPrompt = systemPrompt ?? DefaultSystemPrompt;
    }

    private const string DefaultSystemPrompt = """
        You are a Test Evaluator Agent that assesses the quality of AI agent outputs.
        
        For each criterion, determine if it was met (true/false) and explain why.
        Provide an overall score (0-100) and specific improvement suggestions.
        
        Always respond in valid JSON format only - no markdown code blocks.
        Use this structure:
        {
            "criteriaResults": [{"criterion": "...", "met": true, "explanation": "..."}],
            "overallScore": 75,
            "summary": "Brief summary of the evaluation",
            "improvements": ["suggestion 1", "suggestion 2"]
        }
        """;

    public async Task<EvaluationResult> EvaluateAsync(
        string input,
        string output,
        IEnumerable<string> criteria,
        CancellationToken cancellationToken = default)
    {
        var criteriaList = string.Join("\n", criteria.Select((c, i) => $"{i + 1}. {c}"));
        
        var prompt = $"""
            Evaluate the following agent output:

            INPUT:
            {input}

            OUTPUT:
            {output}

            CRITERIA TO EVALUATE:
            {criteriaList}
            """;

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, _systemPrompt),
            new(ChatRole.User, prompt)
        };

        var response = await _chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken);
        
        return ParseEvaluationResponse(response.Text);
    }

    private static EvaluationResult ParseEvaluationResponse(string responseText)
    {
        try
        {
            var json = LlmJsonParser.ExtractJson(responseText);
            if (json == null)
            {
                return new EvaluationResult { OverallScore = EvaluationDefaults.DefaultFailureScore, Summary = "Failed to parse evaluation - no JSON found" };
            }

            var result = System.Text.Json.JsonSerializer.Deserialize<EvaluationResultDto>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            
            if (result == null)
                return new EvaluationResult { OverallScore = EvaluationDefaults.DefaultFailureScore, Summary = "Failed to parse evaluation" };

            return new EvaluationResult
            {
                OverallScore = result.OverallScore,
                Summary = result.Summary ?? "",
                Improvements = result.Improvements ?? [],
                CriteriaResults = result.CriteriaResults?.Select(c => new CriterionResult
                {
                    Criterion = c.Criterion ?? "",
                    Met = c.Met,
                    Explanation = c.Explanation ?? ""
                }).ToList() ?? []
            };
        }
        catch
        {
            // Return failure score when evaluation parsing fails to indicate evaluation system error
            return new EvaluationResult
            {
                OverallScore = EvaluationDefaults.DefaultFailureScore,
                Summary = "Failed to parse evaluation result"
            };
        }
    }

    // DTO for JSON deserialization
    private class EvaluationResultDto
    {
        public int OverallScore { get; set; }
        public string? Summary { get; set; }
        public List<string>? Improvements { get; set; }
        public List<CriterionResultDto>? CriteriaResults { get; set; }
    }

    private class CriterionResultDto
    {
        public string? Criterion { get; set; }
        public bool Met { get; set; }
        public string? Explanation { get; set; }
    }
}
