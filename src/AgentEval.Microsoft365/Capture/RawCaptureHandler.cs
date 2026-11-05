// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Microsoft365.Capture;

/// <summary>
/// A <see cref="DelegatingHandler"/> that tees every request and response of the A2A client into the current
/// <see cref="RawCaptureSink"/> (set per turn via <see cref="CurrentSink"/>) without altering what the SDK sees:
/// request bytes before dispatch, response bytes (or, for <c>text/event-stream</c>, each frame as it streams)
/// after. It sits <b>below</b> <c>WorkIqAuthHandler</c> in the pipeline, so the <c>Authorization</c> header is
/// already present and is redacted by the sink. Owned by lane B (M12).
/// </summary>
internal sealed class RawCaptureHandler : DelegatingHandler
{
    /// <summary>The sink of the active turn; null means pass-through (nothing is captured).</summary>
    public RawCaptureSink? CurrentSink { get; set; }

    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new NotImplementedException("TODO(M12)");
}
