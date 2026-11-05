// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Microsoft365.Evidence;

/// <summary>
/// The constants that identify the Microsoft 365 evidence document: where it travels on an
/// <c>AgentResponse</c>, which schema it follows, and which normaliser/profile versions produced it.
/// Bump a version constant whenever the corresponding behaviour changes — same capture + same versions
/// must always yield the same document.
/// </summary>
public static class Microsoft365Evidence
{
    /// <summary>The <c>AgentResponse.AdditionalProperties</c> key under which the <see cref="Microsoft365TurnEvidence"/> travels.</summary>
    public const string Key = "AgentEval.Microsoft365.Evidence";

    /// <summary>The evidence document schema id (an AgentEval schema, not a Microsoft wire contract).</summary>
    public const string SchemaVersion = "agenteval.microsoft365.turn-evidence/v1";

    /// <summary>The version of <c>WorkIqEvidenceNormalizer</c> and its decoders.</summary>
    public const string NormalizerVersion = "1.0.0";

    /// <summary>
    /// The provider profile the decoders target: Work IQ A2A 1.0 transport plus the diagnostic artifact shapes
    /// observed in the native evaluator 1.18.0 (experimental; not a documented Microsoft contract).
    /// </summary>
    public const string ProviderProfileVersion = "workiq-a2a-1.0/native118-shapes-v1";

    /// <summary>The only A2A protocol version this provider speaks; sent as <c>A2A-Version</c> on every request.</summary>
    public const string A2AVersion = "1.0";
}
