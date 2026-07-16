// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Gatekeeper (Phase 1, #13) — how much of a <see cref="ToolGateAction.Mutate"/> verdict's before/after tool
/// arguments are captured into the trace as <c>gate.tool.*</c> evidence. A prior version always recorded
/// arguments VERBATIM — the code's own comment admitted it: <c>"NOTE: args are serialized verbatim — do not
/// put secrets in tool args."</c> That is a real gap: an argument carrying a secret (an API key, a customer
/// SSN, a session token) a gate rewrites would land in plaintext in the trace, which may be logged, shipped to
/// a SIEM, or otherwise persisted beyond the process. <see cref="Redacted"/> is now the default.
/// </summary>
public enum TraceCaptureMode
{
    /// <summary>No argument evidence is captured at all — only that a mutation happened, with the policy name/reason. Most private, least auditable.</summary>
    None,

    /// <summary>Only argument NAMES and CLR types are captured (e.g. <c>{"path":"String"}</c>) — never values. Shows what shape changed, not what changed.</summary>
    SchemaOnly,

    /// <summary>
    /// Argument names are captured; every non-null value is replaced by a fixed redaction marker
    /// (<c>"***"</c>) — shows which arguments existed/changed without ever writing a value into the trace.
    /// The default: safe for arguments that may carry secrets, while still showing the mutation's shape
    /// (which keys were touched).
    /// </summary>
    Redacted,

    /// <summary>
    /// Every non-null value is replaced by a stable SHA-256 hash of its serialized form, truncated to 32 hex
    /// chars (128 bits) — lets an auditor confirm value equality/reuse across calls (e.g. "the same token was
    /// reused," "this matches a known-bad value") without ever storing the plaintext. 128 bits of a real
    /// cryptographic digest is not a weak/toy comparison marker — collision search against it is infeasible.
    /// </summary>
    Hashed,

    /// <summary>
    /// The full, verbatim before/after argument values are recorded — the previous, only behavior. Opt-in;
    /// do not use when arguments may carry secrets (the trace may be logged/persisted beyond this process).
    /// </summary>
    Full,
}
