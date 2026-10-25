// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentEval.Core;

/// <summary>
/// Helper for parsing LLM JSON responses in evaluation metrics.
/// </summary>
internal static class LlmJsonParser
{
    // A judge's reply is read leniently on form (a trailing comma, a comment) and strictly on content (the score).
    private static readonly JsonDocumentOptions ReplyOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// The texts in a judge's reply that may hold its JSON verdict, in the order to try them: once the reasoning blocks
    /// (<c>&lt;think&gt;…&lt;/think&gt;</c>) are removed, the body of every <c>```</c> fence (any language tag, any
    /// case; an unclosed fence runs to the end), then every top-level balanced <c>{…}</c> object. With neither, the whole
    /// reply, so a bare scalar such as <c>85</c> is read and refused as not an object. No regex anywhere: a regex
    /// timeout must never read as "no match".
    /// </summary>
    internal static IEnumerable<string> JsonCandidates(string reply) => Candidates(reply).Select(c => c.Text);

    // JsonCandidates, each marked whether it is the whole reply (the fallback when there is no fence and no object).
    private static IEnumerable<(string Text, bool WholeReply)> Candidates(string reply)
    {
        var text = StripReasoning(reply);
        var found = false;
        foreach (var body in FenceBodies(text))
        {
            found = true;
            yield return (body, false);
        }

        foreach (var obj in BalancedObjects(text))
        {
            found = true;
            yield return (obj, false);
        }

        if (!found && text.Trim() is { Length: > 0 } whole)
            yield return (whole, true);
    }

