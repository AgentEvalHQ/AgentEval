// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentEval.MAF.AgentHooks.Aevp;

/// <summary>
/// AEVP — the Agent Evidence Profile. The document that an AGENT-HOOKS-0.1
/// <c>verdict.evidence.artefact</c> content address resolves to.
/// </summary>
/// <remarks>
/// <para><b>The slot this fills.</b> AGENT-HOOKS-0.1 §5.3 defines <c>evidence</c> as "an opaque pointer to an
/// offline-verifiable artefact supporting the verdict" and requires a host to "propagate <c>evidence</c> to its
/// audit sink unchanged". The schema gives that pointer exactly two members — <c>artefact</c> (a
/// <c>sha256:&lt;hex&gt;</c> content address or URI) and <c>verification_pointers</c>. **It never defines what
/// the artefact contains.** AGT-EVIDENCE-1.0 covers the proof/verification plumbing but has no notion of
/// confidence, calibration, coverage, evidence tier or abstention. That semantic slot is unclaimed, and the
/// contract explicitly delegates it: "This specification does NOT define how an interceptor computes a
/// verdict… defined by interceptor specifications."</para>
///
/// <para><b>Five fields, capped deliberately.</b> Each answers a question an auditor must ask and cannot ask
/// today: did you check? how well? could you even have stopped it? who decided? are they any good? Anything
/// beyond these is either derivable or product-specific and belongs in AgentEval, not in a profile intended to
/// be adoptable. If this grows past ~8 fields, stop.</para>
///
/// <para>🔑 <b><see cref="Evaluated"/> is abstention smuggled in legally.</b> The spec's decision enum is
/// closed (<c>allow | deny | transform</c>) and <c>Verdict</c> is <c>additionalProperties: false</c> with no
/// extension slot, so an interceptor that could not evaluate must return <c>allow</c> — it must lie. With this
/// profile the verdict still says <c>allow</c>, but the artefact says <b>nobody actually checked</b>, letting an
/// auditor distinguish <em>permitted</em> from <em>unexamined</em> with no change to agent-hooks at all.</para>
/// </remarks>
public sealed record AgentEvidenceProfile
{
    /// <summary>The profile version this document conforms to.</summary>
    public const string SpecVersion = "aevp/0.1";

    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        // Deterministic output: the artefact is content-addressed, so serialization must be stable.
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Always <see cref="SpecVersion"/>. Present so a consumer can reject an unknown profile loudly.</summary>
    [JsonPropertyName("aevp")]
    public string Aevp { get; init; } = SpecVersion;

    /// <summary>
    /// Whether the interceptor actually evaluated this call, or returned a default. <see langword="false"/>
    /// means the permit carries NO evidence of safety — the coverage-gap signal the verdict enum cannot express.
    /// </summary>
    [JsonPropertyName("evaluated")]
    public required bool Evaluated { get; init; }

    /// <summary>What kind of evidence backs the verdict. Null when <see cref="Evaluated"/> is false.</summary>
    [JsonPropertyName("evidence_tier")]
    public EvidenceTier? EvidenceTier { get; init; }

    /// <summary>
    /// What this interception point could physically have done — NOT what it chose to do. An
    /// <see cref="AevpEnforcementCapability.Observe"/> seam cannot block, so a permit from it means far less
    /// than a permit from a blocking seam.
    /// </summary>
    [JsonPropertyName("enforcement_capability")]
    public required AevpEnforcementCapability EnforcementCapability { get; init; }

    /// <summary>Who decided. Null for a purely deterministic decision with no judge involved.</summary>
    [JsonPropertyName("judge")]
    public JudgeIdentity? Judge { get; init; }

    /// <summary>
    /// Whether that judge is fit to decide. Null means UNCALIBRATED — which a consumer must treat as a weaker
    /// claim, not as a neutral one.
    /// </summary>
    [JsonPropertyName("calibration")]
    public CalibrationEvidence? Calibration { get; init; }

