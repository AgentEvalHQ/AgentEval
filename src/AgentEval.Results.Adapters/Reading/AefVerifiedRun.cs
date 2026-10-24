// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Results.Ingestion;
using AgentEval.Results.Integrity;
using AgentEval.Results.Runs;

namespace AgentEval.Results.Adapters.Reading;

/// <summary>Observed run identity and integrity, kept separate from importer claims and signature trust.</summary>
public sealed record AefRunReadInfo(string RunId, string SubjectRef, string Integrity, string? RunHash,
    string Status, bool Imported, IReadOnlyList<string> Asserted, string? AbortReason, string ContentCapture)
{
    /// <summary>This reader verifies integrity, without applying a signature trust policy.</summary>
    public string SignatureTrust => "not_checked";
}

// All projections consume the same bounded bytes that were verified, including linked blobs.
internal sealed class AefVerifiedRun : IDisposable
{
    private readonly AefOwnedSnapshot _snapshot;
    private AefVerifiedRun(AefOwnedSnapshot snapshot, AefRunFolder folder, AefRunVerification verification)
    {
        _snapshot = snapshot;
        Folder = folder;
        Documents = AefRunDocuments.Read(folder);
        var header = Documents.Run!;
        Info = new((string)header["runId"]!, (string)header["subject"]!["ref"]!,
            AefRunVerification.Name(verification.Outcome), verification.RunHash?.Value, Documents.Status ?? "unknown",
            header["imported"] is not null,
            (header["imported"]?["asserted"] as JsonArray)?.Select(x => x!.GetValue<string>()).ToArray() ?? [],
            (string?)header["abortReason"], (string)header["contentCapture"]!);
    }

    internal AefRunFolder Folder { get; }
    internal AefRunDocuments Documents { get; }
    internal AefRunReadInfo Info { get; }
    internal long Bytes => _snapshot.Bytes;

    internal static AefVerifiedRun Open(string directory, CancellationToken ct = default, long maxBytes = 1L << 30)
    {
        var snapshot = AefOwnedSnapshot.Capture(Path.GetFullPath(directory), new AefStagingOptions
        {
            MaxFiles = 100_000, MaxBytes = maxBytes, MaxFileBytes = maxBytes,
        }, ct);
        try
        {
            var folder = AefRunFolder.Open(snapshot.FullName);
            var verification = AefRunVerifier.Verify(folder);
            if (verification.Outcome == AefOutcome.Invalid)
                throw new InvalidDataException("Expected: valid AEF evidence. Actual: invalid AEF evidence: " +
                    string.Join(", ", verification.Problems.Select(p => $"{p.Path}: {p.Code}")) +
                    ". Suggestions: verify the original run. Because: invalid evidence cannot establish recorded results.");
            ct.ThrowIfCancellationRequested();
            return new(snapshot, folder, verification);
        }
        catch { snapshot.Dispose(); throw; }
    }

    public void Dispose() => _snapshot.Dispose();
}
