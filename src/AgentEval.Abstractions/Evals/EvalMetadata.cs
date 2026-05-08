// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>Identifies an eval by key, human-readable name, category, and version.</summary>
public sealed record EvalMetadata(string Key, string Name, string Category, string Version);
