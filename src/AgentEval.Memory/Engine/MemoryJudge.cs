using AgentEval.Memory.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.Memory.Engine;

/// <summary>
/// LLM-based memory judge that evaluates whether facts are present in agent responses.
/// Uses structured prompting and JSON parsing for reliable fact detection.
/// </summary>
public class MemoryJudge : IMemoryJudge
{
    private readonly IChatClient _chatClient;
    private readonly ILogger<MemoryJudge> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public MemoryJudge(IChatClient chatClient, ILogger<MemoryJudge> logger)
    {
        _chatClient = chatClient;
        _logger = logger;
        
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    /// Evaluates whether expected facts are present in an agent's response using LLM analysis.
    /// </summary>
    public async Task<MemoryJudgmentResult> JudgeAsync(
        string response, 
        MemoryQuery query, 
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(response);
        ArgumentNullException.ThrowIfNull(query);
        
        _logger.LogDebug("Judging memory query: {Question}", query.Question);
        
        try
        {
            // Build the judgment prompt
            var promptText = BuildJudgmentPrompt(response, query);
            var messages = new[] { new ChatMessage(ChatRole.User, promptText) };
            
            // Get LLM analysis
            var chatResponse = await _chatClient.GetResponseAsync(
                messages,
                options: null,
                cancellationToken: cancellationToken);
            
            var responseText = chatResponse.Text ?? "";
            
            // Parse the structured response
            var judgmentData = ParseJudgmentResponse(responseText);
            
            // Convert to our result format
            var result = ConvertToJudgmentResult(judgmentData, query);
            
            _logger.LogDebug("Memory judgment completed: {Score}% ({FoundCount} found, {MissingCount} missing)",
                result.Score, result.FoundFacts.Count, result.MissingFacts.Count);
                
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during memory judgment for query: {Question}", query.Question);
            
            // Return a safe fallback result
            return new MemoryJudgmentResult
            {
                Score = 0,
                FoundFacts = Array.Empty<MemoryFact>(),
                MissingFacts = query.ExpectedFacts.ToArray(),
                ForbiddenFound = Array.Empty<MemoryFact>(),
                Explanation = $"Error during judgment: {ex.Message}",
                TokensUsed = 0
            };
        }
    }

    /// <summary>
    /// Builds a structured prompt for LLM-based fact verification.
    /// </summary>
    private static string BuildJudgmentPrompt(string response, MemoryQuery query)
    {
        var expectedFactsList = string.Join("\n", query.ExpectedFacts.Select((f, i) => $"{i + 1}. {f.Content}"));
        var forbiddenFactsList = query.ForbiddenFacts.Count > 0
            ? string.Join("\n", query.ForbiddenFacts.Select((f, i) => $"{i + 1}. {f.Content}"))
            : "None";

        return $@"You are evaluating whether an AI agent's response demonstrates memory of specific facts.

QUERY: {query.Question}

AGENT'S RESPONSE:
{response}

EXPECTED FACTS (should be reflected in the response):
{expectedFactsList}

FORBIDDEN FACTS (should NOT be in the response):
{forbiddenFactsList}

Analyze the response and return a JSON object with this exact structure:
{{
  ""found_facts"": [""list of expected fact contents that are clearly reflected in the response""],
  ""missing_facts"": [""list of expected fact contents that are missing or unclear in the response""],
  ""forbidden_found"": [""list of forbidden fact contents that were incorrectly mentioned in the response""],
  ""score"": 85,
  ""explanation"": ""Brief explanation of the scoring decision""
}}

Scoring guidelines:
- 100: All expected facts clearly present, no forbidden facts
- 80-99: Most expected facts present, minor gaps
- 60-79: Some expected facts present, significant gaps
- 40-59: Few expected facts present, major gaps
- 20-39: Minimal expected facts present
- 0-19: No or very few expected facts present
- Subtract 10-20 points per forbidden fact found

Be precise - only mark facts as ""found"" if they are clearly and specifically mentioned or implied in the response.";
    }

    /// <summary>
    /// Parses the LLM's structured JSON response.
    /// </summary>
    private JudgmentResponseData ParseJudgmentResponse(string responseText)
    {
        try
        {
            // Extract JSON from response (in case there's extra text)
            var jsonStart = responseText.IndexOf('{');
            var jsonEnd = responseText.LastIndexOf('}');
            
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var jsonStr = responseText.Substring(jsonStart, jsonEnd - jsonStart + 1);
                return JsonSerializer.Deserialize<JudgmentResponseData>(jsonStr, _jsonOptions) 
                       ?? throw new JsonException("Deserialized to null");
            }
            
            throw new JsonException("No valid JSON found in response");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Failed to parse judgment response as JSON: {Error}. Response: {Response}", 
                ex.Message, responseText);
            
            // Fallback: try to extract basic information
            return FallbackParseResponse(responseText);
        }
    }

    /// <summary>
    /// Fallback parsing when structured JSON fails.
    /// </summary>
    private static JudgmentResponseData FallbackParseResponse(string responseText)
    {
        // Simple heuristic: look for score patterns
        var scoreMatch = System.Text.RegularExpressions.Regex.Match(responseText, @"\b(\d{1,3})\b");
        var score = scoreMatch.Success ? int.Parse(scoreMatch.Groups[1].Value) : 50;
        
        return new JudgmentResponseData
        {
            FoundFacts = Array.Empty<string>(),
            MissingFacts = Array.Empty<string>(),
            ForbiddenFound = Array.Empty<string>(),
            Score = Math.Min(100, Math.Max(0, score)),
            Explanation = "Fallback parsing - LLM response was not in expected JSON format"
        };
    }

    /// <summary>
    /// Converts parsed LLM judgment data to our result format.
    /// </summary>
    private MemoryJudgmentResult ConvertToJudgmentResult(JudgmentResponseData data, MemoryQuery query)
    {
        // Match fact contents to original MemoryFact objects
        var foundFacts = MatchFactsByContent(data.FoundFacts, query.ExpectedFacts);
        var missingFacts = MatchFactsByContent(data.MissingFacts, query.ExpectedFacts);
        var forbiddenFound = MatchFactsByContent(data.ForbiddenFound, query.ForbiddenFacts);
        
        // Estimate token usage (rough approximation)
        var estimatedTokens = EstimateTokenUsage(query, data.Explanation ?? "");
        
        return new MemoryJudgmentResult
        {
            Score = data.Score,
            FoundFacts = foundFacts,
            MissingFacts = missingFacts,
            ForbiddenFound = forbiddenFound,
            Explanation = data.Explanation,
            TokensUsed = estimatedTokens
        };
    }

    /// <summary>
    /// Matches fact content strings to original MemoryFact objects.
    /// </summary>
    private static IReadOnlyList<MemoryFact> MatchFactsByContent(
        IReadOnlyList<string> factContents, 
        IReadOnlyList<MemoryFact> originalFacts)
    {
        var matched = new List<MemoryFact>();
        
        foreach (var content in factContents)
        {
            var matchedFact = originalFacts.FirstOrDefault(f => 
                string.Equals(f.Content, content, StringComparison.OrdinalIgnoreCase));
            
            if (matchedFact != null)
            {
                matched.Add(matchedFact);
            }
        }
        
        return matched;
    }

    /// <summary>
    /// Estimates token usage for cost tracking.
    /// </summary>
    private static int EstimateTokenUsage(MemoryQuery query, string explanation)
    {
        // Rough estimation: 4 characters per token
        var promptLength = query.Question.Length + 
                          query.ExpectedFacts.Sum(f => f.Content.Length) +
                          query.ForbiddenFacts.Sum(f => f.Content.Length) +
                          500; // Base prompt template
        
        var responseLength = explanation.Length + 200; // JSON structure overhead
        
        return (promptLength + responseLength) / 4;
    }
}

/// <summary>
/// Internal data structure for parsing LLM judgment responses.
/// </summary>
internal class JudgmentResponseData
{
    [JsonPropertyName("found_facts")]
    public IReadOnlyList<string> FoundFacts { get; set; } = Array.Empty<string>();
    
    [JsonPropertyName("missing_facts")]
    public IReadOnlyList<string> MissingFacts { get; set; } = Array.Empty<string>();
    
    [JsonPropertyName("forbidden_found")]
    public IReadOnlyList<string> ForbiddenFound { get; set; } = Array.Empty<string>();
    
    [JsonPropertyName("score")]
    public double Score { get; set; }
    
    [JsonPropertyName("explanation")]
    public string? Explanation { get; set; }
}