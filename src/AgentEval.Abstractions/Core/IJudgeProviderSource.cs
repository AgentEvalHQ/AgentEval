// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Core;

/// <summary>Optional construction metadata for a judge transport or evaluator.</summary>
/// <remarks>Name the configured provider, never infer it from the requested model or a protocol-compatible SDK.</remarks>
public interface IJudgeProviderSource
{
    /// <summary>The configured provider identity, or null when unknown. Contains no endpoint or credentials.</summary>
    string? ProviderName { get; }
}
