// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Guardrails.Judges;

/// <summary>How an application accepts existing calibration evidence for an inline judge.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<CalibrationReusePolicy>))]
public enum CalibrationReusePolicy
{
    /// <summary>Require the configured instrument and observed model to match. Legacy reports retain axis-only matching.</summary>
    RequireMatchingInstrument,

    /// <summary>Explicitly accept a ready independent report on the same axis for another instrument, language or model.
    /// This is an application trust decision, not a new measurement of the target judge's accuracy.</summary>
    AllowSameAxis
}

/// <summary>The relationship between calibration evidence and an actual gate decision.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<CalibrationUseKind>))]
public enum CalibrationUseKind
{
    /// <summary>The instrument matches; any model observations match the independently calibrated judge.
    /// Implementation-owned structural decisions need no model call.</summary>
    Matched,
    /// <summary>Legacy axis-only evidence; model and language equivalence have not been established.</summary>
    LegacyAxisOnly,
    /// <summary>The application explicitly accepted transferred evidence; target accuracy was not measured by this report.</summary>
    Transferred,
    /// <summary>No conclusive judge measurement was obtained; the gate's existing failure policy still applies.</summary>
    Unmeasured,
    /// <summary>The verdict cannot claim readiness under the selected policy.</summary>
    Rejected
}

/// <summary>Content-free provenance of calibration use. Original report measurements are never rewritten.</summary>
public sealed record CalibrationUse
{
    /// <summary>Evaluation axis shared by the report and target judge.</summary>
    public required string Axis { get; init; }
    /// <summary>Application-selected reuse policy.</summary>
    public required CalibrationReusePolicy Policy { get; init; }
    /// <summary>Whether evidence matched, was transferred, retained legacy scope, or was rejected.</summary>
    public required CalibrationUseKind Kind { get; init; }
    /// <summary>When the source report was measured; not the time of transfer.</summary>
    public required DateTimeOffset CalibratedAt { get; init; }
    /// <summary>Independently calibrated source instrument, or null for legacy evidence.</summary>
    public string? SourceInstrumentDigest { get; init; }
    /// <summary>Declared target instrument, or null for a legacy judge.</summary>
    public string? TargetInstrumentDigest { get; init; }
    /// <summary>Provider-reported models from the source calibration; empty when unknown.</summary>
    public IReadOnlyList<string> CalibratedModelIds { get; init; } = [];
    /// <summary>Provider-reported models in this verdict. Null entries retain unreported model identities.</summary>
    public IReadOnlyList<string?> ObservedModelIds { get; init; } = [];
    /// <summary>Effective locales recorded by the source calibration; empty for legacy reports without locale evidence.</summary>
    public IReadOnlyList<string> CalibratedLocales { get; init; } = [];
    /// <summary>Effective locales recorded in this verdict; empty when the legacy judge does not expose them.</summary>
    public IReadOnlyList<string> ObservedLocales { get; init; } = [];
    /// <summary>Explanation of the evidence relationship, without case text or model responses.</summary>
    public required string Reason { get; init; }
    /// <summary>Whether the application's policy accepts this evidence for the verdict.</summary>
    public bool IsAccepted => Kind is CalibrationUseKind.Matched or CalibrationUseKind.LegacyAxisOnly or CalibrationUseKind.Transferred;
}
