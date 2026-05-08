// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>Pairs an eval with its weight and required flag inside a composite.</summary>
public sealed record EvalComponent(IEval Eval, double Weight = 1.0, bool Required = true);
