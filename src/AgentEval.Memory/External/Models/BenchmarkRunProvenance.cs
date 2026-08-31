// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Serialization;

namespace AgentEval.Memory.External.Models;

/// <summary>
/// Identifies the inputs a run was produced from, so two stored results can be shown to be
/// comparable instead of assumed to be.
/// </summary>
/// <remarks>
/// <para>
/// A sealed benchmark base is only comparable to a later run if the dataset and the judge prompts
/// are the same. Neither is pinned by a package version: the dataset is resolved from disk or an
/// environment variable, and the judge prompts are library source that can change in any release.
/// Verifying either by hand-diffing between releases is work that a hash does exactly.
/// </para>
/// <para>
/// Every field is nullable and every one is null when it was not measured. None is ever
/// defaulted to a plausible value.
/// </para>
/// </remarks>
public sealed class BenchmarkRunProvenance
{
    /// <summary>The provenance detail actually captured.</summary>
    public required RunProvenanceMode Mode { get; init; }

    /// <summary>
    /// SHA-256 over the judge prompt templates, rendered with fixed sentinel inputs so the hash
    /// depends on the template text alone and not on the questions a run happened to draw.
    /// Any edit to any judge prompt changes this value.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? JudgePromptFingerprint { get; init; }

    /// <summary>
    /// Which prompt families the fingerprint covers, so a future run can tell a changed prompt from
    /// a newly-added one.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? JudgePromptTemplateNames { get; init; }

    /// <summary>
    /// The completion budget the judge actually ran with, in tokens.
    /// </summary>
    /// <remarks>
    /// Stamped because RAISING THIS CAP CAN FLIP A VERDICT. On a reasoning deployment the budget
    /// covers reasoning tokens too, so a judge that was silently truncated at one cap returns a
    /// real verdict at a higher one — and two runs that differ only in this value are not
    /// comparable, which is invisible unless the value travels with the run. The consuming project
    /// asked for it explicitly when the default was raised from 512.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? JudgeMaxOutputTokens { get; init; }

    /// <summary>Informational version of the AgentEval.Memory assembly that produced the run.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentEvalVersion { get; init; }

    /// <summary>Resolved dataset path; null unless <see cref="RunProvenanceMode.Full"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DatasetPath { get; init; }

    /// <summary>
    /// Name and version of a dataset that has no path — an embedded corpus such as
    /// <c>agenteval-timegrounded-v1</c>. Null for datasets read from disk, which
    /// <see cref="DatasetPath"/> identifies instead.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DatasetIdentifier { get; init; }

    /// <summary>SHA-256 over the dataset file bytes; null unless <see cref="RunProvenanceMode.Full"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DatasetSha256 { get; init; }

    /// <summary>Dataset file size in bytes; null unless <see cref="RunProvenanceMode.Full"/>.</summary>
    public long? DatasetSizeBytes { get; init; }

    /// <summary>
    /// Questions present in the dataset file BEFORE sampling; null unless
    /// <see cref="RunProvenanceMode.Full"/>. Distinct from
    /// <see cref="ExternalBenchmarkResult.SelectedQuestions"/>, which is what the sample drew.
    /// </summary>
    public int? DatasetQuestionCount { get; init; }

    /// <summary>
    /// SHA-256 over the ordered selected question ids. Two runs sharing this value drew exactly the
    /// same questions in the same order; null unless <see cref="RunProvenanceMode.Full"/>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SelectedQuestionIdFingerprint { get; init; }
}
