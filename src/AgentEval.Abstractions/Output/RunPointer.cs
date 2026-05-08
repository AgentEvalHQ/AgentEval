// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

/// <summary>Lightweight pointer used in run listings and the runs index.</summary>
public sealed record RunPointer(
    string RunId,
    string SubjectName,
    string Verdict,
    DateTimeOffset Timestamp);
