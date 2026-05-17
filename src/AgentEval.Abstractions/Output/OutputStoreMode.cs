// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

/// <summary>Determines which <see cref="IOutputStore"/> implementation is activated.</summary>
public enum OutputStoreMode
{
    /// <summary>Automatically selects FileSystem when a workspace root is discoverable, otherwise Null.</summary>
    Auto,
    /// <summary>Always uses the file-system-backed store; fails if no workspace root is found.</summary>
    FileSystem,
    /// <summary>Uses the no-op store that discards all writes.</summary>
    Null
}
