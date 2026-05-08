// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>Unified abstraction for atomic and composite evals.</summary>
public interface IEval
{
    /// <summary>Machine-readable identifier, e.g. "task_completion".</summary>
    string Key { get; }

    /// <summary>Human-readable name.</summary>
    string Name { get; }

    /// <summary>Eval category, e.g. "system-outcome" or "compliance.gdpr".</summary>
    string Category { get; }

    /// <summary>Semver-style version string, e.g. "1.0.0".</summary>
    string Version { get; }

    /// <summary>Runs the eval against <paramref name="input"/> and returns a result.</summary>
    Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default);
}
