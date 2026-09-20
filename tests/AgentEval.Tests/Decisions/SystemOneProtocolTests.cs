// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using AgentEval.Decisions;
using Xunit;

namespace AgentEval.Tests.Decisions;

/// <summary>
/// Pins the System One wire shape (ADR-033) without a network: what a request serialises to, and
/// what the parser accepts and refuses. Every JSON fixture below is the shape TypeSafe's API
/// reference and OpenRouter's SDK guide document, captured 2026-09-20.
/// </summary>
public class SystemOneProtocolTests
{
    // ── Request serialisation ────────────────────────────────────────────────

    [Fact]
    public void SerializeRequest_NoulWithoutCriteria_OmitsCriteria()
    {
        var request = new DecisionRequest(
            State: "I was charged twice for my subscription.",
            Questions: new Dictionary<string, DecisionQuestion>
            {
                ["refund"] = new NoulQuestion("Is the customer asking for money back?"),
            });

        var json = SystemOneProtocol.SerializeRequest(request, "typesafe/jev-1.13");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("typesafe/jev-1.13", root.GetProperty("model").GetString());
        Assert.Equal("I was charged twice for my subscription.", root.GetProperty("state").GetString());
        var q = root.GetProperty("questions").GetProperty("refund");
        Assert.Equal("noul", q.GetProperty("type").GetString());
        Assert.Equal("Is the customer asking for money back?", q.GetProperty("instructions").GetString());
        Assert.False(q.TryGetProperty("criteria", out _), "criteria must be omitted when neither side is given");
    }

    [Fact]
    public void SerializeRequest_NoulWithCriteria_WritesTrueAndFalse()
    {
        var request = new DecisionRequest(
            State: "x",
            Questions: new Dictionary<string, DecisionQuestion>
            {
                ["grounded"] = new NoulQuestion("Is it grounded?", TrueCriteria: "Every claim is in the context.", FalseCriteria: "A claim is not in the context."),
            });

        using var doc = JsonDocument.Parse(SystemOneProtocol.SerializeRequest(request, "m"));
        var criteria = doc.RootElement.GetProperty("questions").GetProperty("grounded").GetProperty("criteria");
        Assert.Equal("Every claim is in the context.", criteria.GetProperty("true").GetString());
        Assert.Equal("A claim is not in the context.", criteria.GetProperty("false").GetString());
    }

