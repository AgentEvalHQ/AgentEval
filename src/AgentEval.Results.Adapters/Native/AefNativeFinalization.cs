// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Results.Integrity;
using AgentEval.Results.Signatures;
using AgentEval.Results.Writing;

namespace AgentEval.Results.Adapters.Native;

/// <summary>Validates closed native evidence and optionally seals it.</summary>
internal static class AefNativeFinalization
{
    internal static void Validate(bool seal, IAefSigner? signer)
    {
        if (!seal && signer is not null)
            throw new AefInputException("A native run cannot request signing while sealing is disabled.");
    }

    internal static string? Complete(string directory, bool seal, IAefSigner? signer)
    {
        if (seal)
            return AefSealer.Seal(directory, new AefSealOptions { SealedBy = AefSealedBy.Producer, Signer = signer }).RunHash;
        var verification = AefRunVerifier.Verify(directory);
        if (verification.Outcome != AefOutcome.Unsealed || verification.Problems.Count > 0)
            throw new AefWriteException("The closed native run could not be verified before handoff", verification.Problems);
        return null;
    }
}
