// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Core;
using AgentEval.Models;
using Xunit;

namespace AgentEval.Tests.Models;

/// <summary>
/// Tests for <see cref="DatasetTestCaseExtensions"/>.
/// </summary>
public class DatasetTestCaseExtensionsTests
{
    #region ToTestCase Tests

    [Fact]
    public void ToTestCase_BasicMapping_MapsAllFields()
    {
        var dc = new DatasetTestCase
        {
            Id = "test-1",
            Input = "What is 2+2?",
            ExpectedOutput = "4",
            EvaluationCriteria = new[] { "Must be correct" },
            ExpectedTools = new[] { "calculator" },
            Tags = new[] { "math", "basic" },
            PassingScore = 80,
        };

        var tc = dc.ToTestCase();

        Assert.Equal("test-1", tc.Name);
        Assert.Equal("What is 2+2?", tc.Input);
        Assert.Equal("4", tc.ExpectedOutputContains);
        Assert.Equal(new[] { "Must be correct" }, tc.EvaluationCriteria);
        Assert.Equal(new[] { "calculator" }, tc.ExpectedTools);
        Assert.Equal(new[] { "math", "basic" }, tc.Tags);
        Assert.Equal(80, tc.PassingScore);
    }

    [Fact]
    public void ToTestCase_EmptyId_UsesInputTruncated()
    {
        var dc = new DatasetTestCase
        {
            Id = "",
            Input = "This is a very long input that should be truncated to 50 characters maximum",
        };

        var tc = dc.ToTestCase();

        Assert.Equal(50, tc.Name.Length);
        Assert.Equal("This is a very long input that should be truncated", tc.Name);
    }

    [Fact]
    public void ToTestCase_ShortInputEmptyId_UsesFullInput()
    {
        var dc = new DatasetTestCase
        {
            Id = "",
            Input = "Short",
        };

        var tc = dc.ToTestCase();

        Assert.Equal("Short", tc.Name);
    }

    [Fact]
    public void ToTestCase_NullPassingScore_DefaultsTo70()
    {
        var dc = new DatasetTestCase
        {
            Id = "test",
            Input = "question",
            PassingScore = null,
        };

        var tc = dc.ToTestCase();

        Assert.Equal(EvaluationDefaults.DefaultPassingScore, tc.PassingScore);
    }

    [Fact]
    public void ToTestCase_GroundTruth_DefaultSerializesToJson()
    {
        var dc = new DatasetTestCase
        {
            Id = "gt-test",
            Input = "Book a flight",
            GroundTruth = new GroundTruthToolCall
            {
                Name = "book_flight",
                Arguments = new Dictionary<string, object?> { ["city"] = "Paris" }
            }
        };

        var tc = dc.ToTestCase();

        Assert.NotNull(tc.GroundTruth);
        Assert.Contains("book_flight", tc.GroundTruth);
        Assert.Contains("Paris", tc.GroundTruth);
        // Verify it's valid JSON
        var doc = JsonDocument.Parse(tc.GroundTruth);
        Assert.NotNull(doc);
    }

    [Fact]
    public void ToTestCase_GroundTruthNull_MapsToNull()
    {
        var dc = new DatasetTestCase
        {
            Id = "no-gt",
            Input = "Hello",
        };

        var tc = dc.ToTestCase();

        Assert.Null(tc.GroundTruth);
    }

    [Fact]
    public void ToTestCase_CustomGroundTruthProjection_UsesProjection()
    {
        var dc = new DatasetTestCase
        {
            Id = "custom-gt",
            Input = "Query",
            GroundTruth = new GroundTruthToolCall
            {
                Name = "search",
                Arguments = new Dictionary<string, object?> { ["q"] = "test" }
            }
        };

        var tc = dc.ToTestCase(gt => gt?.Name);

        Assert.Equal("search", tc.GroundTruth);
    }

    [Fact]
    public void ToTestCase_Metadata_FiltersNullValues()
    {
        var dc = new DatasetTestCase
        {
            Id = "meta-test",
            Input = "Test",
            Metadata = new Dictionary<string, object?>
            {
                ["key1"] = "value1",
                ["key2"] = null,
                ["key3"] = 42,
            }
        };

        var tc = dc.ToTestCase();

        Assert.NotNull(tc.Metadata);
        Assert.Equal(2, tc.Metadata!.Count);
        Assert.Equal("value1", tc.Metadata["key1"]);
        Assert.Equal(42, tc.Metadata["key3"]);
        Assert.False(tc.Metadata.ContainsKey("key2"));
    }

