// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Assertions;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Assertions;

/// <summary>
/// Regression coverage for BUG-21: WithArgument equality previously stringified a JsonElement via
/// <c>GetRawText().Trim('"')</c> then ordinal-compared, which (a) mangled string values that began
/// or ended with a quote and (b) diverged from CLR ToString() for non-string kinds — JSON
/// <c>true</c> vs CLR <c>True</c>, JSON <c>1.0</c> vs CLR <c>1</c> — so correct boolean/numeric
/// arguments compared unequal. <see cref="ToolArgumentComparison"/> now compares by type.
/// </summary>
public class ToolArgumentComparisonTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    [Theory]
    [InlineData("1.0", 1)]      // JSON 1.0 vs CLR int 1
    [InlineData("1", 1.0)]      // JSON 1 vs CLR double 1.0
    [InlineData("2.5", 2.5)]    // JSON 2.5 vs CLR double 2.5
    [InlineData("42", 42L)]     // JSON 42 vs CLR long 42
    public void Matches_Numbers_CompareNumerically(string actualJson, object expected)
    {
        Assert.True(ToolArgumentComparison.Matches(Json(actualJson), expected));
    }

    [Fact]
    public void Matches_Numbers_DifferentValues_DoNotMatch()
    {
        Assert.False(ToolArgumentComparison.Matches(Json("1.0"), 2));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Matches_Booleans_CompareByValue(string actualJson, bool expected)
    {
        Assert.True(ToolArgumentComparison.Matches(Json(actualJson), expected));
    }

    [Fact]
    public void Matches_Booleans_DifferentValues_DoNotMatch()
    {
        Assert.False(ToolArgumentComparison.Matches(Json("true"), false));
    }

    [Fact]
    public void Matches_StringValueWithSurroundingQuotes_NotMangled()
    {
        // JSON string whose content is literally "hi" (leading + trailing quote).
        // The old Trim('"') stripped those inner quotes, mangling the value.
        var actual = Json("\"\\\"hi\\\"\"");
        Assert.Equal("\"hi\"", actual.GetString()); // sanity: the value really is quoted
        Assert.True(ToolArgumentComparison.Matches(actual, "\"hi\""));
        Assert.False(ToolArgumentComparison.Matches(actual, "hi"));
    }

    [Fact]
    public void Matches_PlainStrings_CompareOrdinally()
    {
        Assert.True(ToolArgumentComparison.Matches(Json("\"Paris\""), "Paris"));
        Assert.False(ToolArgumentComparison.Matches(Json("\"Paris\""), "paris"));
    }

    [Fact]
    public void Matches_NonJsonClrValues_StillCompare()
    {
        Assert.True(ToolArgumentComparison.Matches("value", "value"));
        Assert.True(ToolArgumentComparison.Matches(5, 5.0));
        Assert.True(ToolArgumentComparison.Matches(true, true));
    }

    // ── End-to-end through the public assertion API ──────────────────────────────

    [Fact]
    public void WithArgument_JsonBoolean_MatchesClrBool()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord
        {
            Name = "TestTool",
            CallId = "call-1",
            Order = 1,
            Arguments = new Dictionary<string, object?> { ["enabled"] = Json("true") }
        });

        var exception = Record.Exception(() =>
            report.Should().HaveCalledTool("TestTool").WithArgument("enabled", true));

        Assert.Null(exception); // before BUG-21 fix this threw: "true" != "True"
    }

    [Fact]
    public void WithArgument_JsonNumber_MatchesClrNumberRegardlessOfFormat()
    {
        var report = new ToolUsageReport();
        report.AddCall(new ToolCallRecord
        {
            Name = "TestTool",
            CallId = "call-1",
            Order = 1,
            Arguments = new Dictionary<string, object?> { ["count"] = Json("1.0") }
        });

        var exception = Record.Exception(() =>
            report.Should().HaveCalledTool("TestTool").WithArgument("count", 1));

        Assert.Null(exception); // before BUG-21 fix this threw: "1.0" != "1"
    }
}
