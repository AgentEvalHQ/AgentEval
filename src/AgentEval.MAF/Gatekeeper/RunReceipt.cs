// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// A per-run summary exported at run end (Phase 3, P3-11) — a snapshot of what the <see cref="RunLedger"/>
/// accumulated for one run, tied to the run's identity and the gate configuration that governed it. Emitted as a
/// <c>gate.receipt.*</c> evidence record so it rides the same F-C stream (trace + any sink) as every other finding.
/// </summary>
/// <param name="RunId">The run's identity (<see cref="AgentRunScope.RunId"/>), if a scope was established.</param>
/// <param name="AgentName">The invoking agent's name, if known.</param>
/// <param name="ConfigFingerprint">The gate configuration in force (P3-6).</param>
/// <param name="EndedAtUtc">When the run finished.</param>
/// <param name="TotalToolCalls">Total tool calls the ledger recorded this run.</param>
/// <param name="ToolCallCounts">Per-tool call counts (a snapshot copy).</param>
public sealed record RunReceipt(
    string? RunId,
    string? AgentName,
    string? ConfigFingerprint,
    DateTimeOffset EndedAtUtc,
    int TotalToolCalls,
    IReadOnlyDictionary<string, int> ToolCallCounts);
