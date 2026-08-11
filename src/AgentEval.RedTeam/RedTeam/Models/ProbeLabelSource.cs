// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

namespace AgentEval.RedTeam;

/// <summary>
/// Records WHERE a probe's expected-outcome label came from, so a reported rate can be read with the right
/// confidence.
/// </summary>
/// <remarks>
/// <para>A recall number computed over template-generated probes and one computed over human-authored,
/// in-the-wild attacks are not the same claim, and today nothing in a report distinguishes them. The
/// reference prompt-injection benchmark carries <c>label_source</c> on every corpus row for exactly this
/// reason — its 280 rows are all <c>synthetic_template</c>, which is a material caveat on every number it
/// publishes.</para>
///
/// <para>Stored on <see cref="AttackProbe.Metadata"/> under <see cref="MetadataKey"/>, following the same
/// convention as <c>TransformProvenance</c> so provenance keys stay discoverable in one family. Absent
/// metadata reports <see cref="Unspecified"/> rather than guessing a source — an unknown provenance is a
/// gap to be filled, not a value to be invented.</para>
/// </remarks>
public static class ProbeLabelSource
{
    /// <summary>Metadata key carrying the label source.</summary>
    public const string MetadataKey = "label.source";

    /// <summary>A human authored or reviewed this probe and its expected outcome.</summary>
    public const string Human = "human";

    /// <summary>Generated from a template or generator. The label is only as good as the generator.</summary>
    public const string SyntheticTemplate = "synthetic_template";

    /// <summary>No label source was recorded. Not a claim about provenance — an absence of one.</summary>
    public const string Unspecified = "unspecified";

    /// <summary>
    /// Builds the source value for a probe imported from a named external dataset, e.g.
    /// <c>imported:HarmBench</c>. Keeping the dataset name in the value means a report can be filtered by
    /// corpus, and licence-restricted imports stay identifiable.
    /// </summary>
    /// <param name="datasetName">The dataset the probe came from. Must not be blank.</param>
    public static string Imported(string datasetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetName);
        return $"imported:{datasetName}";
    }

    /// <summary>
    /// Reads the recorded label source, or <see cref="Unspecified"/> when the probe carries none.
    /// </summary>
    public static string Of(AttackProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);

        return probe.Metadata is not null
               && probe.Metadata.TryGetValue(MetadataKey, out var value)
               && value is string s
               && !string.IsNullOrWhiteSpace(s)
            ? s
            : Unspecified;
    }

    /// <summary>Whether <paramref name="labelSource"/> denotes an import, and if so from which dataset.</summary>
    public static bool TryGetImportedDataset(string labelSource, out string datasetName)
    {
        const string prefix = "imported:";
        if (labelSource is not null && labelSource.StartsWith(prefix, StringComparison.Ordinal))
        {
            datasetName = labelSource[prefix.Length..];
            return datasetName.Length > 0;
        }

        datasetName = string.Empty;
        return false;
    }
}
