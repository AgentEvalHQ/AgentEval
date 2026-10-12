// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Native;

internal static class AefNativeUsage
{
    // A metered role replaces its legacy ledger/performance role, even when the meter reports unknown counts.
    // Models can be reported aliases of configured deployments, so merging only by model would double count.
    internal static IReadOnlyList<AefUsage>? Merge(IReadOnlyList<AefUsage>? observed, IReadOnlyList<AefUsage>? fallback)
    {
        if (observed is null) return fallback;
        var roles = observed.Select(u => u.Role).ToHashSet();
        var usage = observed.Concat((fallback ?? []).Where(u => !roles.Contains(u.Role))).ToArray();
        return usage.Length == 0 ? null : usage;
    }

    internal static AefCost? Cost(IReadOnlyList<AefUsage>? usage) =>
        usage is { Count: > 0 } && usage.All(u => u.CostUsd is not null)
            ? new AefCost(usage.Sum(u => u.CostUsd!.Value), "sum of recorded usage costs") : null;
}
