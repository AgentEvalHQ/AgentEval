// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Results.Runner;

/// <summary>The result of resolving a plan without executing cases or writing evidence.</summary>
/// <param name="Accepted">Whether the runner can take this plan.</param>
/// <param name="Reason">The content-free refusal reason, or null when accepted.</param>
/// <param name="Estimate">The accepted plan's cost estimate, or null when refused.</param>
public sealed record AefJobAnalysis(bool Accepted, string? Reason, AefJobEstimate? Estimate);

/// <summary>Cost bounds for the cases counted by a plan, computed exactly and rounded once.</summary>
/// <param name="Cases">The number of cases counted, respecting the plan's case limit.</param>
/// <param name="UsdLow">The sum of known lower cost estimates.</param>
/// <param name="UsdHigh">The sum of upper cost bounds.</param>
/// <param name="PriceTable">The table used for the estimates.</param>
public sealed record AefJobEstimate(long Cases, double UsdLow, double UsdHigh, string PriceTable);
