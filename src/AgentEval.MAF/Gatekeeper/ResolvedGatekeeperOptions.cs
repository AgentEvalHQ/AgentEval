// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Immutable, construction-time snapshot of Phase-3 defaults. Runtime gates receive values from this snapshot
/// instead of retaining mutable caller-owned configuration collections.
/// </summary>
internal sealed record ResolvedGatekeeperOptions(
    int ContainmentRetryThreshold,
    GatekeeperRefusalStyle RefusalStyle,
    IReadOnlyList<string> CamouflagedRefusalMessages,
    IContainmentStore? ContainmentStore);
