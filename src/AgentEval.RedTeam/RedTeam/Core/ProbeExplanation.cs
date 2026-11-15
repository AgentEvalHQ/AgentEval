// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.RedTeam;

/// <summary>Best-effort narration of an existing verdict, separate from the instrument that established it.</summary>
/// <param name="Text">Validated narrative, or null when no usable explanation was generated.</param>
/// <param name="Instrument">Selected narrative instrument and actual calls, including unsuccessful attempts.</param>
/// <param name="FailureReason">Content-free failure diagnostic, or null on successful generation.</param>
public sealed record ProbeExplanation(string? Text, ProbeJudgeInstrument Instrument, string? FailureReason = null);
