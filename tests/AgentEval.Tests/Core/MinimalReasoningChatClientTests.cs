// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using AgentEval.Core;
using Microsoft.Extensions.AI;
using OpenAI;
using Xunit;

namespace AgentEval.Tests.Core;

/// <summary>
/// The reasoning level a request goes out at, read off the wire: the real OpenAI chat client (the one the samples, the
/// demos and the CLI build) sends to a fake provider that records each request body and rejects the levels it is told
/// to. Each test uses its own host, so the level remembered per provider and model does not leak between tests.
/// </summary>
public sealed class MinimalReasoningChatClientTests
{
    private static readonly ChatMessage[] Prompt = [new(ChatRole.User, "Say ok.")];

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GetResponseAsync_ChangingRequestModel_KeepsFallbackCachePerModel(bool metadata, bool streaming)
    {
        var provider = new RoutingProvider(metadata);
        using var client = new MinimalReasoningChatClient(provider);

        foreach (var model in new[] { "model-a", "model-b", "model-a" })
        {
            var options = new ChatOptions { ModelId = model };
            if (streaming)
                await foreach (var _ in client.GetStreamingResponseAsync(Prompt, options)) { }
            else
                await client.GetResponseAsync(Prompt, options);
        }

        Assert.Equal(new (string?, ReasoningEffort?)[]
        {
            ("model-a", ReasoningEffort.None), ("model-a", ReasoningEffort.Low), ("model-a", null),
            ("model-b", ReasoningEffort.None), ("model-a", null),
        }, provider.Requests);
        // Requests for other models do not change the default model's view.
        Assert.Equal(ReasoningEffort.None, client.CurrentLevel);
    }

    [Fact]
    public async Task GetResponseAsync_ExplicitDefaultModel_ReusesItsLearnedLevelForImplicitRequests()
    {
        var provider = new RoutingProvider(metadata: true, defaultModel: "model-a");
        using var client = new MinimalReasoningChatClient(provider);

        await client.GetResponseAsync(Prompt, new ChatOptions { ModelId = "model-a" });
        await client.GetResponseAsync(Prompt);

        Assert.Null(client.CurrentLevel);
        Assert.Equal(new (string?, ReasoningEffort?)[]
        {
            ("model-a", ReasoningEffort.None), ("model-a", ReasoningEffort.Low), ("model-a", null), ("model-a", null),
        }, provider.Requests);
    }

    private sealed class RoutingProvider(bool metadata, string defaultModel = "default-model") : IChatClient
    {
        private readonly ChatClientMetadata _metadata = new("routing", new Uri($"https://routing-{Guid.NewGuid():N}.test/"), defaultModel);
        public List<(string?, ReasoningEffort?)> Requests { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var model = options?.ModelId ?? defaultModel;
            var effort = options?.Reasoning?.Effort;
            Requests.Add((model, effort));
            if (model == "model-a" && effort is not null)
                throw new HttpRequestException("unsupported reasoning", null, HttpStatusCode.BadRequest);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            metadata && serviceType == typeof(ChatClientMetadata) ? _metadata : null;
        public void Dispose() { }
    }

    [Fact]
    public async Task ARequestWithoutAReasoningSetting_AsksForNone()
    {
        var provider = new FakeProvider();

        var response = await provider.Client().GetResponseAsync(Prompt);

        Assert.Equal("ok", response.Text);
        Assert.Equal(new string?[] { "none" }, provider.LevelsSent);
    }

    [Fact]
    public async Task AProviderThatRejectsNone_GetsLow_AndLowIsRememberedForTheModel()
    {
        var provider = new FakeProvider(rejects: ["none"]);

        await provider.Client().GetResponseAsync(Prompt);
        await provider.Client().GetResponseAsync(Prompt);   // a new client for the same endpoint and model

        Assert.Equal(new string?[] { "none", "low", "low" }, provider.LevelsSent);
    }

    [Fact]
    public async Task AProviderThatRejectsNoneAndLow_GetsItsOwnDefault()
    {
        var provider = new FakeProvider(rejects: ["none", "low"]);
        var client = provider.Client();

        await client.GetResponseAsync(Prompt);
        await client.GetResponseAsync(Prompt);

        Assert.Equal(new string?[] { "none", "low", null, null }, provider.LevelsSent);
        Assert.Null(((MinimalReasoningChatClient)client).CurrentLevel);
    }

