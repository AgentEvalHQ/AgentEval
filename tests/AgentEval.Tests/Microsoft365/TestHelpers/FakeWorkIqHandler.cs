// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentEval.Microsoft365.Diagnostics;
using Xunit;

namespace AgentEval.Tests.Microsoft365.TestHelpers;

/// <summary>One scripted reply of <see cref="FakeWorkIqHandler"/>.</summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="Body">The body text (JSON or SSE), when any.</param>
/// <param name="ContentType">The <c>Content-Type</c> of the body.</param>
/// <param name="Headers">Extra response headers (<c>request-id</c>, <c>Retry-After</c>…); <c>Content-Type</c> here overrides <paramref name="ContentType"/>.</param>
/// <param name="ChunkSize">For SSE: the byte size of each streamed chunk; null streams the whole body at once.</param>
/// <param name="Exception">When set, the handler throws it instead of replying.</param>
/// <param name="IsSse">Whether the body is streamed as <c>text/event-stream</c>.</param>
internal sealed record FakeWorkIqResponse(
    HttpStatusCode Status,
    string? Body,
    string ContentType,
    IReadOnlyDictionary<string, string>? Headers,
    int? ChunkSize,
    Exception? Exception,
    bool IsSse)
{
    /// <summary>A JSON reply: <paramref name="fixtureOrJson"/> is an embedded fixture name or literal JSON.</summary>
    public static FakeWorkIqResponse Json(string fixtureOrJson, HttpStatusCode status = HttpStatusCode.OK, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, FakeWorkIqHandler.ResolveText(fixtureOrJson), "application/json", headers, null, null, false);

    /// <summary>An SSE reply streamed in <paramref name="chunkSize"/>-byte chunks (null = all at once).</summary>
    public static FakeWorkIqResponse Sse(string fixtureOrText, int? chunkSize = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        if (chunkSize is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSize), "A chunk size must be positive.");
        }

        return new(HttpStatusCode.OK, FakeWorkIqHandler.ResolveText(fixtureOrText), "text/event-stream", headers, chunkSize, null, true);
    }

    /// <summary>An HTTP failure with a body and headers (for example 429 with <c>Retry-After</c>).</summary>
    public static FakeWorkIqResponse Fail(HttpStatusCode status, string? bodyJson = null, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, bodyJson is null ? null : FakeWorkIqHandler.ResolveText(bodyJson), "application/json", headers, null, null, false);

    /// <summary>A transport-level throw (the exception surfaces from <c>HttpClient.SendAsync</c>).</summary>
    public static FakeWorkIqResponse Throw(Exception exception) =>
        new(HttpStatusCode.InternalServerError, null, "application/json", null, null, exception ?? throw new ArgumentNullException(nameof(exception)), false);
}

/// <summary>One request the fake received, with its headers already redacted (<see cref="WorkIqRedaction.RedactHeaders(HttpRequestMessage)"/>).</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Uri">The request URI.</param>
/// <param name="Headers">Request + content headers, redacted copy (<c>Authorization</c> is <c>[redacted]</c>).</param>
/// <param name="BodyJson">The request body text, when any.</param>
/// <param name="JsonRpcMethod">The JSON-RPC <c>method</c> of a POST body, when it parsed.</param>
/// <param name="JsonRpcId">The JSON-RPC <c>id</c> of a POST body as raw JSON text (<c>"abc"</c> or <c>42</c>), when present.</param>
/// <param name="AuthorizationScheme">The scheme of the <c>Authorization</c> header (for example <c>Bearer</c>), never its parameter; null when absent.</param>
internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri Uri,
    IReadOnlyDictionary<string, string> Headers,
    string? BodyJson,
    string? JsonRpcMethod,
    string? JsonRpcId,
    string? AuthorizationScheme);

