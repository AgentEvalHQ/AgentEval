// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using Microsoft.AspNetCore.Http.Features;

namespace AgentEval.MissionControl.Hosting;

internal sealed record EvidenceQueryBudgetOptions(int RequestBytes, int QueryStringBytes, int ResponseBytes, int RequestSeconds);

internal sealed class EvidenceQueryBudget : IDisposable
{
    private readonly SemaphoreSlim _slots;
    private readonly EvidenceQueryBudgetOptions _options;
    private EvidenceQueryBudget(EvidenceQueryBudgetOptions options, int concurrency) { _options = options; _slots = new(concurrency); }

    internal static void Configure(WebApplicationBuilder builder)
    {
        if (!builder.Configuration.GetValue<bool>("AgentEval:Queries:Enabled")) return;
        var section = builder.Configuration.GetSection("AgentEval:Queries:HttpLimits");
        int Limit(string name, int fallback, int minimum, int maximum)
        {
            var value = section.GetValue(name, fallback);
            if (value < minimum || value > maximum)
                throw new InvalidOperationException($"Expected: {name} within {minimum}..{maximum}. Actual: invalid query HTTP limit. Suggestions: configure a finite value in range. Because: transport envelopes must remain bounded.");
            return value;
        }
        var options = new EvidenceQueryBudgetOptions(Limit("RequestBytes", 256 << 10, 1024, 1 << 20),
            Limit("QueryStringBytes", 64 << 10, 1024, 256 << 10), Limit("ResponseBytes", 8 << 20, 1024, 32 << 20),
            Limit("RequestSeconds", 30, 1, 120));
        var concurrency = Limit("Concurrency", 4, 1, 32);
        builder.Services.AddSingleton(_ => new EvidenceQueryBudget(options, concurrency));
    }

    internal static void Use(WebApplication app)
    {
        var budget = app.Services.GetService<EvidenceQueryBudget>();
        if (budget is not null) app.Use(budget.Invoke);
    }

    private async Task Invoke(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        if (!request.Path.Equals("/graphql", StringComparison.OrdinalIgnoreCase) && !request.Path.Equals("/graphql/", StringComparison.OrdinalIgnoreCase))
        { await next(context); return; }
        if (context.WebSockets.IsWebSocketRequest) { await Refuse(context, 400, "use-http-or-sse"); return; }
        // Nitro assets and the plain browser IDE document are not GraphQL operation envelopes.
        if (!HttpMethods.IsPost(request.Method) && !(HttpMethods.IsGet(request.Method) && request.QueryString.HasValue))
        { await next(context); return; }
        if (Encoding.UTF8.GetByteCount(request.QueryString.Value ?? "") > _options.QueryStringBytes || request.ContentLength > _options.RequestBytes)
        { await Refuse(context, 413, "request-byte-limit"); return; }
        if (!await _slots.WaitAsync(0, request.HttpContext.RequestAborted))
        { context.Response.Headers.RetryAfter = "1"; await Refuse(context, 429, "query-concurrency-limit"); return; }
        var originalRequest = request.Body; var originalAbort = context.RequestAborted;
        var originalResponse = context.Features.Get<IHttpResponseBodyFeature>()!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(originalAbort);
        deadline.CancelAfter(TimeSpan.FromSeconds(_options.RequestSeconds));
        using var input = new MemoryStream();
        using var output = new CappedResponse(_options.ResponseBytes);
        var overflow = false;
        try
        {
            context.RequestAborted = deadline.Token;
            var block = new byte[8192];
            while (true)
            {
                var remaining = _options.RequestBytes + 1 - (int)input.Length;
                var read = await originalRequest.ReadAsync(block.AsMemory(0, Math.Min(block.Length, remaining)), deadline.Token);
                if (read == 0) break;
                input.Write(block, 0, read);
                if (input.Length > _options.RequestBytes) { await Refuse(context, 413, "request-byte-limit"); return; }
            }
            input.Position = 0; request.Body = input;
            context.Response.Body = output;
            try
            {
                await next(context);
                await context.Features.Get<IHttpResponseBodyFeature>()!.CompleteAsync();
            }
            catch (ResponseLimitException) { overflow = true; }
            finally { context.Features.Set(originalResponse); }
            if (overflow || output.Exceeded) { await Refuse(context, 503, "response-byte-limit"); return; }
            deadline.Token.ThrowIfCancellationRequested();
            output.Position = 0;
            context.Response.ContentLength = output.Length;
            await output.CopyToAsync(context.Response.Body, deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            context.Features.Set(originalResponse);
            if (originalAbort.IsCancellationRequested || context.Response.HasStarted) context.Abort();
            else { context.Response.StatusCode = 408; context.Response.ContentLength = 0; }
        }
        finally
        {
            request.Body = originalRequest; context.RequestAborted = originalAbort;
            context.Features.Set(originalResponse); _slots.Release();
        }
    }

    private static async Task Refuse(HttpContext context, int status, string code)
    {
        if (context.Response.HasStarted) { context.Abort(); return; }
        context.Response.StatusCode = status; context.Response.ContentLength = null;
        context.Response.ContentType = "application/json";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try { await context.Response.WriteAsync("{\"errors\":[{\"message\":\"Query refused by host limit.\",\"extensions\":{\"code\":\"" + code + "\"}}]}", deadline.Token); }
        catch (OperationCanceledException) { context.Abort(); }
    }

    private sealed class ResponseLimitException : IOException { }
    private sealed class CappedResponse(int limit) : MemoryStream
    {
        internal bool Exceeded { get; private set; }
        private void Check(long length)
        {
            if (length <= limit) return;
            Exceeded = true; throw new ResponseLimitException();
        }
        public override void SetLength(long value) { Check(value); base.SetLength(value); }
        public override void Write(byte[] buffer, int offset, int count) { Check(Position + count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(Position + buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(Position + 1); base.WriteByte(value); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Write(buffer.Span); return ValueTask.CompletedTask; }
    }

    public void Dispose() => _slots.Dispose();
}
