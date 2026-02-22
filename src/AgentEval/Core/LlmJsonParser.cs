// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;

namespace AgentEval.Core;

/// <summary>
/// Helper for parsing LLM JSON responses in evaluation metrics.
/// </summary>
internal static class LlmJsonParser
{
    /// <summary>
    /// Extracts JSON from a potentially decorated LLM response (with markdown, etc.).
    /// Handles markdown code blocks (```json and ```).
    /// </summary>
    /// <param name="response">The LLM response text.</param>
    /// <returns>The extracted JSON string, or the original text if no markdown wrapper found.</returns>
    public static string? ExtractJson(string? response)
    {
        if (string.IsNullOrEmpty(response))
            return null;
        
        // Try to extract from markdown code blocks first
        if (response.Contains("```json"))
        {
            var start = response.IndexOf("```json") + 7;
            var end = response.IndexOf("```", start);
            if (end > start)
                return response[start..end].Trim();
        }
        else if (response.Contains("```"))
        {
            var start = response.IndexOf("```") + 3;
            var end = response.IndexOf("```", start);
            if (end > start)
                return response[start..end].Trim();
        }
        
        // If no markdown wrapper, try to extract raw JSON by finding braces
        var jsonStart = response.IndexOf('{');
        var jsonEnd = response.LastIndexOf('}');
        
        if (jsonStart >= 0 && jsonEnd > jsonStart)
        {
            return response[jsonStart..(jsonEnd + 1)];
        }
        
        // Return trimmed original text as fallback
        return response.Trim();
    }
    
    /// <summary>
    /// Safely parses a JSON response and extracts common metric fields.
    /// </summary>
    /// <param name="response">The LLM response text.</param>
    /// <returns>Parsed result or null if parsing fails.</returns>
    public static MetricParseResult? ParseMetricResponse(string? response)
    {
        var json = ExtractJson(response);
        if (json == null)
            return null;
        
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            var result = new MetricParseResult();
            
            // Score is required
            if (!root.TryGetProperty("score", out var scoreProp))
                return null;
            
            // Handle score as number or string (some LLMs return string)
            if (scoreProp.ValueKind == JsonValueKind.Number)
            {
                result.Score = scoreProp.GetDouble();
            }
            else if (scoreProp.ValueKind == JsonValueKind.String)
            {
                var scoreStr = scoreProp.GetString();
                if (double.TryParse(scoreStr, out var parsedScore))
                {
                    result.Score = parsedScore;
                }
                else
                {
                    return null; // Invalid score format
                }
            }
            else
            {
                return null; // Unsupported score type
            }
            
            // Reasoning is optional
            if (root.TryGetProperty("reasoning", out var reasoningProp) && 
                reasoningProp.ValueKind == JsonValueKind.String)
            {
                result.Reasoning = reasoningProp.GetString();
            }
            
            // Additional arrays (claims, issues, etc.)
            result.Root = root.Clone();
            
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }
    
    /// <summary>
    /// Extracts a string array from a JSON element.
    /// </summary>
    /// <param name="element">The parent JSON element.</param>
    /// <param name="propertyName">The property name to extract.</param>
    /// <returns>List of strings, or empty list if not found or not an array.</returns>
    public static List<string> ExtractStringArray(JsonElement element, string propertyName)
    {
        var list = new List<string>();
        
        if (element.TryGetProperty(propertyName, out var prop) && 
            prop.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in prop.EnumerateArray())
            {
                var text = item.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    list.Add(text);
                }
            }
        }
        
        return list;
    }
    
    /// <summary>
    /// Gets a boolean property value with a default.
    /// </summary>
    public static bool GetBoolean(JsonElement element, string propertyName, bool defaultValue = false)
    {
        if (element.TryGetProperty(propertyName, out var prop) && 
            (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False))
        {
            return prop.GetBoolean();
        }
        return defaultValue;
    }
}

/// <summary>
/// Result of parsing an LLM metric response.
/// </summary>
internal class MetricParseResult
{
    private double _score;
    
    /// <summary>The score (0-100), clamped to valid range.</summary>
    public double Score 
    { 
        get => _score; 
        set => _score = Math.Clamp(value, 0.0, 100.0); 
    }
    
    /// <summary>The reasoning explanation.</summary>
    public string? Reasoning { get; set; }
    
    /// <summary>The root JSON element for additional property access.</summary>
    public JsonElement Root { get; set; }
}