    /// <summary>
    /// Parses one candidate as a JSON object (a trailing comma and comments allowed). The element is detached from the
    /// parsed document, so it outlives it.
    /// </summary>
    internal static bool TryParseObject(string candidate, out JsonElement root, out JudgeReplyFailure failure)
    {
        root = default;
        try
        {
            using var doc = JsonDocument.Parse(candidate, ReplyOptions);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                failure = JudgeReplyFailure.NotAnObject;
                return false;
            }

            root = doc.RootElement.Clone();
            failure = JudgeReplyFailure.None;
            return true;
        }
        catch (JsonException)
        {
            failure = JudgeReplyFailure.InvalidJson;
            return false;
        }
    }

    /// <summary>
    /// Reads a metric judge's reply: the JSON object among <see cref="JsonCandidates"/> that carries a <c>score</c>. A
    /// quoted number is read in the invariant culture. A score that is not finite or is off 0-100 is refused, never
    /// clamped, and objects that disagree on the score are refused as ambiguous rather than one of them guessed.
    /// </summary>
    /// <returns>True with <paramref name="parsed"/> set; false with <paramref name="failure"/> saying why.</returns>
    internal static bool TryParseMetricResponse(
        string? reply, [NotNullWhen(true)] out MetricParseResult? parsed, out JudgeReplyFailure failure)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(reply))
        {
            failure = JudgeReplyFailure.Empty;
            return false;
        }

        // When no candidate has a usable score, the reason given is the one that came closest to a verdict.
        var closest = JudgeReplyFailure.NoJson;
        MetricParseResult? first = null;
        foreach (var (candidate, wholeReply) in Candidates(reply))
        {
            if (!TryParseObject(candidate, out var root, out var why))
            {
                // A reply with no fence and no object that does not parse is prose: no JSON, not invalid JSON.
                if (wholeReply && why == JudgeReplyFailure.InvalidJson)
                    why = JudgeReplyFailure.NoJson;
                if (why > closest) closest = why;
                continue;
            }

            if (!root.TryGetProperty("score", out var scoreProp))
            {
                if (JudgeReplyFailure.NoScore > closest) closest = JudgeReplyFailure.NoScore;
                continue;
            }

            if (!TryReadScore(scoreProp, out var score))
            {
                if (JudgeReplyFailure.ScoreNotANumber > closest) closest = JudgeReplyFailure.ScoreNotANumber;
                continue;
            }

            if (first is null)
            {
                first = new MetricParseResult
                {
                    Score = score,
                    Reasoning = root.TryGetProperty("reasoning", out var reasoningProp)
                                && reasoningProp.ValueKind == JsonValueKind.String
                        ? reasoningProp.GetString()
                        : null,
                    // Additional arrays (claims, issues, etc.)
                    Root = root,
                };
            }
            else if (!first.Score.Equals(score))
            {
                failure = JudgeReplyFailure.Ambiguous;
                return false;
            }
        }

        if (first is null)
        {
            failure = closest;
            return false;
        }

        if (!double.IsFinite(first.Score) || first.Score < 0 || first.Score > 100)
        {
            failure = JudgeReplyFailure.ScoreOutOfRange;
            return false;
        }

        parsed = first;
        failure = JudgeReplyFailure.None;
        return true;
    }

    // Some models quote the number. Read it the same on every machine: in es-ES or de-DE "85.5" read as 855.
    private static bool TryReadScore(JsonElement score, out double value)
    {
        if (score.ValueKind == JsonValueKind.Number)
            return score.TryGetDouble(out value);
        if (score.ValueKind == JsonValueKind.String)
            return double.TryParse(score.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        value = 0;
        return false;
    }

    // Removes every <think>…</think> block (ordinal, any case). An unclosed <think> drops the rest of the reply; a closing
    // tag with no opening one before it (a provider that strips the opening tag) drops everything before it.
    private static string StripReasoning(string reply)
    {
        const string Open = "<think>";
        const string Close = "</think>";
        var firstOpen = reply.IndexOf(Open, StringComparison.OrdinalIgnoreCase);
        var firstClose = reply.IndexOf(Close, StringComparison.OrdinalIgnoreCase);
        if (firstOpen < 0 && firstClose < 0)
            return reply;
        if (firstClose >= 0 && (firstOpen < 0 || firstClose < firstOpen))
            reply = reply[(firstClose + Close.Length)..];

        var kept = new StringBuilder(reply.Length);
        var at = 0;
        while (true)
        {
            var open = reply.IndexOf(Open, at, StringComparison.OrdinalIgnoreCase);
            if (open < 0)
            {
                kept.Append(reply, at, reply.Length - at);
                break;
            }

            kept.Append(reply, at, open - at);
            var close = reply.IndexOf(Close, open + Open.Length, StringComparison.OrdinalIgnoreCase);
            if (close < 0)
                break;   // unclosed: the rest is reasoning
            at = close + Close.Length;
        }

        return kept.ToString();
    }

    // The body of every ``` fence, after its language tag (```json, ```JSON, ```). An unclosed fence runs to the end.
    private static IEnumerable<string> FenceBodies(string text)
    {
        var at = 0;
        while ((at = text.IndexOf("```", at, StringComparison.Ordinal)) >= 0)
        {
            var start = at + 3;
            while (start < text.Length && (char.IsLetterOrDigit(text[start]) || text[start] is '_' or '-' or '+'))
                start++;
            var end = text.IndexOf("```", start, StringComparison.Ordinal);
            var body = (end < 0 ? text[start..] : text[start..end]).Trim();
            if (body.Length > 0)
                yield return body;
            if (end < 0)
                yield break;
            at = end + 3;
        }
    }

    // Every top-level balanced {…} object, in one string-aware pass (the scanner of LongMemEvalStructuredVerdict, which
    // stops at the first object). Quotes count only inside an object, so a quote in the prose around one cannot hide it.
    // An object left open does not hide the closed objects inside it: the outermost closed ones are returned, in order.
    private static IEnumerable<string> BalancedObjects(string text)
    {
        var open = new Stack<int>();
        var closed = new List<(int Start, int End)>();
        var inString = false;
        var escaped = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped)
                    escaped = false;
                else if (c == '\\')
                    escaped = true;
                else if (c == '"')
                    inString = false;
                continue;
            }

            switch (c)
            {
                case '"' when open.Count > 0:
                    inString = true;
                    break;
                case '{':
                    open.Push(i);
                    break;
                case '}' when open.Count > 0:
                    closed.Add((open.Pop(), i));
                    break;
            }
        }

        // Braces nest, so two closed objects are nested or apart: in start order, keep each one that begins after the
        // last one kept ended.
        closed.Sort((a, b) => a.Start.CompareTo(b.Start));
        var lastEnd = -1;
        foreach (var (start, end) in closed)
        {
            if (start <= lastEnd)
                continue;
            lastEnd = end;
            yield return text[start..(end + 1)];
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
                // Guard ValueKind: JsonElement.GetString() throws InvalidOperationException on a
                // non-string item (e.g. a number/object the LLM put in the array). That is NOT a
                // JsonException, so it escaped the metrics' catch(JsonException) and crashed the
                // whole evaluation instead of degrading gracefully (BUG-08). Skip non-strings.
                if (item.ValueKind != JsonValueKind.String) continue;
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
    /// Gets a numeric property as a double, returning <paramref name="defaultValue"/> when the
    /// property is missing or is not a JSON number. Unlike <see cref="JsonElement.GetDouble()"/>
    /// this never throws <see cref="InvalidOperationException"/> on a non-number value (BUG-08).
    /// </summary>
    public static double GetDouble(JsonElement element, string propertyName, double defaultValue = 0)
    {
        if (element.TryGetProperty(propertyName, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetDouble(out var value))
        {
            return value;
        }
        return defaultValue;
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

    /// <summary>
    /// Gets a string property value, returning <paramref name="defaultValue"/> when the property is missing
    /// or is not a JSON string. Unlike <see cref="JsonElement.GetString()"/> this never throws
    /// <see cref="InvalidOperationException"/> on a non-string value (BUG-08 / N-01).
    /// </summary>
    public static string GetString(JsonElement element, string propertyName, string defaultValue = "")
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString() ?? defaultValue;
        }
        return defaultValue;
    }
}

/// <summary>
/// Why a judge's reply could not be read (<see cref="LlmJsonParser.TryParseMetricResponse"/>), ordered from the furthest
/// from a verdict to the closest.
/// </summary>
internal enum JudgeReplyFailure
{
    /// <summary>The reply was read.</summary>
    None,

    /// <summary>The reply was empty.</summary>
    Empty,

    /// <summary>No JSON object was found.</summary>
    NoJson,

    /// <summary>The JSON found is not valid.</summary>
    InvalidJson,

    /// <summary>The JSON found is not an object (a bare <c>85</c>, <c>null</c>, an array).</summary>
    NotAnObject,

    /// <summary>The object has no score.</summary>
    NoScore,

    /// <summary>The score is not a number.</summary>
    ScoreNotANumber,

    /// <summary>The score is not finite or is off 0-100: refused, never clamped.</summary>
    ScoreOutOfRange,

    /// <summary>The reply holds objects with different scores: refused rather than one of them guessed.</summary>
    Ambiguous,
}

/// <summary>
/// Result of parsing an LLM metric response.
/// </summary>
internal class MetricParseResult
{
    /// <summary>The score, on 0-100: <see cref="LlmJsonParser.TryParseMetricResponse"/> refuses one off the scale.</summary>
    public double Score { get; set; }

    /// <summary>The reasoning explanation.</summary>
    public string? Reasoning { get; set; }
    
    /// <summary>The root JSON element for additional property access.</summary>
    public JsonElement Root { get; set; }
}
