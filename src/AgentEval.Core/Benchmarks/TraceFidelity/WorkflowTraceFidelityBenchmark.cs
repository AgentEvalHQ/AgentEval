// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Tracing;

namespace AgentEval.Benchmarks;

/// <summary>
/// Factory for the Workflow Trace Fidelity benchmark family (Glass Box, Phase 3) — the workflow analogue of
/// <see cref="TraceFidelityBenchmark"/>: "does each executor's framework-reported ledger (tokens + finish
/// reason) match chat-boundary truth?". Lives in the canonical <c>AgentEval.Benchmarks</c> namespace;
/// registered as a Shape-B family (custom runner). Presets map to <see cref="SamplePreset"/> (informational).
/// </summary>
public static partial class WorkflowTraceFidelityBenchmark
{
    /// <summary>Smoke preset.</summary>
    public static WorkflowTraceFidelityReconciler Smoke() => new(SamplePreset.Smoke);

    /// <summary>Standard preset.</summary>
    public static WorkflowTraceFidelityReconciler Standard() => new(SamplePreset.Standard);

    /// <summary>Audit-grade preset.</summary>
    public static WorkflowTraceFidelityReconciler AuditGrade() => new(SamplePreset.AuditGrade);
}
