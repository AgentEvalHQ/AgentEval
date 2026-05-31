// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Tracing;
using Microsoft.Extensions.AI;

namespace AgentEval.MAF.Tracing;

/// <summary>Wraps an <see cref="AIFunction"/> with execution recording. Usage: <c>fn.WithEvaluation(trace)</c>.</summary>
public static class EvaluatingAIFunctionExtensions
{
    /// <summary>Returns an <see cref="EvaluatingAIFunction"/> that records every invocation of <paramref name="function"/> into <paramref name="trace"/>.</summary>
    public static AIFunction WithEvaluation(this AIFunction function, AgentTrace trace)
        => new EvaluatingAIFunction(function, trace);
}
