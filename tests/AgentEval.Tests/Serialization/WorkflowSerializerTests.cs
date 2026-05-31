// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Models.Serialization;
using Xunit;

namespace AgentEval.Tests.Serialization;

/// <summary>
/// Regression coverage for GAP-17: WorkflowSerializer.FromJson now guards its input like the sibling
/// methods (ArgumentException on null/empty/whitespace) instead of emitting a bare framework
/// ArgumentNullException, and documents that malformed JSON surfaces as JsonException.
/// </summary>
public class WorkflowSerializerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FromJson_NullOrWhitespace_ThrowsArgumentException(string? json)
    {
        // null → ArgumentNullException, empty/whitespace → ArgumentException (both ArgumentException-derived).
        Assert.ThrowsAny<ArgumentException>(() => WorkflowSerializer.FromJson(json!));
    }

    [Fact]
    public void FromJson_MalformedJson_ThrowsJsonException()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => WorkflowSerializer.FromJson("{ not valid json"));
    }
}
