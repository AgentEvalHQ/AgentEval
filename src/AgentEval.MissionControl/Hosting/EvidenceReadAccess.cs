// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace AgentEval.MissionControl.Hosting;

internal sealed record EvidenceReadAccessOptions(byte[]? KeyHash);

internal static class EvidenceReadAccess
{
    internal static void Configure(WebApplicationBuilder builder)
    {
        if (!builder.Configuration.GetValue<bool>("AgentEval:Queries:Enabled")) return;
        var key = builder.Configuration["AgentEval:Queries:ReadApiKey"];
        byte[]? hash = null;
        if (key is not null)
        {
            if (key.Length is < 32 or > 256 || key.Any(c => c < '!' || c > '~'))
                throw new InvalidOperationException("Expected: ReadApiKey of 32..256 printable ASCII characters without spaces. Actual: invalid read key. Suggestions: configure a random secret or omit the key for direct loopback access. Because: enabled evidence reads require an explicit access boundary.");
            hash = SHA256.HashData(Encoding.ASCII.GetBytes(key));
        }
        builder.Services.AddSingleton(new EvidenceReadAccessOptions(hash));
    }

    internal static void Use(WebApplication app)
    {
        var options = app.Services.GetService<EvidenceReadAccessOptions>();
        if (options is null) return;
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/graphql") &&
                !context.Request.Path.StartsWithSegments("/api/v1/evidence")) { await next(context); return; }
            context.Response.Headers.CacheControl = "no-store";
            if (Status(context, options) is { } status)
            {
                context.Response.StatusCode = status;
                if (status == 401) context.Response.Headers.WWWAuthenticate = "Bearer";
                return;
            }
            await next(context);
        });
    }

    private static int? Status(HttpContext context, EvidenceReadAccessOptions options)
    {
        var request = context.Request;
        var site = request.Headers["Sec-Fetch-Site"];
        if (site.Count > 1 || (site.Count == 1 && site[0] is not ("same-origin" or "none"))) return 403;
        var origins = request.Headers.Origin;
        if (origins.Count != 0)
        {
            if (origins.Count != 1 || !Uri.TryCreate(origins[0], UriKind.Absolute, out var origin) ||
                !Uri.TryCreate(request.Scheme + "://" + request.Host.Value, UriKind.Absolute, out var target) ||
                origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
                origin.Scheme != target.Scheme || !origin.IdnHost.Equals(target.IdnHost, StringComparison.OrdinalIgnoreCase) || origin.Port != target.Port)
                return 403;
        }
        var peer = context.Connection.RemoteIpAddress;
        var local = peer is not null && IPAddress.IsLoopback(peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer) && LoopbackHost(request.Host.Host);
        if (options.KeyHash is null) return local ? null : 403;
        var values = request.Headers.Authorization;
        var authorization = values.Count == 1 ? values[0] : null;
        if (authorization is null || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || authorization.Length is < 39 or > 263)
            return 401;
        var key = authorization[7..];
        if (key.Any(c => c < '!' || c > '~') || !CryptographicOperations.FixedTimeEquals(options.KeyHash, SHA256.HashData(Encoding.ASCII.GetBytes(key))))
            return 401;
        return request.IsHttps || local ? null : 403;
    }

    private static bool LoopbackHost(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address));
}
