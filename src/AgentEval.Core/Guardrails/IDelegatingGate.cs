// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Guardrails;

/// <summary>
/// Implemented by a gate that hands its decision, in whole or in part, to other gates it holds: an allow-cache, a
/// judge panel, a wrapper that formats the input for an inner judge, an approval gate built on a judge. A
/// construction-time check (for example the inline-judge calibration check) uses it to see the gates that actually
/// decide, instead of stopping at the wrapper.
/// <para>Internal on purpose. It is an inspection seam for the toolkit's own checks, not an extension point, so the
/// frozen public surface does not change. Implement it explicitly, so the wrapper gains no public member.</para>
/// </summary>
internal interface IDelegatingGate
{
    /// <summary>The gates this gate delegates to, in the order it consults them. Never null.</summary>
    IEnumerable<object> InnerGates { get; }
}
