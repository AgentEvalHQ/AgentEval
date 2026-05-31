// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Tracing;

/// <summary>
/// Capture-fidelity preset for Glass Box recording. Controls per-turn detail and tool-definition
/// de-duplication. Shared by <see cref="TraceRecordingChatClient"/>, the policy gate, and the
/// Trace Fidelity benchmark.
/// </summary>
public enum SamplePreset
{
    /// <summary>Minimal capture; tool-definition de-dup ON; for CI smoke runs.</summary>
    Smoke,

    /// <summary>Standard capture; tool-definition de-dup ON.</summary>
    Standard,

    /// <summary>Full verbatim capture; tool-definition de-dup OFF (auditors want raw payloads every turn).</summary>
    AuditGrade,
}