/// <summary>
/// The shared scripted <see cref="HttpMessageHandler"/> of the Microsoft 365 tests: routes agent-card GETs and
/// JSON-RPC POSTs (matched by the <c>method</c> in the body) to scripted replies — fixtures or literal bodies,
/// fixed or successive, JSON or chunked SSE, failures or thrown exceptions — and records every request with
/// redacted headers so a test can assert what was sent without ever seeing a token. No network is touched.
/// A request with no script throws <see cref="InvalidOperationException"/> naming the method.
/// </summary>
internal sealed class FakeWorkIqHandler : HttpMessageHandler
{
    /// <summary>The script key of agent-card GETs (<c>…/.well-known/agent-card.json</c>) for <see cref="FailWith"/> / <see cref="Throw"/>.</summary>
    public const string CardMethod = "<card>";

    /// <summary>The path suffix that identifies an agent-card request.</summary>
    public const string CardPathSuffix = "/.well-known/agent-card.json";

    private readonly object _gate = new();
    private readonly Dictionary<string, Script> _scripts = new(StringComparer.Ordinal);
    private readonly List<RecordedRequest> _requests = [];
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

    /// <summary>
    /// Whether a JSON reply's JSON-RPC <c>id</c> is rewritten to the request's id (default: true), so a fixed
    /// fixture id never trips a client that correlates replies with requests. SSE frames are never rewritten
    /// (their bytes are streamed exactly).
    /// </summary>
    public bool RewriteResponseId { get; set; } = true;

