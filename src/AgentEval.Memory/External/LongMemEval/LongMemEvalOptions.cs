// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.External.Models;

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// LongMemEval-specific benchmark options.
/// Currently inherits all configuration from <see cref="ExternalBenchmarkOptions"/>.
/// Exists as a typed extension point for future LongMemEval-specific settings.
/// </summary>
public class LongMemEvalOptions : ExternalBenchmarkOptions
{
}

