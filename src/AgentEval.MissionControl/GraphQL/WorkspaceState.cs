// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Output;

namespace AgentEval.MissionControl.GraphQL;

/// <summary>
/// Workspace bootstrap state surfaced to the SPA on every page load. Drives
/// the MC1.10.1 first-run landing page: when <see cref="Initialized"/> is
/// <c>false</c> the SPA renders a "welcome — run <c>agenteval init</c>"
/// page instead of an empty dashboard with broken queries.
/// </summary>
/// <param name="Initialized">
/// <c>true</c> when the resolved workspace contains a usable
/// <c>.agenteval/solution.json</c>. <c>false</c> when the folder is missing
/// OR present-but-uninitialised (e.g. only contains a stray file).
/// </param>
/// <param name="Root">
/// Absolute path of the resolved workspace root, or <c>null</c> when the
/// store can't determine one. Helps the SPA show the user which directory
/// Mission Control is actually watching.
/// </param>
/// <param name="Solution">
/// The parsed <see cref="SolutionInfo"/> if available; <c>null</c> when
/// uninitialised.
/// </param>
/// <param name="AgentEvalVersion">
/// AgentEval assembly version — visible in the landing page footer for
/// support / version-skew diagnostics.
/// </param>
public sealed record WorkspaceState(
    bool Initialized,
    string? Root,
    SolutionInfo? Solution,
    string AgentEvalVersion);
