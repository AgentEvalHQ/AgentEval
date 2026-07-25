// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Guardrails;

/// <summary>
/// The single source of truth for the ReDoS match-timeouts every gate's compiled <see cref="System.Text.RegularExpressions.Regex"/>
/// runs under (Phase 6, P6-3 — "centralized tuning"). Before this, the same three values (50 / 100 / 300 ms) were
/// scattered as inline <c>TimeSpan.FromMilliseconds(…)</c> literals across a dozen-plus gates, so tuning the ReDoS
/// budget — or auditing that every gate even has one — meant hunting each call site. Tiered by how much text a gate's
/// pattern scans, deliberately kept BELOW the <see cref="GateCost"/> ceilings a cost watchdog enforces (a
/// <see cref="GateCost.Bounded"/> gate's 500 ms ceiling sits above <see cref="Extended"/> with headroom).
/// </summary>
public static class GateRegexTimeouts
{
    /// <summary>
    /// 50 ms — a trivial, tightly-bounded pattern over a small slice (e.g. a <c>"{3,}</c> triple-quote prefilter).
    /// </summary>
    public static readonly TimeSpan Trivial = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// 100 ms — the default for a standard gate scan (PII, taint tokens, domain extraction, argument patterns):
    /// linear/anchored patterns over a single argument or message.
    /// </summary>
    public static readonly TimeSpan Standard = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 300 ms — an extended budget for a gate that scans a larger surface with several patterns (e.g. the secret
    /// gate sweeping a whole tool result against many credential shapes). The upper bound; still under a
    /// <see cref="GateCost.Bounded"/> gate's 500 ms cost ceiling.
    /// </summary>
    public static readonly TimeSpan Extended = TimeSpan.FromMilliseconds(300);
}
