// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using AgentEval.MissionControl.Hosting;
using AgentEval.Results.Ingestion;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using HttpResults = Microsoft.AspNetCore.Http.Results;

namespace AgentEval.MissionControl.Rest;

internal static class IngestionEndpoints
{
    internal static void MapIngestionEndpoints(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("AgentEval:Ingestion:Enabled")) return;
        app.MapPost("/api/v1/ingestion/runs", UploadAsync);
        app.MapPost("/api/v1/ingestion/runs/{stageId}/finalize", FinalizeAsync);
        app.MapGet("/api/v1/ingestion/stages", (HttpContext context, IAefIngestionRecovery recovery, IngestionHttpOptions options, int? limit) =>
            RecoveryRequest(context, options, ct => recovery.Inspect(limit ?? 64, ct)));
        app.MapPost("/api/v1/ingestion/stages/{stageId}/recover", (HttpContext context, string stageId, IAefIngestionRecovery recovery, IngestionHttpOptions options) =>
            RecoveryRequest(context, options, ct => recovery.Recover(stageId, ct)));
        app.MapDelete("/api/v1/ingestion/stages/{stageId}", (HttpContext context, string stageId, IAefIngestionRecovery recovery, IngestionHttpOptions options) =>
            RecoveryRequest(context, options, ct => recovery.DiscardStage(stageId, ct)));
    }

    private static async Task<IResult> RecoveryRequest(HttpContext context, IngestionHttpOptions options, Func<CancellationToken, object> operation)
    {
        if (CheckAccess(context, options) is { } denied) return denied;
        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            return HttpResults.BadRequest(new { error = "unexpected-body" });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        try { return HttpResults.Ok(await Task.Run(() => operation(timeout.Token), timeout.Token)); }
        catch (AefFinalizationException error)
        { return HttpResults.Json(new { error = error.Code }, statusCode: error.Code == "path" ? 400 : error.Code == "busy" ? 429 : 409); }
        catch (AefStagingException error) { return HttpResults.Json(new { error = error.Code }, statusCode: 409); }
        catch (ArgumentOutOfRangeException) { return HttpResults.BadRequest(new { error = "invalid-limit" }); }
        catch (DirectoryNotFoundException) { return HttpResults.NotFound(new { error = "stage-not-found" }); }
        catch (FileNotFoundException) { return HttpResults.Conflict(new { error = "stage-state-missing" }); }
        catch (OperationCanceledException) { return HttpResults.StatusCode(StatusCodes.Status408RequestTimeout); }
        catch (IOException) { return HttpResults.Json(new { error = "recovery-unavailable" }, statusCode: 503); }
        catch (UnauthorizedAccessException) { return HttpResults.Json(new { error = "recovery-unavailable" }, statusCode: 503); }
    }

    private static async Task<IResult> UploadAsync(HttpContext context, IAefRunStager stager, IngestionHttpOptions options)
    {
        if (CheckAccess(context, options) is { } denied) return denied;
        return await UploadAuthorizedAsync(context, stager, options);
    }

    private static IResult? CheckAccess(HttpContext context, IngestionHttpOptions options)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!Authorized(context, options)) return HttpResults.StatusCode(StatusCodes.Status401Unauthorized);
        if (context.Request.Headers.ContainsKey("Origin") || context.Request.Headers.ContainsKey("Sec-Fetch-Site"))
            return HttpResults.StatusCode(StatusCodes.Status403Forbidden);
        var remote = context.Connection.RemoteIpAddress;
        if (!context.Request.IsHttps && (remote is null || !IPAddress.IsLoopback(remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote)))
            return HttpResults.StatusCode(StatusCodes.Status403Forbidden);
        return null;
    }

    private static async Task<IResult> FinalizeAsync(HttpContext context, string stageId, IAefRunFinalizer finalizer, IngestionHttpOptions options)
    {
        if (CheckAccess(context, options) is { } denied) return denied;
        if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
            return HttpResults.BadRequest(new { error = "unexpected-body" });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        try
        {
            // The protocol verifier is synchronous. Cancellation is checked before publication, but a running
            // verification phase must finish before this task can return a timeout or release its capacity.
            var accepted = await Task.Run(() => finalizer.FinalizeStage(stageId, timeout.Token), timeout.Token);
            return HttpResults.Ok(new { stageId = accepted.StageId, status = "accepted", verified = true,
                integrity = "intact", runHash = accepted.RunHash, receiptPersisted = accepted.ReceiptPersisted,
                alreadyAccepted = accepted.AlreadyAccepted, receiptArchived = accepted.ReceiptArchived });
        }
        catch (AefFinalizationException error)
        {
            var status = error.Code switch { "path" => 400, "busy" => 429, "limit" => 413,
                "capacity" => 503, "state" or "conflict" => 409, _ => 422 };
            return HttpResults.Json(new { error = error.Code }, statusCode: status);
        }
        catch (DirectoryNotFoundException) { return HttpResults.NotFound(new { error = "stage-not-found" }); }
        catch (FileNotFoundException) { return HttpResults.Conflict(new { error = "stage-state-missing" }); }
        catch (OperationCanceledException) { return HttpResults.StatusCode(StatusCodes.Status408RequestTimeout); }
        catch (IOException) { return HttpResults.Json(new { error = "finalization-unavailable" }, statusCode: 503); }
        catch (UnauthorizedAccessException) { return HttpResults.Json(new { error = "finalization-unavailable" }, statusCode: 503); }
    }

    private static async Task<IResult> UploadAuthorizedAsync(HttpContext context, IAefRunStager stager, IngestionHttpOptions options)
    {
        if (context.Request.Headers.ContainsKey("Content-Encoding")) return HttpResults.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var type)
            || !string.Equals(type.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return HttpResults.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        var boundary = HeaderUtilities.RemoveQuotes(type.Boundary).Value;
        if (string.IsNullOrEmpty(boundary) || boundary.Length > 128 || boundary.Any(c => c < ' ' || c > '~'))
            return HttpResults.BadRequest(new { error = "invalid-boundary" });
        if (context.Request.ContentLength > options.Staging.MaxBytes) return HttpResults.StatusCode(StatusCodes.Status413PayloadTooLarge);

        var bodyFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodyFeature is { IsReadOnly: false }) bodyFeature.MaxRequestBodySize = options.Staging.MaxBytes;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var body = new LimitedUploadStream(context.Request.Body, options.Staging.MaxBytes);
        var reader = new MultipartReader(boundary, body)
        {
            HeadersCountLimit = 8,
            HeadersLengthLimit = 4096
        };
        try
        {
            var staged = await stager.StageAsync(ReadFiles(reader, body, timeout.Token), timeout.Token);
            return HttpResults.Json(new { stageId = staged.Id, status = "staged", verified = false, files = staged.FileCount, bytes = staged.Bytes },
                statusCode: StatusCodes.Status202Accepted);
        }
        catch (AefStagingException error)
        {
            var status = error.Code switch { "busy" => 429, "limit" => 413, "conflict" => 409, _ => 400 };
            return HttpResults.Json(new { error = error.Code }, statusCode: status);
        }
        catch (OperationCanceledException) { return HttpResults.StatusCode(StatusCodes.Status408RequestTimeout); }
        catch (InvalidDataException) { return HttpResults.BadRequest(new { error = "malformed-multipart" }); }
        catch (BadHttpRequestException error) { return HttpResults.StatusCode(error.StatusCode); }
        catch (IOException) { return HttpResults.Json(new { error = "staging-unavailable" }, statusCode: 503); }
        catch (UnauthorizedAccessException) { return HttpResults.Json(new { error = "staging-unavailable" }, statusCode: 503); }
    }

    private static bool Authorized(HttpContext context, IngestionHttpOptions options)
    {
        var values = context.Request.Headers.Authorization;
        if (values.Count != 1) return false;
        var value = values[0];
        if (value is null || !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || value.Length is < 39 or > 263) return false;
        var key = value[7..];
        if (key.Any(c => c < '!' || c > '~')) return false;
        return CryptographicOperations.FixedTimeEquals(options.KeyHash, SHA256.HashData(Encoding.ASCII.GetBytes(key)));
    }

    private static async IAsyncEnumerable<AefUploadFile> ReadFiles(MultipartReader reader, Stream body,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        MultipartSection? section;
        while ((section = await NextSection(reader, cancellationToken)) is not null)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                || !string.Equals(disposition.DispositionType.Value, "form-data", StringComparison.OrdinalIgnoreCase)
                || (!disposition.FileNameStar.HasValue && !disposition.FileName.HasValue)
                || section.Headers?.ContainsKey("Content-Transfer-Encoding") == true)
                throw new AefStagingException("path", "each part must provide one plain relative filename");
            var plain = HeaderUtilities.RemoveQuotes(disposition.FileName).Value;
            var filename = disposition.FileNameStar.HasValue ? disposition.FileNameStar.Value : plain;
            if (disposition.FileNameStar.HasValue && disposition.FileName.HasValue && filename != plain)
                throw new AefStagingException("path", "conflicting filename parameters");
            using var input = new MultipartUploadStream(section.Body);
            yield return new(filename ?? "", input);
        }
        // Account for an optional epilogue and require the full request to finish before marking it staged.
        var buffer = new byte[8192];
        while (await body.ReadAsync(buffer, cancellationToken) != 0) { }
    }

    private static async Task<MultipartSection?> NextSection(MultipartReader reader, CancellationToken cancellationToken)
    {
        try { return await reader.ReadNextSectionAsync(cancellationToken); }
        catch (IOException error) when (error is not AefStagingException and not BadHttpRequestException)
        { throw new InvalidDataException("Incomplete multipart input.", error); }
    }
}

