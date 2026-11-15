// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentEval.Guardrails.Judges;
using AgentEval.Prompts;

namespace AgentEval.Cli.Commands.Gatekeeper;

/// <summary>Explicit operator-supplied labels. Review metadata is an attestation, never inferred from judge agreement.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GateCalibrationDataset
{
    public required string Id { get; init; }
    public required string Axis { get; init; }
    public required string Locale { get; init; }
    public required string SplitLabel { get; init; }
    public required string LabelOrigin { get; init; }
    public required string ReviewStatus { get; init; }
    public string? Reviewer { get; init; }
    public required IReadOnlyList<GateCalibrationLabel> Cases { get; init; }

    internal bool Reviewed => ReviewStatus == "human-reviewed" && !string.IsNullOrWhiteSpace(Reviewer);
    internal string Digest => "sha256:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this))).ToLowerInvariant();
    internal JudgeGoldSet GoldSet() => new(Axis, Cases.Select(c => new JudgeGoldCase(c.Text, c.ShouldBlock)));
    internal GateDatasetProvenance Provenance => new(Id, Digest, Locale, SplitLabel, LabelOrigin, ReviewStatus, Reviewer);

    internal static GateCalibrationDataset? Load(string? path)
    {
        if (path is null) return null;
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new ArgumentException("Calibration dataset exceeds the 16 MiB limit.");
        var text = File.ReadAllText(path);
        try
        {
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            if (JudgeResponseReader.HasDuplicateProperties(document.RootElement)) throw new JsonException("Duplicate fields.");
            var data = JsonSerializer.Deserialize<GateCalibrationDataset>(text, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
                ?? throw new JsonException("Expected a dataset object.");
            data.Validate(data.Axis);
            return data;
        }
        catch (JsonException) { throw new ArgumentException("Expected: a calibration dataset with id, axis, locale, splitLabel, labelOrigin, reviewStatus and labelled cases. Actual: malformed dataset. Suggestions: use the documented JSON format. Because: missing or ambiguous labels cannot establish calibration."); }
    }

    internal void Validate(string axis)
    {
        if (new[] { Id, Axis, Locale, SplitLabel }.Any(string.IsNullOrWhiteSpace) || Axis != axis ||
            LabelOrigin is not ("ai-authored" or "human-authored" or "imported" or "unknown") ||
            ReviewStatus is not ("unreviewed" or "human-reviewed") || ReviewStatus == "human-reviewed" && !Reviewed ||
            Cases is null || Cases.Count == 0 || Cases.Any(c => c is null || string.IsNullOrWhiteSpace(c.Text)))
            throw new ArgumentException("Expected: matching axis, explicit dataset metadata, non-empty labelled cases and a reviewer for human-reviewed labels. Actual: invalid calibration metadata. Suggestions: correct the dataset; retain unreviewed for draft labels. Because: review and corpus identity must not be inferred.");
        _ = GoldSet(); // Both directions are required by the same library contract.
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record GateCalibrationLabel
{
    public required string Text { get; init; }
    public required bool ShouldBlock { get; init; }
}

internal sealed record GateDatasetProvenance(string Id, string Digest, string Locale, string SplitLabel,
    string LabelOrigin, string ReviewStatus, string? Reviewer);
