// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Prompts;

namespace AgentEval.Guardrails.Judges;

/// <summary>One actual configured gate dispatch. An absent reported model remains unknown.</summary>
/// <param name="ModelId">Provider-reported model, not inferred from the requested ID.</param>
/// <param name="ResponseReceived">Whether the provider returned a reply, independently of parsing success.</param>
public sealed record JudgeGateCall(string? ModelId, bool ResponseReceived);

/// <summary>Selected gate instrument and its observed execution.</summary>
/// <param name="ConfigurationDigest">Content-free identity of the selected prompt and execution/enforcement policy.</param>
/// <param name="Prompt">Resolved locale and prompt; content is included only by explicit capture.</param>
/// <param name="RequestedModelId">Explicit requested model ID, or null when model selection was left to the client.</param>
/// <param name="Calls">Actual dispatched requests, including failed attempts and parse retries.</param>
/// <param name="Measured">Whether a valid conclusive judge measurement was obtained.</param>
/// <param name="BudgetExhausted">Whether a required model reservation was denied.</param>
public sealed record JudgeGateInstrument(string ConfigurationDigest, JudgePromptProvenance Prompt, string? RequestedModelId,
    IReadOnlyList<JudgeGateCall> Calls, bool Measured, bool BudgetExhausted = false);