    [Fact]
    public async Task CodeThatSetsReasoning_IsSentAsSet_AndNeverRetried()
    {
        var provider = new FakeProvider(rejects: ["high"]);
        var options = new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.High } };

        await Assert.ThrowsAsync<ClientResultException>(() => provider.Client().GetResponseAsync(Prompt, options));

        Assert.Equal(new string?[] { "high" }, provider.LevelsSent);
    }

    [Fact]
    public async Task AnErrorThatIsNotARejectedRequest_IsNotRetried()
    {
        var provider = new FakeProvider(status: HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<ClientResultException>(() => provider.Client().GetResponseAsync(Prompt));

        Assert.Equal(401, ex.Status);
        Assert.Equal(new string?[] { "none" }, provider.LevelsSent);
    }

    [Fact]
    public async Task ARequestRejectedAtEveryLevel_FailsWithTheProvidersError_AndNothingIsRemembered()
    {
        var provider = new FakeProvider(rejectsEverything: true);
        var client = provider.Client();

        var ex = await Assert.ThrowsAsync<ClientResultException>(() => client.GetResponseAsync(Prompt));

        Assert.Equal(400, ex.Status);
        Assert.Equal(new string?[] { "none", "low", null }, provider.LevelsSent);
        Assert.Equal(ReasoningEffort.None, ((MinimalReasoningChatClient)client).CurrentLevel);
    }

    [Fact]
    public async Task AStreamingRequestRejectedAtNone_IsSentAgainAtLow()
    {
        var provider = new FakeProvider(rejects: ["none"]);

        var text = new StringBuilder();
        await foreach (var update in provider.Client().GetStreamingResponseAsync(Prompt))
            text.Append(update.Text);

        Assert.Equal("ok", text.ToString());
        Assert.Equal(new string?[] { "none", "low" }, provider.LevelsSent);
    }

    [Fact]
    public void WrappingTwice_KeepsOneWrapper()
    {
        var client = new FakeProvider().Client();

        Assert.Same(client, client.WithMinimalReasoning());
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, true)]
    [InlineData(HttpStatusCode.UnprocessableEntity, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public void ARejectedRequest_IsA400OrA422(HttpStatusCode status, bool rejected)
    {
        Assert.Equal(rejected, MinimalReasoningChatClient.IsRejectedRequest(new HttpRequestException("x", null, status)));
    }

    /// <summary>
    /// An OpenAI-compatible endpoint: records the <c>reasoning_effort</c> of each request (null when absent) and
    /// answers "ok", or rejects with 400 the levels in <c>rejects</c>.
    /// </summary>
    private sealed class FakeProvider(
        string[]? rejects = null, bool rejectsEverything = false, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        private readonly Uri _endpoint = new($"https://provider-{Guid.NewGuid():N}.test/v1");
        private readonly List<string?> _levels = [];

        public IReadOnlyList<string?> LevelsSent
        {
            get { lock (_levels) return _levels.ToArray(); }
        }

        public IChatClient Client() =>
            new OpenAIClient(new ApiKeyCredential("test-key"), new OpenAIClientOptions
                {
                    Endpoint = _endpoint,
                    Transport = new HttpClientPipelineTransport(new HttpClient(this, disposeHandler: false)),
                    RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
                })
                .GetChatClient("reasoning-model")
                .AsIChatClient()
                .WithMinimalReasoning();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = System.Text.Json.Nodes.JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var level = body["reasoning_effort"]?.GetValue<string>();
            lock (_levels) _levels.Add(level);

            if (status != HttpStatusCode.OK)
                return Error(status);
            if (rejectsEverything || (level is not null && rejects?.Contains(level) == true))
                return Error(HttpStatusCode.BadRequest);

            if (body["stream"]?.GetValue<bool>() == true)
            {
                const string chunk = "{\"id\":\"c\",\"object\":\"chat.completion.chunk\",\"created\":0,\"model\":\"reasoning-model\"," +
                                     "\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"data: {chunk}\n\ndata: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
                };
            }

            const string completion = "{\"id\":\"c\",\"object\":\"chat.completion\",\"created\":0,\"model\":\"reasoning-model\"," +
                                      "\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"},\"finish_reason\":\"stop\"}]," +
                                      "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(completion, Encoding.UTF8, "application/json") };
        }

        private static HttpResponseMessage Error(HttpStatusCode code) => new(code)
        {
            Content = new StringContent("{\"error\":{\"message\":\"invalid request\",\"type\":\"invalid_request_error\"}}",
                Encoding.UTF8, "application/json"),
        };
    }
}
