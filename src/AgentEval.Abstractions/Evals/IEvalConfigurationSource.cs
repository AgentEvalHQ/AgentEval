// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;

namespace AgentEval.Evals;

/// <summary>Declares all configurable settings of a leaf evaluator before execution.</summary>
/// <remarks>Return a fresh content-free JSON object. Hash sensitive reference material; never include responses,
/// credentials or measured outcomes. Producers must also bind implementation identity. Evaluators with nested
/// instruments must expose those instruments rather than omitting their configuration.</remarks>
public interface IEvalConfigurationSource : IEval
{
    /// <summary>Captures the applied configuration without executing the evaluator.</summary>
    JsonObject CaptureConfiguration();
}
