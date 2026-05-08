// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Evals;

/// <summary>A single piece of supporting evidence attached to an eval result.</summary>
public sealed record EvalEvidence(string Source, string Reference, string Message);