    /// <summary>Serializes to canonical JSON — the exact bytes the content address is computed over.</summary>
    public string ToCanonicalJson() => JsonSerializer.Serialize(this, CanonicalOptions);

    /// <summary>
    /// The <c>sha256:&lt;hex&gt;</c> content address for this profile, in the form
    /// <c>verdict.evidence.artefact</c> expects.
    /// </summary>
    public string ToContentAddress()
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(ToCanonicalJson()));
        // Convert.ToHexStringLower is .NET 9+; this project targets net8.0 (the SDK ships lib/net8.0 only).
        return "sha256:" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Parses a profile document, or throws if it is not valid JSON.</summary>
    public static AgentEvidenceProfile FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        return JsonSerializer.Deserialize<AgentEvidenceProfile>(json, CanonicalOptions)
               ?? throw new JsonException("AEVP document deserialized to null.");
    }

    /// <summary>The profile as a <see cref="JsonObject"/>, for schema validation or embedding.</summary>
    public JsonObject ToJsonObject() =>
        JsonNode.Parse(ToCanonicalJson()) as JsonObject
        ?? throw new JsonException("AEVP document did not serialize to a JSON object.");
}

/// <summary>How strong the evidence behind a verdict is. Mirrors AgentEval's <c>EvidenceFidelity</c>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<EvidenceTier>))]
public enum EvidenceTier
{
    /// <summary>The subject only SAID something. The weakest tier.</summary>
    [JsonStringEnumMemberName("verbal")]
    Verbal,

    /// <summary>The subject attempted an action but it was not carried out.</summary>
    [JsonStringEnumMemberName("intent_to_act")]
    IntentToAct,

    /// <summary>A real action crossed a real boundary. The strongest tier.</summary>
    [JsonStringEnumMemberName("behavioral")]
    Behavioral,
}

/// <summary>What an interception point can physically do — the honesty field.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AevpEnforcementCapability>))]
public enum AevpEnforcementCapability
{
    /// <summary>Can see the call but cannot stop or change it. A permit here proves nothing about safety.</summary>
    [JsonStringEnumMemberName("observe")]
    Observe,

    /// <summary>Can deny the call before it happens.</summary>
    [JsonStringEnumMemberName("block")]
    Block,

    /// <summary>Can deny and can rewrite the call's target.</summary>
    [JsonStringEnumMemberName("transform")]
    Transform,
}

/// <summary>Who produced the verdict.</summary>
/// <param name="Id">Stable identifier for the deciding component, e.g. a gate policy name.</param>
/// <param name="Version">Version of that component, when known.</param>
/// <param name="Model">The model backing it, for LLM-based judges. Null for deterministic deciders.</param>
public sealed record JudgeIdentity(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("version")] string? Version = null,
    [property: JsonPropertyName("model")] string? Model = null);

/// <summary>
/// Whether the judge is fit to decide, in the terms AgentEval's <c>GateCalibrationHarness</c> already computes.
/// </summary>
/// <param name="Kappa">Cohen's κ against a gold set.</param>
/// <param name="DecisiveAccuracy">Accuracy over conclusive cases only.</param>
/// <param name="DangerousErrors">False negatives — the errors that matter most.</param>
/// <param name="SampleSize">Cases the calibration ran over. Small n makes every other number weaker.</param>
/// <param name="BaselineBeaten">
/// Whether the judge beat a NAMED deterministic baseline. A judge that cannot beat a regex is not fit to block.
/// </param>
public sealed record CalibrationEvidence(
    [property: JsonPropertyName("kappa")] double Kappa,
    [property: JsonPropertyName("decisive_accuracy")] double DecisiveAccuracy,
    [property: JsonPropertyName("dangerous_errors")] int DangerousErrors,
    [property: JsonPropertyName("n")] int SampleSize,
    [property: JsonPropertyName("baseline_beaten")] bool BaselineBeaten);
