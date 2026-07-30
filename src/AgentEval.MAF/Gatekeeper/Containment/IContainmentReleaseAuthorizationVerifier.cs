// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.MAF.Gatekeeper;

/// <summary>
/// Validates signed operator authority using caller-owned keys. Implementations should be bounded,
/// side-effect-free, and return false rather than throw for invalid signatures.
/// </summary>
public interface IContainmentReleaseAuthorizationVerifier
{
    /// <summary>
    /// Verifies <paramref name="authorization"/> against the exact domain-separated canonical payload.
    /// The payload excludes the signature itself.
    /// </summary>
    bool Verify(
        ContainmentReleaseAuthorization authorization,
        ReadOnlyMemory<byte> canonicalPayload);
}
