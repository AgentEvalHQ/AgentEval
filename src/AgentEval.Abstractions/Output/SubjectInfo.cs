// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

/// <summary>Combines a subject's identity with an optional summary of its most recent run.</summary>
public sealed record SubjectInfo(SubjectIdentity Identity, RunSummary? LastRun = null);
