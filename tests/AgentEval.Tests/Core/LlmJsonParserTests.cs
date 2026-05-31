// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

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
}
