// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using AgentEval.Core;
using Xunit;

namespace AgentEval.Tests.Core;

/// <summary>
/// Regression coverage for BUG-08: JsonElement.GetString()/GetDouble() throw
/// InvalidOperationException (NOT JsonException) on a wrong-typed value, which escaped the
/// metrics' catch(JsonException) and crashed the whole evaluation. The shared parser now
/// guards ValueKind so malformed LLM output degrades gracefully instead of crashing.
/// </summary>
public class LlmJsonParserTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void ExtractStringArray_NonStringItems_AreSkipped_NotThrown()
    {
        var el = Parse("""{ "items": ["a", 42, true, {"x":1}, "b"] }""");

        var result = LlmJsonParser.ExtractStringArray(el, "items");

        Assert.Equal(new[] { "a", "b" }, result);
    }

    [Fact]
    public void ExtractStringArray_AllStrings_ReturnsAll()
    {
        var el = Parse("""{ "items": ["x", "y", "z"] }""");

        Assert.Equal(new[] { "x", "y", "z" }, LlmJsonParser.ExtractStringArray(el, "items"));
    }

    [Theory]
    [InlineData("""{ "score": "eighty" }""", 50.0)]   // string → default (no throw)
    [InlineData("""{ "score": true }""", 50.0)]        // bool → default
    [InlineData("""{ "score": 80 }""", 80.0)]          // number → value
    [InlineData("""{ "other": 1 }""", 50.0)]           // missing → default
    public void GetDouble_GuardsNonNumberValues(string json, double expected)
    {
        Assert.Equal(expected, LlmJsonParser.GetDouble(Parse(json), "score", 50.0));
    }

    // T04: replies that hold a readable verdict. The old reader (first fence, else first '{' to last '}', strict JSON)
    // read none of them, and each became a Fail at score 0.
    [Theory]
    [InlineData("<think>format {score:n}</think>{\"score\":80}")]                // the first brace is in the reasoning
    [InlineData("```JSON\n{\"score\":80}\n```")]                                // an uppercase language tag
    [InlineData("Example: ```\nnot json\n```\n{\"score\":80}")]                 // the first fence is not the verdict
    [InlineData("{\"score\":80} Note: {see above}")]                             // first '{' to last '}' spans both
    [InlineData("{\"score\":80,\"reasoning\":\"ok\",}")]                        // a trailing comma
    [InlineData("{\"score\":80 /* ok */}")]                                      // a comment
    [InlineData("I weigh {the facts} first.</think>{\"score\":80}")]            // a provider stripped the opening tag
    public void TryParseMetricResponse_FindsTheVerdict(string reply)
    {
        Assert.True(LlmJsonParser.TryParseMetricResponse(reply, out var parsed, out var failure), failure.ToString());
        Assert.Equal(80, parsed!.Score);
    }

    [Theory]
    [InlineData("{\"score\":150}")]
    [InlineData("{\"score\":-5}")]
    [InlineData("{\"score\":\"NaN\"}")]
    public void TryParseMetricResponse_AScoreOffTheScale_IsRefused_NeverClamped(string reply)
    {
        // It was clamped: 150 read as a confident Pass at 100, -5 as a measured 0.
        Assert.False(LlmJsonParser.TryParseMetricResponse(reply, out _, out var failure));
        Assert.Equal(JudgeReplyFailure.ScoreOutOfRange, failure);
    }

    [Theory]
    [InlineData("85")]
    [InlineData("null")]
    [InlineData("true")]
    public void TryParseMetricResponse_ABareScalar_IsNotAnObject_NotAnException(string reply)
    {
        // The old reader called TryGetProperty on a non-object root, which throws InvalidOperationException.
        Assert.False(LlmJsonParser.TryParseMetricResponse(reply, out _, out var failure));
        Assert.Equal(JudgeReplyFailure.NotAnObject, failure);
    }

    [Fact]
    public void TryParseMetricResponse_ObjectsWithDifferentScores_AreAmbiguous_NotGuessed()
    {
        Assert.False(LlmJsonParser.TryParseMetricResponse("""{"score":20} then {"score":90}""", out _, out var failure));
        Assert.Equal(JudgeReplyFailure.Ambiguous, failure);
    }

    [Fact]
    public void TryParseMetricResponse_TheSameVerdictTwice_IsNotAmbiguous()
    {
        // A fenced object is found twice, as a fence body and as an object in the text.
        Assert.True(LlmJsonParser.TryParseMetricResponse("```json\n{\"score\":75}\n```", out var parsed, out _));
        Assert.Equal(75, parsed!.Score);
    }

    [Theory]
    [InlineData("   ", "empty")]
    [InlineData("This is not JSON", "no_json")]
    [InlineData("} x {", "no_json")]
    [InlineData("{\"score\": 80,,}", "invalid_json")]
    [InlineData("{\"reasoning\":\"x\"}", "no_score")]
    [InlineData("{\"score\":\"high\"}", "score_not_a_number")]
    public void TryParseMetricResponse_AnUnreadableReply_SaysWhy(string reply, string expected)
    {
        Assert.False(LlmJsonParser.TryParseMetricResponse(reply, out _, out var failure));
        Assert.Equal(expected, JudgeReplyEvidence.Code(failure));
    }

    [Theory]
    [InlineData("es-ES")]
    [InlineData("de-DE")]
    public void TryParseMetricResponse_AQuotedScore_IsReadInTheInvariantCulture(string culture)
    {
        // In a culture whose decimal separator is a comma, "85.5" parsed in the current culture is 855 (then clamped
        // to 100: a Pass for a judge that said 85.5).
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            Assert.True(LlmJsonParser.TryParseMetricResponse("""{"score":"85.5"}""", out var parsed, out _));
            Assert.Equal(85.5, parsed!.Score);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void TryParseMetricResponse_AHugeUnbalancedReply_Completes()
    {
        // Guard: one pass over the reply, no regex. A scan that restarted at every '{' would be quadratic here.
        var reply = new string('{', 200_000);

        Assert.False(LlmJsonParser.TryParseMetricResponse(reply, out _, out _));
    }

    [Fact]
    public void JsonCandidates_ReadTheFencesFirst_ThenTheObjects_AfterTheReasoning()
    {
        var candidates = LlmJsonParser.JsonCandidates(
            "<think>{\"score\":1}</think>```json\n{\"a\":1}\n```\ntext {\"b\":2} more {\"c\":{\"d\":3}}").ToList();

        Assert.Equal(new[] { "{\"a\":1}", "{\"a\":1}", "{\"b\":2}", "{\"c\":{\"d\":3}}" }, candidates);
    }
}
