// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AgentEval.PartnerDeskDemo.Tests;

/// <summary>Deterministic success/error responses for actual-process exit-code tests; never calls an external provider.</summary>
internal sealed class LoopbackChatServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serve;
    private readonly int _successfulResponses;
    private int _requests;

    public LoopbackChatServer(int successfulResponses)
    {
        _successfulResponses = successfulResponses;
        _listener.Start();
        _serve = ServeAsync();
    }

    public string[] Environment =>
    [
        "AI_INFERENCE_PROVIDER=openai-compatible",
        $"OPENAI_COMPATIBLE_ENDPOINT=http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1",
        "OPENAI_COMPATIBLE_MODEL=t35-local"
    ];

    private async Task ServeAsync()
    {
        try
        {
            while (true)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                await using var stream = client.GetStream();
                var header = new List<byte>();
                var one = new byte[1];
                while (header.Count < 4 || !header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 }))
                {
                    await stream.ReadExactlyAsync(one, _stop.Token);
                    header.Add(one[0]);
                }
                var length = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n")
                    .Where(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    .Select(line => int.Parse(line[15..].Trim())).Single();
                await stream.ReadExactlyAsync(new byte[length], _stop.Token);
                var ok = _requests++ < _successfulResponses;
                var body = Encoding.UTF8.GetBytes(ok
                    ? """{"id":"chatcmpl-local","object":"chat.completion","created":1,"model":"t35-local","choices":[{"index":0,"message":{"role":"assistant","content":"No actions taken."},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":2,"total_tokens":12}}"""
                    : """{"error":{"message":"T35 deterministic provider error","type":"invalid_request_error","code":"t35_test_error"}}""");
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {(ok ? "200 OK" : "400 Bad Request")}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
                await stream.WriteAsync(body, _stop.Token);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _serve;
        _stop.Dispose();
    }
}
