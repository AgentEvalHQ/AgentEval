// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentEval.Tests.TestHelpers;

/// <summary>A local OpenAI-compatible endpoint with scripted replies and usage; it exercises the real HTTP client.</summary>
internal sealed class LocalChatEndpoint : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<JsonObject> _requests = new();
    private readonly ConcurrentQueue<string> _authorizationHeaders = new();
    private readonly ConcurrentQueue<string> _apiKeyHeaders = new();

    private LocalChatEndpoint(WebApplication app) => _app = app;

    public string Endpoint { get; private set; } = "";

    public IReadOnlyList<JsonObject> Requests => _requests.ToArray();

    public IReadOnlyList<string> AuthorizationHeaders => _authorizationHeaders.ToArray();

    public IReadOnlyList<string> ApiKeyHeaders => _apiKeyHeaders.ToArray();

    public static async Task<LocalChatEndpoint> StartAsync(Func<JsonObject, CancellationToken, Task<string>> reply,
        int statusCode = StatusCodes.Status200OK, Func<JsonObject, (long? Input, long? Output)?>? usage = null, string? probePack = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        // Let the OS reserve a port in the actual server bind, avoiding a free-port lookup race under parallel load.
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var endpoint = new LocalChatEndpoint(app);
        async Task HandleChat(HttpContext context)
        {
            var request = (await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted))!.AsObject();
            endpoint._requests.Enqueue(request);
            endpoint._authorizationHeaders.Enqueue(context.Request.Headers.Authorization.ToString());
            endpoint._apiKeyHeaders.Enqueue(context.Request.Headers["api-key"].ToString());
            if (statusCode != StatusCodes.Status200OK)
            {
                context.Response.StatusCode = statusCode;
                await context.Response.WriteAsJsonAsync(new { error = new { message = "Scripted endpoint failure", type = "server_error" } },
                    context.RequestAborted);
                return;
            }
            var text = await reply(request, context.RequestAborted);
            (long? Input, long? Output)? report = usage is null ? (10L, 5L) : usage(request);
            await context.Response.WriteAsJsonAsync(new
            {
                id = "chatcmpl-local",
                @object = "chat.completion",
                created = 1,
                model = (string?)request["model"] ?? "local-model",
                choices = new[] { new { index = 0, message = new { role = "assistant", content = text }, finish_reason = "stop" } },
                usage = report is { } counts ? new
                { prompt_tokens = counts.Input, completion_tokens = counts.Output, total_tokens = counts.Input + counts.Output } : null,
            }, context.RequestAborted);
        }
        if (probePack is not null)
            app.MapGet("/probes.json", () => Microsoft.AspNetCore.Http.Results.Text(probePack, "application/json"));
        app.MapPost("/v1/chat/completions", HandleChat);
        app.MapPost("/openai/deployments/{deployment}/chat/completions", HandleChat);
        try
        {
            await app.StartAsync();
            endpoint.Endpoint = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/v1";
            return endpoint;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    public static Task<LocalChatEndpoint> StartAsync(string reply = "Hi") =>
        StartAsync((_, _) => Task.FromResult(reply));

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await _app.StopAsync(timeout.Token);
        await _app.DisposeAsync();
    }
}
