// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Tracing;

namespace AgentEval.Benchmarks;

/// <summary>
/// Factory for the Trace Fidelity benchmark family (Glass Box) — "does the framework's reported trace match
/// what the model actually saw?". Lives in the canonical <c>AgentEval.Benchmarks</c> namespace; registered as
/// a Shape-B family (custom runner consuming two traces). Presets map to <see cref="SamplePreset"/>.
/// </summary>
public static partial class TraceFidelityBenchmark
{
    /// <summary>Smoke preset — core discrepancy classes; de-dup on.</summary>
    public static TraceFidelityRunner Smoke() => new(SamplePreset.Smoke);

    /// <summary>Standard preset — all six discrepancy classes; de-dup on.</summary>
    public static TraceFidelityRunner Standard() => new(SamplePreset.Standard);

    /// <summary>Audit-grade preset — all six classes; verbatim payloads (de-dup off).</summary>
    public static TraceFidelityRunner AuditGrade() => new(SamplePreset.AuditGrade);
}