internal abstract class ReadOnlyUploadStream(Stream inner) : Stream
{
    protected Stream Inner { get; } = inner;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

// Distinguish incomplete client multipart bytes from failures writing application-owned storage.
internal sealed class MultipartUploadStream(Stream inner) : ReadOnlyUploadStream(inner)
{
    public override int Read(byte[] buffer, int offset, int count)
    {
        try { return Inner.Read(buffer, offset, count); }
        catch (IOException error) when (error is not AefStagingException and not BadHttpRequestException)
        { throw new InvalidDataException("Incomplete multipart input.", error); }
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try { return await Inner.ReadAsync(buffer, cancellationToken); }
        catch (IOException error) when (error is not AefStagingException and not BadHttpRequestException)
        { throw new InvalidDataException("Incomplete multipart input.", error); }
    }
}

// Bounds raw request bytes, including multipart headers/preamble and chunked requests without Content-Length.
// Disposing this wrapper leaves the server-owned request stream open.
internal sealed class LimitedUploadStream(Stream inner, long limit) : ReadOnlyUploadStream(inner)
{
    private long _read;
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = Inner.Read(buffer, offset, (int)Math.Min(count, limit - _read + 1));
        Count(read);
        return read;
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await Inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, limit - _read + 1)], cancellationToken);
        Count(read);
        return read;
    }
    private void Count(int read)
    {
        _read += read;
        if (_read > limit) throw new AefStagingException("limit", "request byte limit exceeded");
    }
}
