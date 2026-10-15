// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace AgentEval.MissionControl.Hosting;

internal static class IngestionAccessPolicy
{
    internal static byte[] HashKey(string? key)
    {
        if (key is null || key.Length is < 32 or > 256 || key.Any(c => c < '!' || c > '~'))
            throw new InvalidOperationException("Expected: an ingestion ApiKey of 32..256 printable ASCII characters without spaces. " +
                "Actual: absent or invalid key. Suggestions: configure a random secret through the environment. Because: ingestion is a write endpoint.");
        return SHA256.HashData(Encoding.ASCII.GetBytes(key));
    }

    internal static IResult? Check(HttpContext context, byte[] keyHash) =>
        Status(context, keyHash) is { } status ? HttpResults.StatusCode(status) : null;

    internal static int? Status(HttpContext context, byte[] keyHash)
    {
        context.Response.Headers.CacheControl = "no-store";
        var values = context.Request.Headers.Authorization;
        var value = values.Count == 1 ? values[0] : null;
        if (value is null || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || value.Length is < 39 or > 263)
            return 401;
        var key = value[7..];
        if (key.Any(c => c < '!' || c > '~') || !CryptographicOperations.FixedTimeEquals(keyHash, SHA256.HashData(Encoding.ASCII.GetBytes(key))))
            return 401;
        if (context.Request.Headers.ContainsKey("Origin") || context.Request.Headers.ContainsKey("Sec-Fetch-Site")) return 403;
        var remote = context.Connection.RemoteIpAddress;
        if (!context.Request.IsHttps && (remote is null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote))) return 403;
        return null;
    }
}
