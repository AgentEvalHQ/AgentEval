// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Runs;
using AgentEval.Results.Signatures;

namespace AgentEval.Results.Requirements;

/// <summary>An explicit evidence selection. The expected seal hash is mandatory; no latest-run discovery is performed.</summary>
public sealed record RequirementEvidenceSelection(string Directory, string RunHash);

/// <summary>The exact subject and version for which requirement status is requested.</summary>
public sealed record RequirementSubject(string Ref, string Version);

/// <summary>A verified requirement waiver, with signature assurance and separately configured authority.</summary>
public sealed record RequirementWaiver(string RunHash, string Requirement, string Event, string Identity,
    string Assurance, string Expires, bool Active, bool Authorized);

/// <summary>A detached snapshot of explicitly selected evidence. Only the verifying reader constructs snapshots.</summary>
public sealed class RequirementEvidence
{
    internal RequirementEvidence(string hash, AefRunDocuments documents, EffectiveView view,
        IReadOnlyList<RequirementWaiver> waivers, IReadOnlyList<string> diagnostics, AefTime at, string policyDigest, string overlayDigest)
    {
        RunHash = hash;
        Documents = documents;
        View = view;
        Waivers = waivers;
        Diagnostics = diagnostics;
        At = at;
        PolicyDigest = policyDigest;
        OverlayDigest = overlayDigest;
    }

    internal AefRunDocuments Documents { get; }
    internal EffectiveView View { get; }
    /// <summary>Verified selected run hash.</summary>
    public string RunHash { get; }
    /// <summary>Run identity from the verified header.</summary>
    public string RunId => Documents.RunId!;
    /// <summary>Exact view time used to evaluate waivers.</summary>
    public AefTime At { get; }
    /// <summary>SHA-256 of the normalized trust policy used by the reader.</summary>
    public string PolicyDigest { get; }
    /// <summary>SHA-256 of normalized verified events in file order and their shown assurance; binds the overlay inputs separately from the sealed run.</summary>
    public string OverlayDigest { get; }
    /// <summary>Verified requirement waiver history; result-only waivers are not requirement waivers.</summary>
    public IReadOnlyList<RequirementWaiver> Waivers { get; }
    /// <summary>Overlay problems and unsealed-tail notices. They never silently invalidate sealed measurements.</summary>
    public IReadOnlyList<string> Diagnostics { get; }
}

/// <summary>Reads and verifies one explicitly selected run, without modifying any files.</summary>
public interface IRequirementEvidenceReader
{
    /// <summary>Refuses invalid/unsealed runs or a hash mismatch; snapshots verified overlay effects at the requested time.</summary>
    RequirementEvidence Read(RequirementEvidenceSelection selection, AefTime at, TrustPolicy? policy = null);
}

/// <summary>Invalid evidence selection or status request; distinct from an unmeasured requirement.</summary>
public sealed class RequirementStatusException(string message) : FormatException(message);

/// <summary>AEF-backed evidence verification and snapshot creation for the pure status resolver.</summary>
public sealed class RequirementEvidenceReader : IRequirementEvidenceReader
{
    /// <summary>Exact scoped permission consumed from an existing trust policy's may list.</summary>
    public static string WaivePermission(string requirementId) => "requirements:waive:" + requirementId;

    /// <summary>Digest of normalized policy semantics, independent of file whitespace. An absent policy trusts nobody.</summary>
    public static string PolicyDigest(TrustPolicy? policy) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            (policy ?? TrustPolicy.Empty).ToJson().ToJsonString()))).ToLowerInvariant();

    /// <inheritdoc />
    public RequirementEvidence Read(RequirementEvidenceSelection selection, AefTime at, TrustPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!at.IsValid || string.IsNullOrWhiteSpace(selection.Directory) || selection.RunHash is not { Length: 64 }
            || selection.RunHash.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw Error("an explicit directory, lowercase SHA-256 run hash and valid UTC view time", "invalid selection",
                "Provide a sealed run's exact hash and an RFC 3339 UTC time.");

        var folder = AefRunFolder.Open(selection.Directory);
        var documents = AefRunDocuments.Read(folder);
        var verification = AefRunVerifier.Verify(folder, new AefVerifyOptions { Policy = policy });
        if (verification.Outcome != AefOutcome.Intact || verification.RunHash?.Value != selection.RunHash)
            throw Error("intact evidence matching the selected run hash",
                $"{AefRunVerification.Name(verification.Outcome)}; hash {verification.RunHash?.Value}; " +
                string.Join(", ", verification.Problems.Select(p => $"{p.Path}: {p.Code}")),
                "Verify the run and select its exact sealed hash; never substitute another run.");

        var chain = OverlayChain.Verify(folder, documents);
        var view = EffectiveView.Compute(folder, documents, chain, at, policy);
        var events = chain.VerifiedEvents.ToDictionary(e => AefNode.String(e.Event!["eventId"])!, e => e.Event!);
        var assurances = view.Assurance.ToDictionary(a => a.Event, a => a.Shown);
        var waivers = new List<RequirementWaiver>();
        foreach (var waiver in view.Waivers)
        {
            if (AefNode.String(waiver.Target["requirement"]) is not { } requirement) continue;
            var identity = AefNode.String(events[waiver.Event]["by"]?["identity"])!;
            var assurance = assurances[waiver.Event];
            waivers.Add(new(selection.RunHash, requirement, waiver.Event, identity, assurance, waiver.Expires!,
                waiver.Active, assurance == "signed" && policy?.Allows(identity, WaivePermission(requirement)) == true));
        }
        var diagnostics = chain.Problems.Select(p => $"overlay {p.Path}: {p.Code}").ToList();
        if (view.UnsealedEvents > 0) diagnostics.Add($"Ignored {view.UnsealedEvents} unsealed overlay events.");
        using var overlayHash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var line in chain.VerifiedEvents)
            overlayHash.AppendData(System.Text.Encoding.UTF8.GetBytes(line.Event!.ToJsonString() + "\n"));
        overlayHash.AppendData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(view.Assurance));
        var overlayDigest = Convert.ToHexString(overlayHash.GetHashAndReset()).ToLowerInvariant();
        return new(selection.RunHash, documents, view, waivers.AsReadOnly(), diagnostics.AsReadOnly(), at, PolicyDigest(policy), overlayDigest);
    }

    internal static RequirementStatusException Error(string expected, string actual, string suggestion) =>
        new($"Expected: {expected}. Actual: {actual}. Suggestions: {suggestion} Because: requirement status must be reproducible from explicitly verified evidence.");
}
