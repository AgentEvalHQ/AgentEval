// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using System.Text;
using AgentEval.Decisions;
using Xunit;

namespace AgentEval.Tests.Decisions;

/// <summary>
/// Three tightenings from the PR review, each pinned so it cannot regress silently: a fractional
/// token count is a protocol violation, not something to truncate; a probability for an option
/// that was never asked is a violation, not extra information; and a malformed 2xx body that echoes
/// the bearer must not put it into the exception message.
/// </summary>
public class SystemOneStrictnessTests
{
    private static readonly IReadOnlyDictionary<string, DecisionQuestion> OneNoul =
        new Dictionary<string, DecisionQuestion> { ["q"] = new BinaryQuestion("?") };

    [Theory]
    [InlineData("""{ "model": "m", "answers": { "q": { "type": "noul", "noul": 0.5 } }, "usage": { "input_tokens": 1.9, "output_tokens": 0 } }""")]
    [InlineData("""{ "model": "m", "answers": { "q": { "type": "noul", "noul": 0.5 } }, "usage": { "input_tokens": 10, "output_tokens": 2.5 } }""")]
    [InlineData("""{ "model": "m", "answers": { "q": { "type": "noul", "noul": 0.5 } }, "usage": { "input_tokens": 1e30, "output_tokens": 0 } }""")]
    public void FractionalOrOutOfRangeTokenCount_IsInvalidResponse_NotTruncated(string body)
    {
        var ex = Assert.Throws<DecisionClientException>(() => SystemOneProtocol.ParseResponse(body, OneNoul, "h"));

        Assert.Equal(DecisionFailureKind.InvalidResponse, ex.Kind);
        Assert.Contains("not an integer token count", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ChoiceAnswer_WithAProbabilityForAnUnrequestedOption_IsInvalidResponse()
    {
        var asked = new Dictionary<string, DecisionQuestion>
        {
            ["risk"] = new ChoiceQuestion("?", new Dictionary<string, string> { ["low"] = "a", ["high"] = "b" }),
        };
        const string body = """
            { "model": "m", "answers": { "risk": { "type": "choice", "choice": "high",
              "probabilities": { "low": 0.1, "high": 0.8, "medium": 0.1 }, "confidence": 0.8 } } }
            """;

        var ex = Assert.Throws<DecisionClientException>(() => SystemOneProtocol.ParseResponse(body, asked, "h"));

        Assert.Equal(DecisionFailureKind.InvalidResponse, ex.Kind);
        Assert.Contains("'medium'", ex.Message, StringComparison.Ordinal);
    }

    private sealed class FixedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/plain") });
    }

    [Fact]
    public async Task Malformed2xxBody_ThatEchoesTheKey_IsRedactedInTheException()
    {
        const string key = "sk-live-DO-NOT-LEAK-4c1e9";
        var client = new SystemOneDecisionClient(
            SystemOneClientOptions.ForTypeSafe(key),
            new HttpClient(new FixedHandler(HttpStatusCode.OK, $"<html>proxy error: Authorization: Bearer {key}</html>")));

        var ex = await Assert.ThrowsAsync<DecisionClientException>(() =>
            client.DecideAsync(new DecisionRequest("state", OneNoul)));

        Assert.Equal(DecisionFailureKind.InvalidResponse, ex.Kind);
        Assert.DoesNotContain(key, ex.Message, StringComparison.Ordinal);
        Assert.Contains("[redacted]", ex.Message, StringComparison.Ordinal);
    }
}
