// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>Abstract base for all atomic (single-metric) evals.</summary>
public abstract class AtomicEval : IEval
{
    /// <summary>Initialises a new instance of <see cref="AtomicEval"/>.</summary>
    protected AtomicEval(string key, string name, string category, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        Key = key;
        Name = name;
        Category = category;
        Version = version;
    }

    /// <inheritdoc/>
    public string Key { get; }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public string Category { get; }

    /// <inheritdoc/>
    public string Version { get; }

    /// <inheritdoc/>
    public abstract Task<EvalResult> EvaluateAsync(EvalInput input, CancellationToken ct = default);
}
