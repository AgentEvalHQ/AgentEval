// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentTrace = AgentEval.Tracing.AgentTrace;

namespace AgentEval.Evals;

/// <summary>
/// Glass Box Part 2 (P2.A1): the bridge that lets a Glass Box <see cref="AgentTrace"/> ride along on an
/// <see cref="EvalInput"/> so trace-aware evaluators can read what actually happened at the chat/tool boundary.
/// <para>
/// The trace is carried as an <see cref="EvalInput.Metadata"/> entry under
/// <see cref="EvalInput.TraceMetadataKey"/> (a string key, because the Abstractions assembly that owns
/// <see cref="EvalInput"/> cannot reference the Core <see cref="AgentTrace"/> type). These extensions live in
/// <c>AgentEval.Core</c> — the assembly that owns the trace type — and share <see cref="EvalInput"/>'s
/// namespace so any code already using <see cref="EvalInput"/> gets them without an extra <c>using</c>.
/// </para>
/// <para>
/// NOTE: qualify <c>AgentEval.Tracing.AgentTrace</c> explicitly — a second, unrelated
/// <c>AgentEval.Abstractions.Output.AgentTrace</c> record exists (the output-store artifact shape).
/// </para>
/// </summary>
public static class EvalInputTraceAccessor
{
    /// <summary>
    /// Reads the Glass Box trace previously attached via <see cref="WithTrace"/>, or <c>null</c> when no
    /// trace is present (or a value of a different type was stored under the key).
    /// </summary>
    public static AgentTrace? GetTrace(this EvalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Metadata is not null
            && input.Metadata.TryGetValue(EvalInput.TraceMetadataKey, out var value))
        {
            return value as AgentTrace;
        }

        return null;
    }

    /// <summary>
    /// Returns a copy of <paramref name="input"/> with <paramref name="trace"/> attached under
    /// <see cref="EvalInput.TraceMetadataKey"/>. Copy-on-write: the original <see cref="EvalInput.Metadata"/>
    /// dictionary is never mutated (records are values); an existing trace under the key is replaced.
    /// This is the canonical write site — no framework path constructs an <see cref="EvalInput"/> with a
    /// trace already in scope, so callers (Phase-A evaluator harnesses, samples, traced test runs) attach it here.
    /// </summary>
    public static EvalInput WithTrace(this EvalInput input, AgentTrace trace)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(trace);

        var metadata = input.Metadata is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(input.Metadata);
        metadata[EvalInput.TraceMetadataKey] = trace;

        return input with { Metadata = metadata };
    }
}
