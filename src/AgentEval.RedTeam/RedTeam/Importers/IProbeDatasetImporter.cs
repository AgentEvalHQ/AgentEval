// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
namespace AgentEval.RedTeam.Importers;

/// <summary>
/// Imports an external seed-prompt dataset (HarmBench / JailbreakBench / AdvBench / DAN collections / PyRIT
/// seed YAML, etc.) into native <see cref="AttackProbe"/>s — the next-wave breadth seam (Wave F). Each imported
/// probe carries its source + license in <see cref="AttackProbe.Metadata"/> for attribution; we import the *data*,
/// we never run garak/PyRIT as a subprocess. Importers must be deterministic.
/// </summary>
public interface IProbeDatasetImporter
{
    /// <summary>Short format id this importer handles (e.g. <c>"json"</c>).</summary>
    string Format { get; }

    /// <summary>
    /// Parses <paramref name="content"/> into probes, stamping each with <paramref name="datasetName"/> provenance.
    /// Throws <see cref="FormatException"/> on malformed input — callers surface that honestly rather than
    /// importing a partial/garbage set.
    /// </summary>
    IReadOnlyList<AttackProbe> Import(string content, string datasetName);
}
