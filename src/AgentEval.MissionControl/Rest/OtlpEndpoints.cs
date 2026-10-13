// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using AgentEval.MissionControl.Hosting;
using AgentEval.Results.Observations;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace AgentEval.MissionControl.Rest;

internal static class OtlpEndpoints
{
    internal static void MapOtlpEndpoints(this WebApplication app)
    {
        if (app.Configuration.GetValue<bool>("AgentEval:Otlp:Enabled")) app.MapPost("/v1/traces", ExportAsync);
    }

    private static async Task<IResult> ExportAsync(HttpContext context, OtlpHttpOptions options, OtlpReceiver receiver)
    {
        var validType = MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var media);
        var isJson = validType && string.Equals(media!.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase);
        if (IngestionAccessPolicy.Status(context, options.KeyHash) is { } denied) return Reply(denied, isJson, "access-denied");
        if (!validType || (!isJson && !string.Equals(media!.MediaType.Value, "application/x-protobuf", StringComparison.OrdinalIgnoreCase))
            || (media!.Charset.HasValue && !string.Equals(media.Charset.Value?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)))
            return Reply(415, isJson, "unsupported-content-type");
        var encodings = context.Request.Headers.ContentEncoding;
        var encoding = encodings.Count == 0 ? "" : encodings.Count == 1 ? encodings[0] : "unsupported";
        var compression = string.Equals(encoding, "gzip", StringComparison.OrdinalIgnoreCase) ? OtlpCompression.Gzip : OtlpCompression.None;
        if (compression == OtlpCompression.None && !string.IsNullOrEmpty(encoding) && !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase))
            return Reply(415, isJson, "unsupported-content-encoding");
        if (context.Request.ContentLength > options.Limits.MaxRequestBytes) return Reply(413, isJson, "request-byte-limit");
        var bodyLimit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyLimit is { IsReadOnly: false }) bodyLimit.MaxRequestBodySize = options.Limits.MaxRequestBytes;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Limits.TimeoutSeconds));
        try
        {
            await receiver.AcceptAsync(context.Request.Body, isJson, compression, timeout.Token);
            return Reply(200, isJson);
        }
        catch (OtlpIngestionException error)
        {
            return Reply(error.Code switch { "busy" => 429, "storage" => 503, "limit" => 413, _ => 400 }, isJson, error.Code);
        }
        catch (OperationCanceledException) { return Reply(503, isJson, "request-timeout"); }
        catch (BadHttpRequestException error) { return Reply(error.StatusCode, isJson, "invalid-request-body"); }
        catch (InvalidDataException) { return Reply(400, isJson, "invalid-compressed-body"); }
        catch (IOException) { return Reply(503, isJson, "observation-storage-unavailable"); }
        catch (UnauthorizedAccessException) { return Reply(503, isJson, "observation-storage-unavailable"); }
    }

    private static IResult Reply(int status, bool json, string? error = null)
    {
        if (json) return new OtlpReply(status, "application/json", error is null ? "{}"u8.ToArray() : JsonSerializer.SerializeToUtf8Bytes(new { message = error }));
        if (error is null) return new OtlpReply(status, "application/x-protobuf", []);
        // google.rpc.Status.message is field 2. All fixed, path-free messages here fit a one-byte length.
        var message = Encoding.UTF8.GetBytes(error);
        if (message.Length > 127) throw new InvalidOperationException("OTLP status message exceeds the fixed encoder limit.");
        return new OtlpReply(status, "application/x-protobuf", [0x12, (byte)message.Length, .. message]);
    }

    private sealed record OtlpReply(int Status, string ContentType, byte[] Body) : IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.StatusCode = Status;
            context.Response.ContentType = ContentType;
            context.Response.ContentLength = Body.Length;
            if (Status is 429 or 503) context.Response.Headers.RetryAfter = "1";
            await context.Response.Body.WriteAsync(Body, context.RequestAborted);
        }
    }
}
