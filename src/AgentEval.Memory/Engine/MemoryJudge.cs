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

            // Extract actual token usage from the response when available
            var actualTokens = ExtractTokenUsage(chatResponse);

            // Parse the structured response
            var judgmentData = ParseJudgmentResponse(responseText);

            // Convert to our result format
            var result = ConvertToJudgmentResult(judgmentData, query, actualTokens);
            
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
        // Look for explicit score patterns like "score: 85", "Score: 85%", "85/100", "85 out of 100"
        var scoreMatch = System.Text.RegularExpressions.Regex.Match(
            responseText,
            @"(?:score\s*[:=]\s*(\d{1,3})|(\d{1,3})\s*[/]\s*100|(\d{1,3})\s*(?:out of|percent|%))",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var score = 50; // Default when no score pattern is found
        if (scoreMatch.Success)
        {
            var matched = scoreMatch.Groups[1].Success ? scoreMatch.Groups[1].Value
                        : scoreMatch.Groups[2].Success ? scoreMatch.Groups[2].Value
                        : scoreMatch.Groups[3].Value;
            score = int.Parse(matched);
        }

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
    /// Extracts actual token usage from the ChatResponse when available.
    /// </summary>
    private static int ExtractTokenUsage(ChatResponse response)
    {
        if (response.Usage is { } usage)
        {
            var input = (int)(usage.InputTokenCount ?? 0);
            var output = (int)(usage.OutputTokenCount ?? 0);
            if (input > 0 || output > 0)
                return input + output;
        }
        return 0;
    }

    /// <summary>
    /// Converts parsed LLM judgment data to our result format.
    /// </summary>
    private MemoryJudgmentResult ConvertToJudgmentResult(JudgmentResponseData data, MemoryQuery query, int actualTokens)
    {
        // Match fact contents to original MemoryFact objects
        var foundFacts = MatchFactsByContent(data.FoundFacts, query.ExpectedFacts);
        var missingFacts = MatchFactsByContent(data.MissingFacts, query.ExpectedFacts);
        var forbiddenFound = MatchFactsByContent(data.ForbiddenFound, query.ForbiddenFacts);

        // Use actual token counts when available, fall back to estimation
        var tokensUsed = actualTokens > 0
            ? actualTokens
            : EstimateTokenUsage(query, data.Explanation ?? "");

        return new MemoryJudgmentResult
        {
            Score = data.Score,
            FoundFacts = foundFacts,
            MissingFacts = missingFacts,
            ForbiddenFound = forbiddenFound,
            Explanation = data.Explanation,
            TokensUsed = tokensUsed
        };
    }

    /// <summary>
    /// Matches fact content strings from LLM output to original MemoryFact objects.
    /// Uses fuzzy matching because LLMs often paraphrase or slightly alter fact text
    /// (e.g., "My name is John" might be returned as "The user's name is John").
    /// </summary>
    private static IReadOnlyList<MemoryFact> MatchFactsByContent(
        IReadOnlyList<string> factContents,
        IReadOnlyList<MemoryFact> originalFacts)
    {
        var matched = new List<MemoryFact>();
        var alreadyMatched = new HashSet<int>();

        foreach (var content in factContents)
        {
            // Try exact match first, then contains-based, then keyword overlap
            var matchIndex = -1;

            for (int i = 0; i < originalFacts.Count; i++)
            {
                if (alreadyMatched.Contains(i)) continue;

                var fact = originalFacts[i];

                // Exact match
                if (string.Equals(fact.Content, content, StringComparison.OrdinalIgnoreCase))
                {
                    matchIndex = i;
                    break;
                }

                // Contains match (either direction)
                if (fact.Content.Contains(content, StringComparison.OrdinalIgnoreCase) ||
                    content.Contains(fact.Content, StringComparison.OrdinalIgnoreCase))
                {
                    matchIndex = i;
                    break;
                }

                // Keyword overlap: weaker signal, so record but keep looking for an exact/contains match
                if (matchIndex < 0 && HasSignificantOverlap(content, fact.Content))
                {
                    matchIndex = i;
                    // Don't break — a subsequent exact or contains match is preferred
                }
            }

            if (matchIndex >= 0)
            {
                matched.Add(originalFacts[matchIndex]);
                alreadyMatched.Add(matchIndex);
            }
        }

        return matched;
    }

    /// <summary>
    /// Stop words filtered out during keyword overlap matching.
    /// Static to avoid allocating a new set on every call (called hundreds of times per benchmark).
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
        "have", "has", "had", "do", "does", "did", "will", "would", "could",
        "should", "may", "might", "shall", "can", "to", "of", "in", "for",
        "on", "with", "at", "by", "from", "or", "and", "not", "no", "but",
        "if", "that", "this", "it", "its", "my", "your", "user", "users"
    };

    /// <summary>
    /// Checks whether two strings share enough significant keywords to be considered a match.
    /// </summary>
    private static bool HasSignificantOverlap(string text1, string text2)
    {
        var words1 = text1.Split([' ', ',', '.', '!', '?', ':', ';', '\'', '"'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !StopWords.Contains(w))
            .Select(w => w.ToLowerInvariant())
            .ToHashSet();

        var words2 = text2.Split([' ', ',', '.', '!', '?', ':', ';', '\'', '"'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !StopWords.Contains(w))
            .Select(w => w.ToLowerInvariant())
            .ToHashSet();

        if (words1.Count == 0 || words2.Count == 0) return false;

        var overlap = words1.Intersect(words2).Count();
        var smaller = Math.Min(words1.Count, words2.Count);

        // Require at least 50% keyword overlap with the smaller set
        return overlap >= Math.Max(1, smaller * 0.5);
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