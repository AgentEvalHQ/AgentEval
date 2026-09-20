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
                ["refund"] = new BinaryQuestion("Is the customer asking for money back?"),
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
                ["grounded"] = new BinaryQuestion("Is it grounded?", TrueCriteria: "Every claim is in the context.", FalseCriteria: "A claim is not in the context."),
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
            Questions: new Dictionary<string, DecisionQuestion> { ["a"] = new BinaryQuestion("?") });

        using var doc = JsonDocument.Parse(SystemOneProtocol.SerializeRequest(request, "m"));
        var state = doc.RootElement.GetProperty("state");
        Assert.Equal("q", state.GetProperty("query").GetString());
        Assert.Equal("r", state.GetProperty("response").GetString());
        Assert.False(state.TryGetProperty("context", out _), "null state members must not be sent");
    }

    [Fact]
    public void SerializeRequest_RequestModel_OverridesDefault()
    {
        var request = new DecisionRequest("x", new Dictionary<string, DecisionQuestion> { ["a"] = new BinaryQuestion("?") }, Model: "typesafe/jev-1.13");
        using var doc = JsonDocument.Parse(SystemOneProtocol.SerializeRequest(request, "~typesafe/jev-latest"));
        Assert.Equal("typesafe/jev-1.13", doc.RootElement.GetProperty("model").GetString());
    }

    // ── Response parsing: the documented shapes ──────────────────────────────

    private static readonly IReadOnlyDictionary<string, DecisionQuestion> OneNoul =
        new Dictionary<string, DecisionQuestion> { ["refund"] = new BinaryQuestion("Is the customer asking for money back?") };

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
        var answer = Assert.IsType<BinaryAnswer>(response.Answers["refund"]);
        Assert.Equal(0.98, answer.TrueProbability, precision: 6);
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
        Assert.Equal(0.12, Assert.IsType<BinaryAnswer>(response.Answers["refund"]).TrueProbability, precision: 6);
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
                "quality": { "type": "score",  "score": 0.7, "legend": { "0": "poor", "1": "good" }, "probabilities": { "0": 0.3, "1": 0.7 }, "confidence": 0.7 }
              },
              "usage": { "input_tokens": 1, "output_tokens": 1 } }
            """;

        var response = SystemOneProtocol.ParseResponse(body, asked, "h");

        var risk = Assert.IsType<ChoiceAnswer>(response.Answers["risk"]);
        Assert.Equal("high", risk.Choice);
        Assert.Equal(0.8, risk.Probabilities["high"], precision: 6);
        Assert.Equal(0.8, risk.Confidence, precision: 6);

        var quality = Assert.IsType<ScoreAnswer>(response.Answers["quality"]);
        Assert.Equal(0.7, quality.Score, precision: 6);
        Assert.Equal("good", quality.Legend!["1"]);
        Assert.Equal(0.7, quality.Probabilities["1"], precision: 6);
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

    // ── Strict pairing of answers to the options and levels that were asked ──

    private static IReadOnlyDictionary<string, DecisionQuestion> ChoiceAsk() =>
        new Dictionary<string, DecisionQuestion> { ["dept"] = new ChoiceQuestion("Which?", new Dictionary<string, string> { ["billing"] = "Payments", ["tech"] = "Bugs" }) };

    private static IReadOnlyDictionary<string, DecisionQuestion> ScoreAsk() =>
        new Dictionary<string, DecisionQuestion> { ["mood"] = new ScoreQuestion("How upset?", new[] { "calm", "angry" }) };

    [Fact]
    public void ParseResponse_ChoiceSelectingUnknownOption_ThrowsInvalidResponse()
    {
        const string body = """{ "model": "m", "answers": { "dept": { "type": "choice", "choice": "legal", "probabilities": { "billing": 0.5, "tech": 0.5 }, "confidence": 0.5 } } }""";

        var ex = Assert.Throws<DecisionClientException>(() => SystemOneProtocol.ParseResponse(body, ChoiceAsk(), "h"));

        Assert.Equal(DecisionFailureKind.InvalidResponse, ex.Kind);
        Assert.Contains("not one of the requested options", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseResponse_ChoiceMissingAnOptionProbability_ThrowsInvalidResponse()
    {
        const string body = """{ "model": "m", "answers": { "dept": { "type": "choice", "choice": "billing", "probabilities": { "billing": 1.0 }, "confidence": 1.0 } } }""";

        var ex = Assert.Throws<DecisionClientException>(() => SystemOneProtocol.ParseResponse(body, ChoiceAsk(), "h"));

        Assert.Contains("no probability for option 'tech'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseResponse_ScoreWithUnknownLevel_ThrowsInvalidResponse()
    {
        const string body = """{ "model": "m", "answers": { "mood": { "type": "score", "score": 0.5, "probabilities": { "0": 0.5, "1": 0.5, "7": 0.0 }, "confidence": 0.5 } } }""";

        var ex = Assert.Throws<DecisionClientException>(() => SystemOneProtocol.ParseResponse(body, ScoreAsk(), "h"));

        Assert.Contains("unknown level '7'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseResponse_ScoreMissingALevel_ThrowsInvalidResponse()
    {
        const string body = """{ "model": "m", "answers": { "mood": { "type": "score", "score": 0.0, "probabilities": { "0": 1.0 }, "confidence": 1.0 } } }""";

        var ex = Assert.Throws<DecisionClientException>(() => SystemOneProtocol.ParseResponse(body, ScoreAsk(), "h"));

        Assert.Contains("no probability for level 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseResponse_WellFormedChoiceAndScore_StillParse()
    {
        const string body = """{ "model": "m", "answers": { "dept": { "type": "choice", "choice": "tech", "probabilities": { "billing": 0.2, "tech": 0.8 }, "confidence": 0.8 }, "mood": { "type": "score", "score": 0.9, "probabilities": { "0": 0.1, "1": 0.9 }, "confidence": 0.9 } } }""";
        var ask = new Dictionary<string, DecisionQuestion>(ChoiceAsk());
        foreach (var (k, v) in ScoreAsk()) ask[k] = v;

        var response = SystemOneProtocol.ParseResponse(body, ask, "h");

        Assert.Equal("tech", Assert.IsType<ChoiceAnswer>(response.Answers["dept"]).Choice);
        Assert.Equal(0.9, Assert.IsType<ScoreAnswer>(response.Answers["mood"]).Score, precision: 6);
        Assert.Null(response.ResponseId);
    }

    [Fact]
    public void ParseResponse_ProviderId_IsCapturedAsResponseId()
    {
        const string body = """{ "id": "gen-abc123", "model": "typesafe/jev-1.13", "answers": { "refund": { "type": "noul", "noul": 0.5 } }, "usage": { "input_tokens": 3, "output_tokens": 0 } }""";
        var ask = new Dictionary<string, DecisionQuestion> { ["refund"] = new BinaryQuestion("?") };

        var response = SystemOneProtocol.ParseResponse(body, ask, "h");

        Assert.Equal("gen-abc123", response.ResponseId);
    }

    // ── Provider limits live in the transport, not the contract ──────────────

    [Fact]
    public void SerializeRequest_ChoiceAboveProviderMaximum_ThrowsInvalidRequestBeforeSending()
    {
        var options = Enumerable.Range(0, SystemOneProtocol.MaxChoiceOptions + 1).ToDictionary(i => $"o{i}", i => $"option {i}");
        var request = new DecisionRequest("x", new Dictionary<string, DecisionQuestion> { ["c"] = new ChoiceQuestion("Which?", options) });

        var ex = Assert.Throws<DecisionClientException>(() => SystemOneProtocol.SerializeRequest(request, "m"));

        Assert.Equal(DecisionFailureKind.InvalidRequest, ex.Kind);
        Assert.False(ex.IsTransient);
        Assert.Contains(SystemOneProtocol.MaxChoiceOptions.ToString(), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeRequest_ScoreAboveProviderMaximum_ThrowsInvalidRequestBeforeSending()
    {
        var levels = Enumerable.Range(0, SystemOneProtocol.MaxScoreLevels + 1).Select(i => $"level {i}").ToList();
        var request = new DecisionRequest("x", new Dictionary<string, DecisionQuestion> { ["s"] = new ScoreQuestion("How much?", levels) });

        var ex = Assert.Throws<DecisionClientException>(() => SystemOneProtocol.SerializeRequest(request, "m"));

        Assert.Equal(DecisionFailureKind.InvalidRequest, ex.Kind);
        Assert.Contains(SystemOneProtocol.MaxScoreLevels.ToString(), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Contract_AcceptsCountsAboveJevLimits_BecauseTheyAreProviderLimits()
    {
        var options = Enumerable.Range(0, 300).ToDictionary(i => $"o{i}", i => $"option {i}");
        var levels = Enumerable.Range(0, 12).Select(i => $"level {i}").ToList();

        _ = new ChoiceQuestion("Which?", options);
        _ = new ScoreQuestion("How much?", levels);
    }

    [Fact]
    public void Contract_StillRejectsFewerThanTwoOptionsOrLevels()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChoiceQuestion("Which?", new Dictionary<string, string> { ["only"] = "one" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ScoreQuestion("How much?", new[] { "one" }));
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
