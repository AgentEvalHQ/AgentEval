// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Results.Writing;

/// <summary>Observed text for a result. The writer attaches it only when its run permits content capture.</summary>
/// <param name="Kind">The kind of observed content.</param>
/// <param name="Text">The exact text to encode as UTF-8, or null to omit this item.</param>
public sealed record AefTextEvidence(AefEvidenceKind Kind, string? Text);
