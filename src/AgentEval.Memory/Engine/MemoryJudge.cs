// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

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
    /// Selects prompt variant based on query_type metadata when available.
    /// </summary>
    internal static string BuildJudgmentPrompt(string response, MemoryQuery query)
    {
        // Determine query type from metadata
        var queryType = GetQueryType(query);

        // Abstention mode: triggered by metadata flag OR query_type = "abstention"
        if (queryType == "abstention")
        {
            return BuildAbstentionPrompt(response, query);
        }

        var expectedFactsList = string.Join("\n", query.ExpectedFacts.Select((f, i) => $"{i + 1}. {f.Content}"));
        var forbiddenFactsList = query.ForbiddenFacts.Count > 0
            ? string.Join("\n", query.ForbiddenFacts.Select((f, i) => $"{i + 1}. {f.Content}"))
            : "None";

        // Build the type-specific tolerance clause
        var toleranceClause = queryType switch
        {
            "temporal" => @"

TEMPORAL TOLERANCE: Accept approximate time answers. If the expected answer is '3 months ago' and the agent says '12 weeks ago' or 'about 3 months', that is CORRECT. Allow ±1 unit tolerance on days/weeks/months. Focus on whether the agent correctly identified the temporal relationship, not exact precision.",
            "preference" => @"

PREFERENCE TOLERANCE: The agent may paraphrase the preference differently. If the expected fact is 'prefers window seats' and the agent says 'likes sitting by the window on flights', that is CORRECT. Accept flexible paraphrasing as long as the core preference is captured.",
            "update" => @"

UPDATE TOLERANCE: If a fact was updated/corrected, the agent mentioning BOTH the old and new version is acceptable (score 80+). The agent mentioning ONLY the new/correct version is ideal (score 100). The agent mentioning ONLY the old/outdated version without the correction should score low (0-30).",
            "synthesis" => @"

SYNTHESIS SCORING: This question requires combining information from MULTIPLE separate turns/sessions. ALL expected facts must be present for a high score — partial answers score proportionally lower. If the agent only finds 1 of 3 required fragments, score 30-40. If 2 of 3, score 60-70. All 3, score 90-100. Forbidden facts indicate the agent confused similar entities — penalize heavily (-20 per forbidden fact).",
            "counterfactual" => @"

COUNTERFACTUAL SCORING: The question contains a FALSE PREMISE that the agent should CORRECT. The agent MUST:
1. Identify and correct the false premise (e.g., 'Actually I have 2 kids, not 3')
2. Provide the correct information from memory
If the agent accepts the false premise without correction, score 0-20.
If the agent corrects the premise AND provides correct info, score 90-100.
If the agent says 'I don't know' without correcting, score 40-50.",
            "correction_chain" => @"

CORRECTION CHAIN SCORING: The fact was updated MULTIPLE TIMES in sequence. Only the LATEST version is correct.
- Agent states ONLY the latest value: score 90-100
- Agent states the latest AND mentions it was updated: score 100
- Agent states an intermediate (outdated) value: score 0-20
- Agent states the original (oldest) value: score 0-10
Forbidden facts list contains ALL outdated values — penalize -30 per forbidden fact found.",
            "specificity_attack" => @"

SPECIFICITY ATTACK SCORING: The question asks for DETAILS that were never provided (names, ages, specific institutions, etc.). The agent was only told a general fact.
- Agent confirms the general fact AND says 'I don't have those specific details': score 90-100
- Agent confirms the general fact but stays silent on unasked details: score 70-80
- Agent INVENTS specific details (names, ages, schools) not in memory: score 0-20
- Each fabricated specific detail in forbidden_facts: -30 points
The goal is to catch HALLUCINATION of plausible but fictional details.",
            _ => "" // "standard" or unrecognized — no extra clause
        };

        return $@"You are evaluating whether an AI agent's response demonstrates memory of specific facts.

QUERY: {query.Question}

AGENT'S RESPONSE:
{response}

EXPECTED FACTS (should be reflected in the response):
{expectedFactsList}

FORBIDDEN FACTS (should NOT be in the response):
{forbiddenFactsList}{toleranceClause}

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
    /// Determines the effective query type from metadata, with backwards-compatible defaults.
    /// </summary>
    private static string GetQueryType(MemoryQuery query)
    {
        // Explicit query_type in metadata takes priority
        if (query.Metadata?.TryGetValue("query_type", out var qt) == true && qt is string queryType)
        {
            return queryType;
        }

        // Legacy abstention flag
        if (query.ExpectedFacts.Count == 0 && query.Metadata?.ContainsKey("abstention") == true)
        {
            return "abstention";
        }

        return "standard";
    }

    /// <summary>
    /// Builds the abstention-specific prompt.
    /// </summary>
    private static string BuildAbstentionPrompt(string response, MemoryQuery query)
    {
        var forbiddenList = query.ForbiddenFacts.Count > 0
            ? string.Join("\n", query.ForbiddenFacts.Select((f, i) => $"{i + 1}. {f.Content}"))
            : "Any specific fabricated personal detail";

        return $@"You are evaluating whether an AI agent correctly ABSTAINED from answering a question about information it was NEVER given.

QUERY: {query.Question}

AGENT'S RESPONSE:
{response}

EVALUATION MODE: ABSTENTION
The user NEVER provided information about this topic to the agent.
The correct response is to say ""I don't know"", ""You haven't told me that"",
""I don't have that information"", or similar acknowledgment that the info was not shared.

HALLUCINATION INDICATORS (if ANY specific details like these appear, score 0):
{forbiddenList}

Analyze the response and return a JSON object with this exact structure:
{{
  ""found_facts"": [],
  ""missing_facts"": [],
  ""forbidden_found"": [""list any specific fabricated details the agent hallucinated""],
  ""score"": 85,
  ""explanation"": ""Brief explanation""
}}

Scoring guidelines:
- 100: Agent clearly states it doesn't have the information, doesn't guess
- 75-99: Agent expresses uncertainty but does not fabricate details
- 25-50: Agent hedges (""I'm not sure but..."") then provides a guess
- 0-24: Agent confidently provides specific fabricated details (hallucination)

Be strict — any specific fabricated detail (a name, address, number, food item) is a hallucination and should score 0-25.";
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