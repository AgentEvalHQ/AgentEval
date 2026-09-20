// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using System.Text;
using System.Text.Json;
using AgentEval.Decisions;
using Xunit;

namespace AgentEval.Tests.Decisions;

/// <summary>
/// The HTTP layer of <see cref="SystemOneDecisionClient"/> against a recording handler: where it
/// posts, what headers it sends, how it turns a status into a typed failure, and that the API key
/// never leaks into an exception. No network.
/// </summary>
public class SystemOneDecisionClientTests
{
    private const string Key = "sk-test-DO-NOT-LEAK-9f3a";

    private const string OkBody = """
        { "model": "typesafe/jev-1.13-20260917",
          "answers": { "q": { "type": "noul", "noul": 0.75 } },
          "usage": { "input_tokens": 10, "output_tokens": 2, "cost": 0.0000004 } }
        """;

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private static DecisionRequest OneQuestion() => new(
        State: new { Query = "q", Response = "r" },
        Questions: new Dictionary<string, DecisionQuestion> { ["q"] = new BinaryQuestion("Is r an answer to q?") });

    private static (SystemOneDecisionClient Client, RecordingHandler Handler) Make(
        HttpStatusCode status = HttpStatusCode.OK, string body = OkBody, SystemOneClientOptions? options = null)
    {
        var handler = new RecordingHandler(status, body);
        var http = new HttpClient(handler);
        var client = new SystemOneDecisionClient(options ?? SystemOneClientOptions.ForOpenRouter(Key), http);
        return (client, handler);
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task DecideAsync_PostsToConfiguredEndpoint_WithBearerAndJson()
    {
        var (client, handler) = Make();

        var response = await client.DecideAsync(OneQuestion());

        var req = handler.LastRequest!;
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal(new Uri(SystemOneClientOptions.OpenRouterEndpoint), req.RequestUri);
        Assert.Equal("Bearer", req.Headers.Authorization!.Scheme);
        Assert.Equal(Key, req.Headers.Authorization.Parameter);
        Assert.Equal("application/json", req.Content!.Headers.ContentType!.MediaType);

        using var sent = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(SystemOneClientOptions.OpenRouterDefaultModel, sent.RootElement.GetProperty("model").GetString());
        Assert.Equal("noul", sent.RootElement.GetProperty("questions").GetProperty("q").GetProperty("type").GetString());

        Assert.Equal("typesafe/jev-1.13-20260917", response.Model);
        Assert.Equal(0.75, Assert.IsType<BinaryAnswer>(response.Answers["q"]).TrueProbability, precision: 6);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DecideAsync_TypeSafeOptions_PostToTypeSafeWithItsDefaultModel()
    {
        var (client, handler) = Make(options: SystemOneClientOptions.ForTypeSafe(Key));

        await client.DecideAsync(OneQuestion());

        Assert.Equal(new Uri(SystemOneClientOptions.TypeSafeEndpoint), handler.LastRequest!.RequestUri);
        using var sent = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(SystemOneClientOptions.TypeSafeDefaultModel, sent.RootElement.GetProperty("model").GetString());
        Assert.Equal("typesafe", client.ProviderName);
    }

    [Fact]
    public async Task RenderRequest_MatchesWhatDecideAsyncSends()
    {
        // The dry-run render and the real call go through the same serializer; a drift between
        // them would make every "I printed what I sent" claim in the samples false.
        var request = OneQuestion();
        var rendered = SystemOneDecisionClient.RenderRequest(request, "typesafe/jev-1.13");

        var (client, handler) = Make();
        await client.DecideAsync(request);

        Assert.Equal(rendered, handler.LastBody);
    }

    // ── Failures ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, DecisionFailureKind.Authentication)]
    [InlineData(HttpStatusCode.Forbidden, DecisionFailureKind.Authentication)]
    [InlineData(HttpStatusCode.UnprocessableEntity, DecisionFailureKind.InvalidRequest)]
    [InlineData(HttpStatusCode.TooManyRequests, DecisionFailureKind.RateLimited)]
    [InlineData((HttpStatusCode)529, DecisionFailureKind.Overloaded)]
    [InlineData(HttpStatusCode.BadGateway, DecisionFailureKind.ProviderUnavailable)]
    public async Task DecideAsync_NonSuccessStatus_ThrowsTypedFailure(HttpStatusCode status, DecisionFailureKind kind)
    {
        var (client, _) = Make(status, """{"error":{"message":"nope"}}""");

        var ex = await Assert.ThrowsAsync<DecisionClientException>(() => client.DecideAsync(OneQuestion()));

        Assert.Equal(kind, ex.Kind);
        Assert.Equal((int)status, ex.StatusCode);
        Assert.Contains("nope", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecideAsync_Failure_NeverPutsTheKeyInTheMessage()
    {
        // A provider that echoes the request back would otherwise leak the bearer through our own exception.
        var (client, _) = Make(HttpStatusCode.BadRequest, $$"""{"echo":"Authorization: Bearer {{Key}}"}""");

        var ex = await Assert.ThrowsAsync<DecisionClientException>(() => client.DecideAsync(OneQuestion()));

        // The body excerpt IS included (it is the provider's diagnostic) — with the key redacted.
        Assert.DoesNotContain(Key, ex.Message, StringComparison.Ordinal);
        Assert.Contains("[redacted]", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, client.ToString() ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(Key, SystemOneClientOptions.ForOpenRouter(Key).ToString() ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecideAsync_SuccessWithUnusableBody_ThrowsInvalidResponse()
    {
        var (client, _) = Make(HttpStatusCode.OK, """{ "model": "m", "answers": {} }""");

        var ex = await Assert.ThrowsAsync<DecisionClientException>(() => client.DecideAsync(OneQuestion()));

        Assert.Equal(DecisionFailureKind.InvalidResponse, ex.Kind);
        Assert.False(ex.IsTransient);
    }

    [Fact]
    public async Task DecideAsync_CallerCancellation_PropagatesAsCancellation_NotAsProviderFailure()
    {
        var (client, _) = Make();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DecideAsync(OneQuestion(), cts.Token));
    }

    // ── Construction guards ──────────────────────────────────────────────────

    [Fact]
    public void Ctor_RejectsBlankKey_HttpEndpoint_BlankModel()
    {
        Assert.Throws<ArgumentException>(() => new SystemOneDecisionClient(SystemOneClientOptions.ForOpenRouter("")));
        Assert.Throws<ArgumentException>(() => new SystemOneDecisionClient(SystemOneClientOptions.ForOpenRouter(Key, model: " ")));
        Assert.Throws<ArgumentException>(() => new SystemOneDecisionClient(new SystemOneClientOptions
        {
            Endpoint = new Uri("http://openrouter.ai/api/v1/systemone"),   // plaintext to a real host
            ApiKey = Key,
            Model = "m",
        }));
        Assert.Throws<ArgumentNullException>(() => new SystemOneDecisionClient(null!));
    }

    [Fact]
    public void Ctor_AllowsLoopbackHttp_ForLocalTesting()
    {
        using var client = new SystemOneDecisionClient(new SystemOneClientOptions
        {
            Endpoint = new Uri("http://127.0.0.1:8080/v1/systemone"),
            ApiKey = Key,
            Model = "m",
        });
        Assert.Equal("m", client.Model);
    }
}
