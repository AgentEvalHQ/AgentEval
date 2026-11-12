// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Prompts;

/// <summary>File boundary for versioned JSON/YAML metadata and Markdown message files.</summary>
public interface IJudgePromptPackLoader
{
    /// <summary>Loads and validates one bounded pack without evaluating or contacting a model.</summary>
    Task<JudgePromptPack> LoadAsync(string manifestPath, CancellationToken cancellationToken = default);

    /// <summary>Exports an immutable pack to a new directory, without overwriting existing files.</summary>
    Task ExportAsync(JudgePromptPack pack, string directory, CancellationToken cancellationToken = default);
}
