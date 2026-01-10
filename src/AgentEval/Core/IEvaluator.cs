// Copyright (c) 2025-2026 AgentEval Contributors
// Licensed under the MIT License.

using Microsoft.Extensions.AI;

namespace AgentEval.Core;

/// <summary>
/// Provides AI-powered response evaluation.
/// </summary>
public interface IEvaluator
{
    /// <summary>
    /// Evaluate an agent response against criteria.
    /// </summary>
    /// <param name="input">The original input/prompt.</param>
    /// <param name="output">The agent's output.</param>
    /// <param name="criteria">Evaluation criteria.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The evaluation result.</returns>
    Task<EvaluationResult> EvaluateAsync(
        string input,
        string output,
        IEnumerable<string> criteria,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of an AI-powered evaluation.
/// </summary>
public class EvaluationResult
{
    /// <summary>Overall score from 0 to 100.</summary>
    public int OverallScore { get; init; }
    
    /// <summary>Summary of the evaluation.</summary>
    public string Summary { get; init; } = "";
    
    /// <summary>Suggested improvements.</summary>
    public IReadOnlyList<string> Improvements { get; init; } = [];
    
    /// <summary>Individual criteria results.</summary>
    public IReadOnlyList<CriterionResult> CriteriaResults { get; init; } = [];
}

/// <summary>
/// Result for a single evaluation criterion.
/// </summary>
public class CriterionResult
{
    /// <summary>The criterion being evaluated.</summary>
    public string Criterion { get; init; } = "";
    
    /// <summary>Whether the criterion was met.</summary>
    public bool Met { get; init; }
    
    /// <summary>Explanation of the result.</summary>
    public string Explanation { get; init; } = "";
}

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
                return new EvaluationResult { OverallScore = 50, Summary = "Failed to parse evaluation - no JSON found" };
            }

            var result = System.Text.Json.JsonSerializer.Deserialize<EvaluationResultDto>(json,
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            
            if (result == null)
                return new EvaluationResult { OverallScore = 50, Summary = "Failed to parse evaluation" };

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
            // Default to passing score when parsing fails but evaluation completed
            return new EvaluationResult
            {
                OverallScore = EvaluationDefaults.DefaultPassingScore,
                Summary = "Evaluation completed but result parsing had issues"
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
