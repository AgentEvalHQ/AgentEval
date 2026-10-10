// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentEval.Interop.AssertAi;

/// <summary>
/// A minimal HTTP server for an <see cref="AssertAiTarget"/>, for when the agent has no web host of its own
/// (<c>agenteval assert-ai serve</c> uses it). Point ASSERT's <c>pipeline.inference.target.endpoint</c> at
/// <see cref="Endpoint"/>.
/// </summary>
/// <remarks>
/// <para>
/// It answers POST with <c>Content-Type: application/json</c> (ASSERT refuses any other type), 400 with a JSON error
/// for a body that is not ASSERT's request, and 500 when the agent fails, which ASSERT records as a target error.
/// It serves several requests at once; ASSERT sends up to <c>pipeline.inference.concurrency</c> (default 10).
/// </para>
/// <para>
/// ASSERT refuses a literal <c>127.0.0.1</c> endpoint unless <c>ASSERT_ALLOW_PRIVATE_ENDPOINTS=1</c> is set, but
/// accepts the host name <c>localhost</c>, which is what this server listens on by default.
/// </para>
/// <para>
/// <b>Host names.</b> The server answers only requests addressed to its host name: a request ASSERT sends from a
/// container to <c>host.docker.internal</c> is refused before it reaches the server. Listen on <c>+</c> (every host
/// name) for that. On Windows, a host other than <c>localhost</c> needs a URL reservation made once by an
/// administrator (<c>netsh http add urlacl url=http://+:PORT/ user=Everyone</c>); Linux and macOS need none.
/// </para>
/// </remarks>
public sealed class AssertAiTargetServer : IAsyncDisposable
{
    private const int MaxRequestBytes = 4 * 1024 * 1024;
    private readonly HttpListener _listener;
    private readonly AssertAiTarget _target;
    private readonly TextWriter? _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly string _path;
    private int _served;

    private AssertAiTargetServer(HttpListener listener, AssertAiTarget target, Uri endpoint, string path, TextWriter? log)
    {
        _listener = listener;
        _target = target;
        _log = log;
        _path = path;
        Endpoint = endpoint;
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>The URL to give ASSERT.</summary>
    public Uri Endpoint { get; }

    /// <summary>Requests answered so far (any status).</summary>
    public int Served => Volatile.Read(ref _served);

    /// <summary>Starts serving <paramref name="target"/>.</summary>
    /// <param name="target">The target.</param>
    /// <param name="port">The port.</param>
    /// <param name="path">The path, e.g. <c>/assert</c> (with or without a trailing slash); <c>/</c> answers every path.</param>
    /// <param name="host">The host name to listen on; <c>localhost</c> by default, <c>+</c> or <c>*</c> for every host name.</param>
    /// <param name="log">Receives one line per request, when given.</param>
    public static AssertAiTargetServer Start(AssertAiTarget target, int port, string path = "/", string host = "localhost", TextWriter? log = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var normalized = "/" + (path ?? "/").Trim('/');
        var wildcard = host is "+" or "*";
        var endpoint = new Uri($"http://{(wildcard ? "localhost" : host)}:{port}{normalized}");   // checked before anything is opened
        var listener = new HttpListener();
        try
        {
            listener.Prefixes.Add($"http://{host}:{port}/");   // the path is matched per request: a prefix would refuse it without its trailing slash
            listener.Start();
            return new AssertAiTargetServer(listener, target, endpoint, normalized, log);
        }
        catch
        {
            listener.Close();
            throw;
        }
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_stop.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }
            catch (HttpListenerException)
            {
                continue;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var started = DateTimeOffset.UtcNow;
        int status;
        string body;
        try
        {
            var requested = "/" + (context.Request.Url?.AbsolutePath ?? "/").Trim('/');
            if (_path != "/" && !string.Equals(requested, _path, StringComparison.Ordinal))
            {
                (status, body) = (404, Error($"The target is at {_path}."));
            }
            else if (context.Request.HttpMethod != "POST")
            {
                (status, body) = (405, Error("ASSERT sends POST."));
            }
            else
            {
                var text = await ReadBodyAsync(context.Request).ConfigureAwait(false);
                if (text is null)
                {
                    (status, body) = (413, Error($"The request is larger than {MaxRequestBytes} bytes."));
                }
                else
                {
                    try
                    {
                        body = await _target.RespondJsonAsync(text, _stop.Token).ConfigureAwait(false);
                        status = 200;
                    }
                    catch (InvalidDataException bad)
                    {
                        (status, body) = (400, Error(bad.Message));
                    }
                    catch (Exception failed) when (failed is not OperationCanceledException || !_stop.IsCancellationRequested)
                    {
                        (status, body) = (500, Error($"The agent failed: {failed.GetType().Name}: {failed.Message}"));
                    }
                }
            }
        }
        catch (Exception unexpected)
        {
            (status, body) = (500, Error($"{unexpected.GetType().Name}: {unexpected.Message}"));
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            context.Response.Close();
        }
        catch (Exception)
        {
            // The caller went away (ASSERT's timeout); nothing to answer.
        }

        Interlocked.Increment(ref _served);
        _log?.WriteLine($"  {started:HH:mm:ss} {context.Request.HttpMethod} {context.Request.Url?.AbsolutePath} -> {status} ({(DateTimeOffset.UtcNow - started).TotalMilliseconds:0} ms)");
    }

    private static async Task<string?> ReadBodyAsync(HttpListenerRequest request)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.InputStream.ReadAsync(chunk).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxRequestBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string Error(string message) => new JsonObject { ["error"] = message }.ToJsonString(AssertAiJson.Write);

    /// <summary>Stops serving.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The loop ends with the listener.
        }

        _listener.Close();
        _stop.Dispose();
    }
}
