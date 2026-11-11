// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Prompts;

namespace AgentEval.Results.Adapters.Native;

/// <summary>An observed probe-judge call. Unknown model or request settings remain null.</summary>
/// <param name="ModelId">Model identity returned by the provider.</param>
/// <param name="Temperature">Temperature actually dispatched, or null when omitted.</param>
/// <param name="MaxOutputTokens">Output limit actually dispatched, or null when omitted.</param>
/// <param name="ResponseReceived">Whether a response arrived, independently of verdict validity.</param>
public sealed record AefProbeJudgeCall(string? ModelId, float? Temperature, int? MaxOutputTokens, bool ResponseReceived);

/// <summary>An invoked leaf instrument, separate from the identity of a complete declared judge graph.</summary>
/// <param name="Prompt">Resolved prompt identity and explicitly captured content.</param>
/// <param name="RubricDigest">Leaf reader, prompt and observed execution identity.</param>
/// <param name="Calls">Actual attempts, including provider adaptation and corrective retries.</param>
public sealed record AefProbeJudgeInstrument(JudgePromptProvenance Prompt, string RubricDigest, IReadOnlyList<AefProbeJudgeCall> Calls);
