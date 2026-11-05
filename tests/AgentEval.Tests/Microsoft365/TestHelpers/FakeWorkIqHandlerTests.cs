// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AgentEval.Tests.Microsoft365.TestHelpers;

/// <summary>
/// The shared fake itself: routing by JSON-RPC method and card path, successive replies, SSE chunking that cuts
/// inside lines and inside UTF-8 sequences, failures and throws, and request recording with redacted headers.
/// </summary>
public sealed class FakeWorkIqHandlerTests
{
    private static readonly Uri Endpoint = new("https://workiq.svc.cloud.microsoft/a2a/");

    private static HttpClient Client(FakeWorkIqHandler handler) => new(handler, disposeHandler: false) { BaseAddress = Endpoint };

    private static StringContent Rpc(string method, string id = "\"rpc-test-1\"", string @params = "{}") =>
        new($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"{method}\",\"params\":{@params}}}", Encoding.UTF8, "application/json");

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    // ---------------------------------------------------------------------------------------------------------
    // Routing
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnRpc_PostWithMatchingMethod_ReturnsTheFixtureWithTheRequestsId()
    {
        var handler = new FakeWorkIqHandler().OnRpc("GetTask", "envelopes/gettask-completed.json");
        using var client = Client(handler);

        using var response = await client.PostAsync("", Rpc("GetTask", "\"my-id-7\""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        using var body = await ReadJsonAsync(response);
        Assert.Equal("my-id-7", body.RootElement.GetProperty("id").GetString());
        Assert.Equal("TASK_STATE_COMPLETED", body.RootElement.GetProperty("result").GetProperty("status").GetProperty("state").GetString());
        Assert.Equal(1, handler.CountOf("GetTask"));
        Assert.Equal(0, handler.CountOf("SendMessage"));
        var recorded = Assert.Single(handler.Requests);
        Assert.Equal("GetTask", recorded.JsonRpcMethod);
        Assert.Equal("\"my-id-7\"", recorded.JsonRpcId);
        Assert.Equal(HttpMethod.Post, recorded.Method);
    }

    [Fact]
    public async Task OnRpc_NumericId_IsRewrittenAsANumber()
    {
        var handler = new FakeWorkIqHandler().OnRpc("GetTask", "envelopes/gettask-working.json");
        using var client = Client(handler);

        using var response = await client.PostAsync("", Rpc("GetTask", "42"));

        using var body = await ReadJsonAsync(response);
        Assert.Equal(JsonValueKind.Number, body.RootElement.GetProperty("id").ValueKind);
        Assert.Equal(42, body.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task OnRpc_RewriteDisabled_ServesTheFixtureBytesUntouched()
    {
        var handler = new FakeWorkIqHandler { RewriteResponseId = false }.OnRpc("GetTask", "envelopes/gettask-completed.json");
        using var client = Client(handler);

        using var response = await client.PostAsync("", Rpc("GetTask", "\"my-id-7\""));

        Assert.Equal(FixtureLoader.Text("envelopes/gettask-completed.json"), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OnRpc_LiteralJson_IsServedAsIs()
    {
        var handler = new FakeWorkIqHandler().OnRpc("GetTask", "{\"jsonrpc\":\"2.0\",\"id\":\"x\",\"error\":{\"code\":-32001,\"message\":\"Task not found\"}}");
        using var client = Client(handler);

        using var response = await client.PostAsync("", Rpc("GetTask", "\"q\""));

        using var body = await ReadJsonAsync(response);
        Assert.Equal("q", body.RootElement.GetProperty("id").GetString());
        Assert.Equal(-32001, body.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task OnRpc_RepeatedCalls_ReturnTheSameReplyEveryTime()
    {
        var handler = new FakeWorkIqHandler().OnRpc("GetTask", "envelopes/gettask-working.json");
        using var client = Client(handler);

        for (var i = 0; i < 3; i++)
        {
            using var response = await client.PostAsync("", Rpc("GetTask"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(3, handler.CountOf("GetTask"));
    }

    [Fact]
    public async Task OnRpcSequence_SuccessiveCalls_GetSuccessiveRepliesThenThrow()
    {
        var handler = new FakeWorkIqHandler().OnRpcSequence("GetTask", "envelopes/gettask-working.json", "envelopes/gettask-completed.json");
        using var client = Client(handler);

        using var first = await client.PostAsync("", Rpc("GetTask"));
        using var second = await client.PostAsync("", Rpc("GetTask"));
        var third = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PostAsync("", Rpc("GetTask")));

        using var firstBody = await ReadJsonAsync(first);
        using var secondBody = await ReadJsonAsync(second);
        Assert.Equal("TASK_STATE_WORKING", firstBody.RootElement.GetProperty("result").GetProperty("status").GetProperty("state").GetString());
        Assert.Equal("TASK_STATE_COMPLETED", secondBody.RootElement.GetProperty("result").GetProperty("status").GetProperty("state").GetString());
        Assert.Contains("exhausted", third.Message, StringComparison.Ordinal);
        Assert.Equal(3, handler.CountOf("GetTask"));
    }

    [Fact]
    public async Task OnRpcSequence_MixedShapes_RunInOrder()
    {
        var handler = new FakeWorkIqHandler().OnRpcSequence(
            "GetTask",
            FakeWorkIqResponse.Fail(HttpStatusCode.TooManyRequests, "envelopes/http-429-body.json", new Dictionary<string, string> { ["Retry-After"] = "7" }),
            FakeWorkIqResponse.Json("envelopes/gettask-completed.json"));
        using var client = Client(handler);

        using var first = await client.PostAsync("", Rpc("GetTask"));
        using var second = await client.PostAsync("", Rpc("GetTask"));

        Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(7), first.Headers.RetryAfter!.Delta);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    [Fact]
    public async Task OnCard_GetOfTheWellKnownPath_ReturnsTheCard()
    {
        var handler = new FakeWorkIqHandler().OnCard("cards/default-card-two-interfaces.json");
        using var client = Client(handler);

        using var response = await client.GetAsync("https://workiq.svc.cloud.microsoft/a2a/.well-known/agent-card.json");
        using var agentSpecific = await client.GetAsync("https://workiq.svc.cloud.microsoft/a2a/U_abc.def/.well-known/agent-card.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = await ReadJsonAsync(response);
        Assert.Equal("Work IQ", body.RootElement.GetProperty("name").GetString());
        Assert.Equal(2, handler.CardRequestCount);
        Assert.Null(handler.Requests[0].JsonRpcMethod);
    }

    [Fact]
    public async Task FailWith_CardMethod_FailsDiscovery()
    {
        var handler = new FakeWorkIqHandler().FailWith(FakeWorkIqHandler.CardMethod, HttpStatusCode.NotFound, "{\"error\":\"no such agent\"}");
        using var client = Client(handler);

        using var response = await client.GetAsync(".well-known/agent-card.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, handler.CardRequestCount);
    }

    [Fact]
    public async Task Unscripted_Request_ThrowsNamingTheMethod()
    {
        var handler = new FakeWorkIqHandler().OnRpc("GetTask", "envelopes/gettask-working.json");
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PostAsync("", Rpc("SendMessage")));

        Assert.Contains("no script", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SendMessage", ex.Message, StringComparison.Ordinal);
        Assert.Contains("GetTask", ex.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
        Assert.Equal(1, handler.CountOf("SendMessage"));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Failures and throws
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FailWith_429_ReturnsStatusBodyAndHeaders()
    {
        var headers = new Dictionary<string, string>
        {
            ["Retry-After"] = "7",
            ["request-id"] = "4c2e9a7b-6d1f-4b3e-8a5c-2f7d0e9b1c64",
            ["Content-Type"] = "application/json; charset=utf-8",
        };
        var handler = new FakeWorkIqHandler().FailWith("SendMessage", HttpStatusCode.TooManyRequests, "envelopes/http-429-body.json", headers);
        using var client = Client(handler);

        using var response = await client.PostAsync("", Rpc("SendMessage"));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(7), response.Headers.RetryAfter!.Delta);
        Assert.Equal("4c2e9a7b-6d1f-4b3e-8a5c-2f7d0e9b1c64", response.Headers.GetValues("request-id").Single());
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType!.CharSet);
        using var body = await ReadJsonAsync(response);
        Assert.Equal("TooManyRequests", body.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task FailWith_NoBody_ReturnsAnEmptyResponse()
    {
        var handler = new FakeWorkIqHandler().FailWith("SendMessage", HttpStatusCode.Unauthorized);
        using var client = Client(handler);

        using var response = await client.PostAsync("", Rpc("SendMessage"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Throw_ScriptedException_SurfacesFromSendAsync()
    {
        var handler = new FakeWorkIqHandler().Throw("SendMessage", new HttpRequestException("connection reset"));
        using var client = Client(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync("", Rpc("SendMessage")));

        Assert.Equal("connection reset", ex.Message);
        Assert.Equal(1, handler.CountOf("SendMessage"));
    }

    // ---------------------------------------------------------------------------------------------------------
    // SSE chunking
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(64)]
    public async Task OnSse_ChunkSize_BoundsEveryReadAndReassemblesTheExactBytes(int chunkSize)
    {
        var expected = FixtureLoader.Bytes("sse/stream-happy-path.txt");
        Assert.Contains((byte)0xEE, expected); // U+E200 citation marker: a 3-byte UTF-8 sequence a 1-byte chunk must cut.
        var handler = new FakeWorkIqHandler().OnSse("SendStreamingMessage", "sse/stream-happy-path.txt", chunkSize);
        using var client = Client(handler);

        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "") { Content = Rpc("SendStreamingMessage") },
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Null(response.Content.Headers.ContentLength);
        using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[4096];
        using var assembled = new MemoryStream();
        var reads = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            Assert.True(read <= chunkSize, $"read returned {read} bytes, chunk size is {chunkSize}");
            assembled.Write(buffer, 0, read);
            reads++;
        }

        Assert.Equal(expected, assembled.ToArray());
        Assert.Equal((expected.Length + chunkSize - 1) / chunkSize, reads);
    }

    [Fact]
    public async Task OnSse_SmallChunks_StillParseAsTheSameFramesThroughTheSdkSseParser()
    {
        var whole = FixtureLoader.Bytes("sse/stream-happy-path.txt");
        var expectedFrames = await CountFramesAsync(new MemoryStream(whole));
        var handler = new FakeWorkIqHandler().OnSse("SendStreamingMessage", "sse/stream-happy-path.txt", chunkSize: 3);
        using var client = Client(handler);

        using var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "") { Content = Rpc("SendStreamingMessage") },
            HttpCompletionOption.ResponseHeadersRead);
        using var stream = await response.Content.ReadAsStreamAsync();

        Assert.True(expectedFrames > 1);
        Assert.Equal(expectedFrames, await CountFramesAsync(stream));
    }

    [Fact]
    public async Task OnSse_NoChunkSize_ServesEverythingAtOnce()
    {
        var handler = new FakeWorkIqHandler().OnSse("SubscribeToTask", "sse/subscribe-first-frame-snapshot.txt");
        using var client = Client(handler);

        using var response = await client.PostAsync("", Rpc("SubscribeToTask"));

        Assert.Equal(FixtureLoader.Text("sse/subscribe-first-frame-snapshot.txt"), await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void OnSse_NonPositiveChunk_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FakeWorkIqHandler().OnSse("SendStreamingMessage", "sse/stream-happy-path.txt", 0));
    }

    // ---------------------------------------------------------------------------------------------------------
    // Recording
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Requests_Always_RecordRedactedHeadersBodyAndScheme()
    {
        var handler = new FakeWorkIqHandler().OnRpc("SendMessage", "envelopes/send-direct-message.json");
        using var client = Client(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "") { Content = Rpc("SendMessage", "\"id-1\"", "{\"message\":{\"text\":\"hi\"}}") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "secret-token-value");
        request.Headers.TryAddWithoutValidation("A2A-Version", "1.0");
        request.Headers.TryAddWithoutValidation("Cookie", "session=abc");

        using var response = await client.SendAsync(request);

        var recorded = Assert.Single(handler.Requests);
        Assert.Equal(new Uri("https://workiq.svc.cloud.microsoft/a2a/"), recorded.Uri);
        Assert.Equal("[redacted]", recorded.Headers["Authorization"]);
        Assert.Equal("[redacted]", recorded.Headers["Cookie"]);
        Assert.Equal("1.0", recorded.Headers["A2A-Version"]);
        Assert.StartsWith("application/json", recorded.Headers["Content-Type"], StringComparison.Ordinal);
        Assert.Equal("Bearer", recorded.AuthorizationScheme);
        Assert.Contains("\"text\":\"hi\"", recorded.BodyJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-token-value", recorded.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Requests_WithoutAuthorization_RecordANullScheme()
    {
        var handler = new FakeWorkIqHandler().OnCard("cards/card-untrusted-host.json");
        using var client = Client(handler);

        using var response = await client.GetAsync(".well-known/agent-card.json");

        Assert.Null(Assert.Single(handler.Requests).AuthorizationScheme);
    }

    [Fact]
    public async Task AssertNoRequests_AfterARequest_FailsNamingIt()
    {
        var handler = new FakeWorkIqHandler().OnRpc("GetTask", "envelopes/gettask-working.json");
        using var client = Client(handler);
        handler.AssertNoRequests();

        using var response = await client.PostAsync("", Rpc("GetTask"));

        var ex = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => handler.AssertNoRequests());
        Assert.Contains("GetTask", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.TotalRequests);
    }

    [Fact]
    public async Task ClearRequests_Always_ForgetsRecordingsButKeepsScripts()
    {
        var handler = new FakeWorkIqHandler().OnRpc("GetTask", "envelopes/gettask-working.json");
        using var client = Client(handler);
        using var first = await client.PostAsync("", Rpc("GetTask"));

        handler.ClearRequests();

        handler.AssertNoRequests();
        Assert.Equal(0, handler.CountOf("GetTask"));
        using var second = await client.PostAsync("", Rpc("GetTask"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
    }

    private static async Task<int> CountFramesAsync(Stream stream)
    {
        var frames = 0;
        await foreach (var item in SseParser.Create(stream).EnumerateAsync())
        {
            Assert.False(string.IsNullOrEmpty(item.Data));
            frames++;
        }

        return frames;
    }
}