    /// <summary>Every request received so far, in order (a snapshot).</summary>
    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToArray();
            }
        }
    }

    /// <summary>How many agent-card GETs were received.</summary>
    public int CardRequestCount => CountOf(CardMethod);

    /// <summary>How many requests were received in total.</summary>
    public int TotalRequests
    {
        get
        {
            lock (_gate)
            {
                return _requests.Count;
            }
        }
    }

    /// <summary>Scripts the agent card: every card GET returns <paramref name="fixtureOrJson"/> with <paramref name="status"/>.</summary>
    public FakeWorkIqHandler OnCard(string fixtureOrJson, HttpStatusCode status = HttpStatusCode.OK, IReadOnlyDictionary<string, string>? headers = null) =>
        Set(CardMethod, Script.Repeating(FakeWorkIqResponse.Json(fixtureOrJson, status, headers)));

    /// <summary>Scripts a JSON-RPC method: every POST whose body <c>method</c> equals <paramref name="methodName"/> returns the reply.</summary>
    public FakeWorkIqHandler OnRpc(string methodName, string fixtureOrJson, HttpStatusCode status = HttpStatusCode.OK, IReadOnlyDictionary<string, string>? headers = null) =>
        Set(methodName, Script.Repeating(FakeWorkIqResponse.Json(fixtureOrJson, status, headers)));

    /// <summary>Scripts successive replies for a method (200 OK each); a call past the last one throws.</summary>
    public FakeWorkIqHandler OnRpcSequence(string methodName, params string[] fixtures)
    {
        ArgumentNullException.ThrowIfNull(fixtures);
        return OnRpcSequence(methodName, fixtures.Select(f => FakeWorkIqResponse.Json(f)).ToArray());
    }

    /// <summary>Scripts successive replies of any shape for a method; a call past the last one throws.</summary>
    public FakeWorkIqHandler OnRpcSequence(string methodName, params FakeWorkIqResponse[] responses)
    {
        ArgumentNullException.ThrowIfNull(responses);
        if (responses.Length == 0)
        {
            throw new ArgumentException("A sequence needs at least one reply.", nameof(responses));
        }

        return Set(methodName, Script.Sequence(responses));
    }

    /// <summary>Scripts an SSE reply: <c>text/event-stream</c> whose bytes are streamed in <paramref name="chunkSize"/>-byte reads (null = all at once).</summary>
    public FakeWorkIqHandler OnSse(string methodName, string sseFixtureOrText, int? chunkSize = null, IReadOnlyDictionary<string, string>? headers = null) =>
        Set(methodName, Script.Repeating(FakeWorkIqResponse.Sse(sseFixtureOrText, chunkSize, headers)));

    /// <summary>Scripts an HTTP failure for a method (or <see cref="CardMethod"/>).</summary>
    public FakeWorkIqHandler FailWith(string methodName, HttpStatusCode status, string? bodyJson = null, IReadOnlyDictionary<string, string>? headers = null) =>
        Set(methodName, Script.Repeating(FakeWorkIqResponse.Fail(status, bodyJson, headers)));

    /// <summary>Scripts a thrown exception for a method (or <see cref="CardMethod"/>).</summary>
    public FakeWorkIqHandler Throw(string methodName, Exception exception) =>
        Set(methodName, Script.Repeating(FakeWorkIqResponse.Throw(exception)));

    /// <summary>How many requests hit <paramref name="jsonRpcMethod"/> (or <see cref="CardMethod"/>).</summary>
    public int CountOf(string jsonRpcMethod)
    {
        ArgumentNullException.ThrowIfNull(jsonRpcMethod);
        lock (_gate)
        {
            return _counts.TryGetValue(jsonRpcMethod, out var n) ? n : 0;
        }
    }

    /// <summary>Fails the test if any request was received.</summary>
    public void AssertNoRequests()
    {
        var requests = Requests;
        if (requests.Count > 0)
        {
            Assert.Fail(
                $"Expected no HTTP requests but {requests.Count} were sent: " +
                string.Join("; ", requests.Select(r => $"{r.Method} {r.Uri} ({r.JsonRpcMethod ?? "no json-rpc method"})")));
        }
    }

    /// <summary>Forgets every recorded request and count (scripts stay).</summary>
    public void ClearRequests()
    {
        lock (_gate)
        {
            _requests.Clear();
            _counts.Clear();
        }
    }

    /// <summary>Resolves <paramref name="fixtureOrText"/>: an embedded fixture name is loaded, anything else is returned as is.</summary>
    public static string ResolveText(string fixtureOrText)
    {
        ArgumentNullException.ThrowIfNull(fixtureOrText);
        return FixtureLoader.Exists(fixtureOrText) ? FixtureLoader.Text(fixtureOrText) : fixtureOrText;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var (rpcMethod, rpcId) = ParseJsonRpc(body);
        var isCard = request.Method == HttpMethod.Get
            && request.RequestUri is { } uri
            && uri.AbsolutePath.EndsWith(CardPathSuffix, StringComparison.OrdinalIgnoreCase);
        var key = isCard ? CardMethod : rpcMethod;

        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri ?? new Uri("http://unknown.invalid/"),
            WorkIqRedaction.RedactHeaders(request),
            body,
            rpcMethod,
            rpcId,
            request.Headers.Authorization?.Scheme);

        FakeWorkIqResponse reply;
        lock (_gate)
        {
            _requests.Add(recorded);
            if (key is not null)
            {
                _counts[key] = CountOf(key) + 1;
            }

            if (key is null || !_scripts.TryGetValue(key, out var script))
            {
                throw new InvalidOperationException(
                    $"FakeWorkIqHandler has no script for {request.Method} {request.RequestUri} " +
                    $"(json-rpc method: {rpcMethod ?? "none"}). Scripted keys: {(_scripts.Count == 0 ? "none" : string.Join(", ", _scripts.Keys))}.");
            }

            reply = script.Next(key);
        }

        if (reply.Exception is not null)
        {
            throw reply.Exception;
        }

        var response = new HttpResponseMessage(reply.Status) { RequestMessage = request };
        if (reply.IsSse)
        {
            response.Content = new ChunkedContent(Encoding.UTF8.GetBytes(reply.Body ?? ""), reply.ChunkSize, reply.ContentType);
        }
        else if (reply.Body is not null)
        {
            var text = RewriteResponseId && rpcId is not null ? RewriteId(reply.Body, rpcId) : reply.Body;
            response.Content = new StringContent(text, Encoding.UTF8, reply.ContentType);
        }

        if (reply.Headers is not null)
        {
            foreach (var (name, value) in reply.Headers)
            {
                if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    response.Content ??= new ByteArrayContent([]);
                    response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
                }
                else if (!response.Headers.TryAddWithoutValidation(name, value))
                {
                    response.Content ??= new ByteArrayContent([]);
                    response.Content.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }

        return response;
    }

    private FakeWorkIqHandler Set(string key, Script script)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_gate)
        {
            _scripts[key] = script;
        }

        return this;
    }

    private static (string? Method, string? Id) ParseJsonRpc(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            string? method = null;
            if (document.RootElement.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String)
            {
                method = m.GetString();
            }

            string? id = null;
            if (document.RootElement.TryGetProperty("id", out var i) && i.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                id = i.GetRawText();
            }

            return (method, id);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string RewriteId(string body, string rawId)
    {
        try
        {
            if (JsonNode.Parse(body) is JsonObject envelope && envelope.ContainsKey("jsonrpc") && envelope.ContainsKey("id"))
            {
                envelope["id"] = JsonNode.Parse(rawId);
                return envelope.ToJsonString();
            }
        }
        catch (JsonException)
        {
            // Not an envelope: serve the body untouched.
        }

        return body;
    }

    private sealed class Script
    {
        private readonly FakeWorkIqResponse[] _responses;
        private readonly bool _repeatLast;
        private int _next;

        private Script(FakeWorkIqResponse[] responses, bool repeatLast)
        {
            _responses = responses;
            _repeatLast = repeatLast;
        }

        public static Script Repeating(FakeWorkIqResponse response) => new([response], repeatLast: true);

        public static Script Sequence(FakeWorkIqResponse[] responses) => new(responses, repeatLast: false);

        public FakeWorkIqResponse Next(string key)
        {
            if (_next < _responses.Length)
            {
                return _responses[_next++];
            }

            if (_repeatLast)
            {
                return _responses[^1];
            }

            throw new InvalidOperationException(
                $"FakeWorkIqHandler script for '{key}' is exhausted: {_responses.Length} scripted reply(ies), call #{_responses.Length + 1} received.");
        }
    }

    /// <summary>Body bytes served in bounded reads, so an SSE consumer sees chunk boundaries inside lines and inside UTF-8 sequences.</summary>
    private sealed class ChunkedContent : HttpContent
    {
        private readonly byte[] _bytes;
        private readonly int? _chunkSize;

        public ChunkedContent(byte[] bytes, int? chunkSize, string contentType)
        {
            _bytes = bytes;
            _chunkSize = chunkSize;
            Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
        {
            var size = _chunkSize ?? _bytes.Length;
            for (var offset = 0; offset < _bytes.Length; offset += size)
            {
                var length = Math.Min(size, _bytes.Length - offset);
                await stream.WriteAsync(_bytes.AsMemory(offset, length), cancellationToken);
                await stream.FlushAsync(cancellationToken);
                await Task.Yield();
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new ChunkedReadStream(_bytes, _chunkSize ?? _bytes.Length));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>A read-only stream that returns at most <c>chunkSize</c> bytes per read and yields between reads.</summary>
    private sealed class ChunkedReadStream : Stream
    {
        private readonly byte[] _bytes;
        private readonly int _chunkSize;
        private int _position;

        public ChunkedReadStream(byte[] bytes, int chunkSize)
        {
            _bytes = bytes;
            _chunkSize = Math.Max(1, chunkSize);
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var length = Math.Min(Math.Min(buffer.Length, _chunkSize), _bytes.Length - _position);
            if (length <= 0)
            {
                return 0;
            }

            _bytes.AsSpan(_position, length).CopyTo(buffer);
            _position += length;
            return length;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            return Read(buffer.Span);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
