// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Output;

/// <summary>Manifest for a red-team campaign stored in the output folder.</summary>
public sealed record RedTeamCampaignManifest(
    string SchemaVersion,
    string CampaignId,
    string Name,
    DateTimeOffset StartedAt,
    IReadOnlyList<SubjectIdentity> Targets,
    string Mode,
    string? CompletedAt = null);
