// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace AgentEval.Microsoft365.Evidence;

/// <summary>
/// Turns one turn's reduced task state (or direct message), raw capture references, diagnostic profile and
/// identity into a <see cref="Microsoft365TurnEvidence"/> document. Deterministic: the same inputs and the same
/// <see cref="Microsoft365Evidence.NormalizerVersion"/> yield the same document. Owns the answer evidence
/// (raw/display with source pointers), citation sources via <c>AttributionDecoder</c>, citation markers via
/// <c>CitationMarkerParser</c>, and the diagnostic channels (<c>NotRequested</c> under
/// <see cref="DiagnosticProfile.PublicAnswerOnly"/>; <c>NotProvided</c>/<c>Provided</c> via
/// <c>Native118DiagnosticsDecoder</c> under <see cref="DiagnosticProfile.Native118Diagnostics"/>).
/// It must never invent arguments or results, match calls by name, or turn missing telemetry into an empty success.
/// </summary>
internal sealed class WorkIqEvidenceNormalizer
{
    /// <summary>Normalises a completed, failed, suspended or interrupted turn.</summary>
    /// <param name="task">The final task snapshot, or null for a direct-message reply.</param>
    /// <param name="directMessage">The direct reply message when the provider answered without a task.</param>
    /// <param name="rawCaptures">The raw captures that fed the turn.</param>
    /// <param name="captureState">The overall capture state of the turn.</param>
    /// <param name="profile">The diagnostic profile that was requested.</param>
    /// <param name="identity">The turn's identities.</param>
    public Microsoft365TurnEvidence Normalize(
        WorkIqTaskSnapshot? task,
        WorkIqMessage? directMessage,
        ImmutableArray<RawCaptureRef> rawCaptures,
        CaptureState captureState,
        DiagnosticProfile profile,
        TurnIdentity identity) =>
        throw new NotImplementedException("TODO(M22)");
}
