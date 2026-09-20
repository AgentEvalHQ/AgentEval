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
    public void AnswerForAQuestionThatWasNotAsked_IsInvalidResponse()
    {
        // A reply carrying an extra id is a reply to some other request; strict pairing cuts both ways.
        const string body = """{ "model": "m", "answers": { "q": { "type": "noul", "noul": 0.5 }, "other": { "type": "noul", "noul": 0.1 } } }""";

        var ex = Assert.Throws<DecisionClientException>(() => SystemOneProtocol.ParseResponse(body, OneNoul, "h"));

        Assert.Equal(DecisionFailureKind.InvalidResponse, ex.Kind);
        Assert.Contains("'other'", ex.Message, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("ftp://127.0.0.1/v1/systemone")]        // loopback, but not http(s)
    [InlineData("http://openrouter.ai/api/v1/systemone")] // http, but not loopback
    [InlineData("file:///C:/systemone")]
    public void Ctor_RejectsEveryEndpointThatIsNotHttpsOrLoopbackHttp(string endpoint)
    {
        var ex = Assert.Throws<ArgumentException>(() => new SystemOneDecisionClient(new SystemOneClientOptions
        {
            Endpoint = new Uri(endpoint),
            ApiKey = "k",
            Model = "m",
        }));
        Assert.Contains(endpoint, ex.Message, StringComparison.Ordinal);
    }

    private sealed class ThrowingHandler(string message) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException(message);
    }

    [Fact]
    public async Task HandlerException_ThatEchoesTheKey_IsScrubbedFromTheClientMessage()
    {
        const string key = "sk-live-DO-NOT-LEAK-91b3";
        var client = new SystemOneDecisionClient(
            SystemOneClientOptions.ForTypeSafe(key),
            new HttpClient(new ThrowingHandler($"proxy rejected Authorization: Bearer {key}")));

        var ex = await Assert.ThrowsAsync<DecisionClientException>(() => client.DecideAsync(new DecisionRequest("state", OneNoul)));

        Assert.Equal(DecisionFailureKind.Unknown, ex.Kind);
        Assert.DoesNotContain(key, ex.Message, StringComparison.Ordinal);
        Assert.Contains("[redacted]", ex.Message, StringComparison.Ordinal);
    }

    private sealed class HugeUsageClient : IDecisionClient
    {
        public Task<DecisionResponse> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DecisionResponse(
                "m",
                request.Questions.Keys.ToDictionary(k => k, _ => (DecisionAnswer)new BinaryAnswer(0.9), StringComparer.Ordinal),
                new DecisionUsage(long.MaxValue - 5, 10, Cost: 0.0)));
    }

    [Fact]
    public async Task DecisionEval_TokenSumThatWouldOverflow_SaturatesAtIntMax_NeverNegative()
    {
        var eval = new AgentEval.Evals.DecisionEval(new HugeUsageClient(), "k", "n", "c", "1.0.0", "?");

        var result = await eval.EvaluateAsync(new AgentEval.Evals.EvalInput("q", "r"));

        Assert.Equal(int.MaxValue, result.Provenance.TokensUsed);
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
