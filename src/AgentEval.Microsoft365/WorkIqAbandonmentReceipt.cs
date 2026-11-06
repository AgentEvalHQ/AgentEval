// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Microsoft365.Evidence;

namespace AgentEval.Microsoft365;

/// <summary>
/// What <see cref="IWorkIqConversation.AbandonAndResetAsync"/> did: it stopped local waiting, recorded the last
/// known outcome and cleared the local context. It never sends <c>CancelTask</c>, so
/// <see cref="RemoteCancellationConfirmed"/> is <see langword="false"/> unless a prior cancellation was separately
/// confirmed by the service. Abandonment does not undo remote work.
/// </summary>
/// <param name="Reason">The caller's reason (user content; subject to the content policy like any report field).</param>
/// <param name="AbandonedAt">Local timestamp.</param>
/// <param name="Quiesced">Whether the local operation stopped within the recovery budget; when <see langword="false"/> no new turn was started and the context was not reused.</param>
/// <param name="PriorContextId">The context id that was dropped, when known.</param>
/// <param name="PriorTask">The last task handle, when known (so the host can still <c>GetTask</c>/<c>CancelTask</c> it).</param>
/// <param name="LastOutcome">The last observed turn outcome, when a turn had run.</param>
/// <param name="LastCapture">The last raw capture reference, when retained.</param>
/// <param name="RemoteCancellationConfirmed"><see langword="true"/> only when the service had already confirmed <c>TASK_STATE_CANCELED</c> for <paramref name="PriorTask"/>.</param>
public sealed record WorkIqAbandonmentReceipt(
    string Reason,
    DateTimeOffset AbandonedAt,
    bool Quiesced,
    string? PriorContextId,
    WorkIqTaskHandle? PriorTask,
    WorkIqTurnOutcome? LastOutcome,
    RawCaptureRef? LastCapture,
    bool RemoteCancellationConfirmed);