    [Fact]
    public void SerializeRequest_ChoiceAndScore_WriteMapAndArray()
    {
        var request = new DecisionRequest(
            State: "x",
            Questions: new Dictionary<string, DecisionQuestion>
            {
                ["risk"] = new ChoiceQuestion("Classify the risk.", new Dictionary<string, string>
                {
                    ["low"] = "No concern.",
                    ["high"] = "Block it.",
                }),
                ["quality"] = new ScoreQuestion("Rate the quality.", ["poor", "fair", "good"]),
            });

        using var doc = JsonDocument.Parse(SystemOneProtocol.SerializeRequest(request, "m"));
        var questions = doc.RootElement.GetProperty("questions");

        var risk = questions.GetProperty("risk");
        Assert.Equal("choice", risk.GetProperty("type").GetString());
        Assert.Equal("Block it.", risk.GetProperty("criteria").GetProperty("high").GetString());

        var quality = questions.GetProperty("quality");
        Assert.Equal("score", quality.GetProperty("type").GetString());
        var levels = quality.GetProperty("criteria").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "poor", "fair", "good" }, levels);
    }

    [Fact]
    public void SerializeRequest_ObjectState_IsCamelCasedWithNullsOmitted()
    {
        var request = new DecisionRequest(
            State: new { Query = "q", Response = "r", Context = (string?)null },
            Questions: new Dictionary<string, DecisionQuestion> { ["a"] = new NoulQuestion("?") });

        using var doc = JsonDocument.Parse(SystemOneProtocol.SerializeRequest(request, "m"));
        var state = doc.RootElement.GetProperty("state");
        Assert.Equal("q", state.GetProperty("query").GetString());
        Assert.Equal("r", state.GetProperty("response").GetString());
        Assert.False(state.TryGetProperty("context", out _), "null state members must not be sent");
    }

    [Fact]
    public void SerializeRequest_RequestModel_OverridesDefault()
    {
        var request = new DecisionRequest("x", new Dictionary<string, DecisionQuestion> { ["a"] = new NoulQuestion("?") }, Model: "typesafe/jev-1.13");
        using var doc = JsonDocument.Parse(SystemOneProtocol.SerializeRequest(request, "~typesafe/jev-latest"));
        Assert.Equal("typesafe/jev-1.13", doc.RootElement.GetProperty("model").GetString());
    }

    // ── Response parsing: the documented shapes ──────────────────────────────

    private static readonly IReadOnlyDictionary<string, DecisionQuestion> OneNoul =
        new Dictionary<string, DecisionQuestion> { ["refund"] = new NoulQuestion("Is the customer asking for money back?") };

    [Fact]
    public void ParseResponse_OpenRouterNoulExample_RoundTrips()
    {
        // Verbatim from OpenRouter's TypeSafe SDK guide (2026-09-20), including the relay-only
        // fields (id, provider, usage.cost) the parser must tolerate.
        const string body = """
            {
              "id": "gen-dec-1789738314-X5e5eKGQdvR9rblyX250",
              "model": "typesafe/jev-1.13-20260917",
              "provider": "TypeSafe",
              "answers": { "refund": { "type": "noul", "noul": 0.98 } },
              "usage": { "input_tokens": 275, "output_tokens": 20, "cost": 0.00003 }
            }
            """;

        var response = SystemOneProtocol.ParseResponse(body, OneNoul, "openrouter.ai");

        Assert.Equal("typesafe/jev-1.13-20260917", response.Model);
        var answer = Assert.IsType<NoulAnswer>(response.Answers["refund"]);
        Assert.Equal(0.98, answer.ProbabilityYes, precision: 6);
        Assert.NotNull(response.Usage);
        Assert.Equal(275, response.Usage!.InputTokens);
        Assert.Equal(20, response.Usage.OutputTokens);
        Assert.Equal(0.00003, response.Usage.Cost!.Value, precision: 9);
    }

    [Fact]
    public void ParseResponse_TypeSafeShape_NoCostField_UsageCostIsNull()
    {
        const string body = """
            { "model": "jev-1.13.0", "answers": { "refund": { "type": "noul", "noul": 0.12 } },
              "usage": { "input_tokens": 100, "output_tokens": 5 } }
            """;

        var response = SystemOneProtocol.ParseResponse(body, OneNoul, "api.typesafe.ai");

        Assert.Null(response.Usage!.Cost);
        Assert.Equal(0.12, Assert.IsType<NoulAnswer>(response.Answers["refund"]).ProbabilityYes, precision: 6);
    }

    [Fact]
    public void ParseResponse_ChoiceAndScore_CarryDistributionsAndConfidence()
    {
        var asked = new Dictionary<string, DecisionQuestion>
        {
            ["risk"] = new ChoiceQuestion("?", new Dictionary<string, string> { ["low"] = "a", ["high"] = "b" }),
            ["quality"] = new ScoreQuestion("?", ["poor", "good"]),
        };
        const string body = """
            { "model": "jev-1.13.0",
              "answers": {
                "risk":    { "type": "choice", "choice": "high", "probabilities": { "low": 0.2, "high": 0.8 }, "confidence": 0.8 },
                "quality": { "type": "score",  "score": 1.7, "legend": { "1": "poor", "2": "good" }, "probabilities": { "1": 0.3, "2": 0.7 }, "confidence": 0.7 }
              },
              "usage": { "input_tokens": 1, "output_tokens": 1 } }
            """;

        var response = SystemOneProtocol.ParseResponse(body, asked, "h");

        var risk = Assert.IsType<ChoiceAnswer>(response.Answers["risk"]);
        Assert.Equal("high", risk.Choice);
        Assert.Equal(0.8, risk.Probabilities["high"], precision: 6);
        Assert.Equal(0.8, risk.Confidence, precision: 6);

        var quality = Assert.IsType<ScoreAnswer>(response.Answers["quality"]);
        Assert.Equal(1.7, quality.Score, precision: 6);
        Assert.Equal("good", quality.Legend!["2"]);
        Assert.Equal(0.7, quality.Probabilities["2"], precision: 6);
        Assert.Equal(0.7, quality.Confidence, precision: 6);
    }

    [Fact]
    public void ParseResponse_NoUsageObject_UsageIsNull()
    {
        const string body = """{ "model": "m", "answers": { "refund": { "type": "noul", "noul": 0.5 } } }""";
        var response = SystemOneProtocol.ParseResponse(body, OneNoul, "h");
        Assert.Null(response.Usage);
    }

    // ── Response parsing: fail closed ────────────────────────────────────────

    [Theory]
    [InlineData("""{ "model": "m", "answers": { } }""", "no answer for question 'refund'")]
    [InlineData("""{ "model": "m", "answers": { "refund": { "type": "choice", "choice": "x", "probabilities": {}, "confidence": 1 } } }""", "'choice' answer to a 'noul' question")]
    [InlineData("""{ "model": "m", "answers": { "refund": { "noul": 0.5 } } }""", "has no 'type'")]
    [InlineData("""{ "model": "m", "answers": { "refund": { "type": "noul" } } }""", "no numeric 'noul'")]
    [InlineData("""{ "model": "m", "answers": { "refund": { "type": "noul", "noul": 1.5 } } }""", "out of range")]
    [InlineData("""{ "model": "m", "answers": { "refund": { "type": "noul", "noul": -0.1 } } }""", "out of range")]
    [InlineData("""{ "answers": { "refund": { "type": "noul", "noul": 0.5 } } }""", "without a 'model'")]
    [InlineData("""{ "model": "m" }""", "without an 'answers'")]
    [InlineData("""[1, 2, 3]""", "not an object")]
    [InlineData("""not json at all""", "not JSON")]
    public void ParseResponse_UnusableBody_ThrowsInvalidResponse_NeverAScore(string body, string expectedFragment)
    {
        var ex = Assert.Throws<DecisionClientException>(() => SystemOneProtocol.ParseResponse(body, OneNoul, "h"));

        Assert.Equal(DecisionFailureKind.InvalidResponse, ex.Kind);
        Assert.False(ex.IsTransient);
        Assert.Contains(expectedFragment, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseResponse_NegativeTokens_ThrowsInvalidResponse()
    {
        const string body = """{ "model": "m", "answers": { "refund": { "type": "noul", "noul": 0.5 } }, "usage": { "input_tokens": -1, "output_tokens": 0 } }""";
        var ex = Assert.Throws<DecisionClientException>(() => SystemOneProtocol.ParseResponse(body, OneNoul, "h"));
        Assert.Equal(DecisionFailureKind.InvalidResponse, ex.Kind);
    }

    // ── Failure classification ───────────────────────────────────────────────

    [Theory]
    [InlineData(401, DecisionFailureKind.Authentication, false)]
    [InlineData(403, DecisionFailureKind.Authentication, false)]
    [InlineData(400, DecisionFailureKind.InvalidRequest, false)]
    [InlineData(422, DecisionFailureKind.InvalidRequest, false)]
    [InlineData(429, DecisionFailureKind.RateLimited, true)]
    [InlineData(529, DecisionFailureKind.Overloaded, true)]
    [InlineData(500, DecisionFailureKind.ProviderUnavailable, true)]
    [InlineData(503, DecisionFailureKind.ProviderUnavailable, true)]
    [InlineData(418, DecisionFailureKind.Unknown, false)]
    public void ClassifyFailure_MapsStatusToKindAndTransience(int status, DecisionFailureKind kind, bool transient)
    {
        var ex = SystemOneProtocol.ClassifyFailure(status, "{\"error\":\"x\"}", "h");

        Assert.Equal(kind, ex.Kind);
        Assert.Equal(transient, ex.IsTransient);
        Assert.Equal(status, ex.StatusCode);
        Assert.Contains($"HTTP {status}", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ClassifyFailure_LongBody_IsExcerptedAndFlattened()
    {
        var body = "line1\nline2 " + new string('x', 2000);
        var ex = SystemOneProtocol.ClassifyFailure(500, body, "h");

        Assert.DoesNotContain("\n", ex.Message);
        Assert.True(ex.Message.Length < 700, $"message should be bounded, was {ex.Message.Length}");
        Assert.EndsWith("…", ex.Message);
    }
}
