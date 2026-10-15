// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Results.Integrity;
using AgentEval.Results.Json;
using AgentEval.Results.Runs;

namespace AgentEval.Results.Suites;

/// <summary>A detached identity snapshot from an intact sealed run; only the verifying reader constructs it.</summary>
public sealed class SuiteIdentity
{
    internal SuiteIdentity(string runHash, string runId, string status, string scope, string? reference,
        string? version, string? contentDigest, SuiteDefinitionManifest? definition)
    {
        RunHash = runHash; RunId = runId; RunStatus = status; Scope = scope;
        SuiteRef = reference; SuiteVersion = version; ContentDigest = contentDigest; Definition = definition;
    }

    /// <summary>Exact verified seal hash, without an algorithm prefix.</summary>
    public string RunHash { get; }
    /// <summary>The sealed run identifier.</summary>
    public string RunId { get; }
    /// <summary>Completed or aborted; identity compatibility does not establish result completeness.</summary>
    public string RunStatus { get; }
    /// <summary>definition, recipe, dataset, observed-roster, legacy-roster or unknown.</summary>
    public string Scope { get; }
    /// <summary>The suite reference, when declared.</summary>
    public string? SuiteRef { get; }
    /// <summary>The suite version, when declared.</summary>
    public string? SuiteVersion { get; }
    /// <summary>The header's suite digest; legacy scopes do not establish full-definition identity.</summary>
    public string? ContentDigest { get; }
    /// <summary>Verified immutable manifest, absent for legacy or unknown identity.</summary>
    public SuiteDefinitionManifest? Definition { get; }

    /// <summary>Content-free identity and verification provenance suitable for reports.</summary>
    public JsonObject ToJson() => new()
    {
        ["runHash"] = RunHash, ["runId"] = RunId, ["runStatus"] = RunStatus, ["scope"] = Scope,
        ["suiteRef"] = SuiteRef, ["suiteVersion"] = SuiteVersion, ["suiteDigest"] = ContentDigest,
        ["definitionDigest"] = Definition?.Digest, ["frozen"] = Definition?.Frozen,
    };
}

/// <summary>Verifies sealed evidence and reads its declared suite identity.</summary>
public interface ISuiteIdentityReader
{
    /// <summary>Reads one directory, optionally requiring an exact lowercase seal hash; refuses invalid or unsealed evidence.</summary>
    SuiteIdentity Read(string directory, string? expectedRunHash = null);
}

/// <summary>Reads bounded, sealed definition bytes and validates their binding to the run header.</summary>
public sealed class SuiteIdentityReader : ISuiteIdentityReader
{
    /// <inheritdoc />
    public SuiteIdentity Read(string directory, string? expectedRunHash = null)
    {
        try { return ReadCore(directory, expectedRunHash); }
        catch (AefReadException error) { throw Bad(error.Message); }
    }

    private static SuiteIdentity ReadCore(string directory, string? expectedRunHash)
    {
        if (expectedRunHash is not null && (expectedRunHash.Length != 64 || expectedRunHash.Any(c => !char.IsAsciiHexDigitLower(c))))
            throw Bad("expected run hash is not lowercase SHA-256");
        var folder = AefRunFolder.Open(directory);
        if (folder.PathProblems.Count != 0) throw Bad("run has invalid paths or exceeds the file limit");

        // Read identity bytes BEFORE verification so the folder's cached digests bind precisely these snapshots
        // to the seal. Never follow a path supplied by untrusted extension metadata or reread after verification.
        var header = AefJsonReader.ParseDocument(folder.Read("run.json", AefLimits.MaxBytesOf("run.json")));
        const string path = "ext/" + SuiteDefinitionManifest.ExtensionFile;
        var manifestBytes = folder.Has(path) ? folder.Read(path, SuiteDefinitionReader.MaxBytes) : null;
        var verification = AefRunVerifier.Verify(folder);
        if (verification.Outcome != AefOutcome.Intact || verification.RunHash is null)
            throw Bad("run is " + AefRunVerification.Name(verification.Outcome) + "; " +
                string.Join(", ", verification.Problems.Select(p => $"{p.Path}: {p.Code}")));
        var runHash = verification.RunHash.Value.Value;
        if (expectedRunHash is not null && expectedRunHash != runHash)
            throw Bad("verified run hash differs from the selected hash");

        var suite = header["suite"] as JsonObject;
        var identity = (header["ext"] as JsonObject)?["agenteval.suiteIdentity"];
        if (identity is not null && identity is not JsonObject) throw Bad("suite identity metadata is not an object");
        var scope = AefNode.String(identity?["scope"]);
        SuiteDefinitionManifest? definition = null;
        if (scope == "definition")
        {
            if (manifestBytes is null) throw Bad("declared definition file is missing");
            definition = new SuiteDefinitionReader().Read(manifestBytes);
            foreach (var field in definition.Metadata())
                if (!JsonNode.DeepEquals(identity?[field.Key], field.Value))
                    throw Bad("suite identity metadata differs at " + field.Key);
            if (suite is null || AefNode.String(suite["ref"]) != definition.SuiteRef
                || AefNode.String(suite["version"]) != definition.DefinitionVersion
                || !JsonNode.DeepEquals(suite["frozen"], JsonValue.Create(definition.Frozen))
                || !JsonNode.DeepEquals(suite["digest"], JsonValue.Create(definition.SuiteContentDigest)))
                throw Bad("suite header differs from its declared definition");
            if (definition.Frozen && AefNode.String(header["execution"]?["stimulus"]) == "generated")
                throw Bad("generated stimuli claim a frozen definition");
            scope = definition.Frozen ? "definition" : "recipe";
        }
        else
        {
            if (manifestBytes is not null) throw Bad("definition file has no matching definition metadata");
            if (scope is not ("dataset" or "observed-roster" or "legacy-roster")) scope = "unknown";
        }
        return new(runHash, AefNode.String(header["runId"])!, AefNode.String(header["status"])!,
            scope, AefNode.String(suite?["ref"]), AefNode.String(suite?["version"]), AefNode.String(suite?["digest"]), definition);
    }

    private static FormatException Bad(string actual) => new(
        $"Expected: intact sealed evidence with consistent suite identity. Actual: {actual}. Suggestions: verify the selected run and regenerate evidence with a matching declared manifest. Because: comparisons must use the identity bound to the sealed evidence.");
}