    [Fact]
    public void ToTestCase_EmptyMetadata_MapsToNull()
    {
        var dc = new DatasetTestCase
        {
            Id = "no-meta",
            Input = "Test",
        };

        var tc = dc.ToTestCase();

        Assert.Null(tc.Metadata);
    }

    [Fact]
    public void ToTestCase_NullFields_HandledGracefully()
    {
        var dc = new DatasetTestCase
        {
            Id = "null-test",
            Input = "Question",
            ExpectedOutput = null,
            EvaluationCriteria = null,
            ExpectedTools = null,
            Tags = null,
            PassingScore = null,
        };

        var tc = dc.ToTestCase();

        Assert.Equal("null-test", tc.Name);
        Assert.Null(tc.ExpectedOutputContains);
        Assert.Null(tc.EvaluationCriteria);
        Assert.Null(tc.ExpectedTools);
        Assert.Null(tc.Tags);
        Assert.Equal(EvaluationDefaults.DefaultPassingScore, tc.PassingScore);
    }

    #endregion

    #region ToEvaluationContext Tests

    [Fact]
    public void ToEvaluationContext_BasicMapping_MapsCorrectly()
    {
        var dc = new DatasetTestCase
        {
            Id = "eval-test",
            Input = "What color is the sky?",
            ExpectedOutput = "Blue",
            Context = new[] { "The sky appears blue due to Rayleigh scattering." },
        };

        var ctx = dc.ToEvaluationContext("The sky is blue.");

        Assert.Equal("What color is the sky?", ctx.Input);
        Assert.Equal("The sky is blue.", ctx.Output);
        Assert.Equal("The sky appears blue due to Rayleigh scattering.", ctx.Context);
        Assert.Equal("Blue", ctx.GroundTruth);
    }

    [Fact]
    public void ToEvaluationContext_MultipleContextDocs_JoinsWithSeparator()
    {
        var dc = new DatasetTestCase
        {
            Id = "multi-ctx",
            Input = "Question",
            Context = new[] { "Doc1", "Doc2", "Doc3" },
        };

        var ctx = dc.ToEvaluationContext("Answer");

        Assert.Equal("Doc1\nDoc2\nDoc3", ctx.Context);
    }

    [Fact]
    public void ToEvaluationContext_CustomSeparator_UsesIt()
    {
        var dc = new DatasetTestCase
        {
            Id = "sep-test",
            Input = "Question",
            Context = new[] { "A", "B" },
        };

        var ctx = dc.ToEvaluationContext("Answer", contextSeparator: " | ");

        Assert.Equal("A | B", ctx.Context);
    }

    [Fact]
    public void ToEvaluationContext_NullContext_MapsToNull()
    {
        var dc = new DatasetTestCase
        {
            Id = "no-ctx",
            Input = "Question",
        };

        var ctx = dc.ToEvaluationContext("Answer");

        Assert.Null(ctx.Context);
    }

    [Fact]
    public void ToEvaluationContext_NullActualOutput_MapsToEmptyString()
    {
        var dc = new DatasetTestCase
        {
            Id = "null-output",
            Input = "Question",
        };

        var ctx = dc.ToEvaluationContext(null);

        Assert.Equal("", ctx.Output);
    }

    [Fact]
    public void ToEvaluationContext_GroundTruthUsesExpectedOutput_NotToolCall()
    {
        var dc = new DatasetTestCase
        {
            Id = "gt-semantics",
            Input = "Book flight",
            ExpectedOutput = "Flight booked successfully",
            GroundTruth = new GroundTruthToolCall
            {
                Name = "book_flight",
                Arguments = new Dictionary<string, object?> { ["city"] = "Paris" }
            }
        };

        var ctx = dc.ToEvaluationContext("Done");

        // GroundTruth in EvaluationContext comes from ExpectedOutput (text), not GroundTruthToolCall
        Assert.Equal("Flight booked successfully", ctx.GroundTruth);
    }

    [Fact]
    public void ToEvaluationContext_OnlyToolCallNoExpectedOutput_GroundTruthIsNull()
    {
        var dc = new DatasetTestCase
        {
            Id = "tool-only",
            Input = "Search for pizza",
            GroundTruth = new GroundTruthToolCall { Name = "search" }
        };

        var ctx = dc.ToEvaluationContext("Results found");

        // By design: no ExpectedOutput → no GroundTruth in evaluation context
        Assert.Null(ctx.GroundTruth);
    }

    #endregion
}
