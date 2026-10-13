// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.Results.Ingestion;

/// <summary>Verifies a completed upload and atomically publishes its original evidence bytes.</summary>
public interface IAefRunFinalizer
{
    /// <summary>
    /// Finalizes a server-generated stage ID. Cancellation is observed during preflight and between verification
    /// phases, including immediately before publication. The protocol verifier itself is synchronous.
    /// </summary>
    AefAcceptedRun FinalizeStage(string stageId, CancellationToken cancellationToken = default);
}

/// <summary>Published intact evidence; this does not assert signer trust, a passing verdict, or benchmark completeness.</summary>
/// <param name="StageId">Original upload identity.</param>
/// <param name="RunHash">Verified sealed run hash, 64 lower-case hexadecimal characters.</param>
/// <param name="Directory">Accepted local path; do not expose over HTTP.</param>
/// <param name="ReceiptPersisted">Whether accepted state was persisted after publication. False requires receipt recovery.</param>
public sealed record AefAcceptedRun(string StageId, string RunHash, string Directory, bool ReceiptPersisted);

/// <summary>A bounded, content-free refusal to finalize an upload.</summary>
public sealed class AefFinalizationException : IOException
{
    /// <summary>Creates a refusal with a stable code (state, invalid, unsealed, limit, conflict, capacity, busy, or path).</summary>
    public AefFinalizationException(string code, string actual) : base(
        $"Expected: a complete bounded stage containing intact sealed evidence. Actual: {actual}. " +
        "Suggestions: correct or recover the stage before retrying. Because: only verified evidence may be published.") => Code = code;

    /// <summary>Stable refusal code, without paths or uploaded text.</summary>
    public string Code { get; }
}
