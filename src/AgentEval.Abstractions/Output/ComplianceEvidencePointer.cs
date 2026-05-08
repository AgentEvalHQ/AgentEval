// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

/// <summary>Lightweight listing entry for a stored compliance evidence document.</summary>
public sealed record ComplianceEvidencePointer(
    string Regulation,
    string SubjectName,
    string Timestamp,
    string OverallStatus);
