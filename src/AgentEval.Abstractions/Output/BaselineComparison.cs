// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

/// <summary>Result of comparing a run's summary against its subject's saved baseline.</summary>
public sealed record BaselineComparison(
    string SubjectName,
    RunSummary? Baseline,
    RunSummary Current,
    IReadOnlyDictionary<string, double> Deltas,
    bool Regressed);
