// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

/// <summary>Options controlling how the output store is resolved at startup.</summary>
public sealed class OutputStoreOptions
{
    /// <summary>Determines which <see cref="IOutputStore"/> implementation is activated.</summary>
    public OutputStoreMode OutputStore { get; set; } = OutputStoreMode.Auto;

    /// <summary>Overrides the default output store root path when set.</summary>
    public string? OutputStorePath { get; set; }
}
