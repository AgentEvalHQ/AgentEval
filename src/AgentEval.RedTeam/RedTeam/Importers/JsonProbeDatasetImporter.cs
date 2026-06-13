// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentEval.RedTeam.Importers;

/// <summary>
/// Imports a JSON seed-prompt dataset into <see cref="AttackProbe"/>s (next-wave Wave-F core). Schema — a JSON array:
/// <code>
/// [ { "id": "HB-001", "prompt": "…", "technique": "harmful_behavior",
///     "expectedTokens": ["…"], "source": "HarmBench", "license": "MIT" } ]
/// </code>
/// Only <c>prompt</c> is required; <c>id</c> defaults to <c>{dataset}-{index}</c>. <c>source</c>/<c>license</c> are
/// stamped into <see cref="AttackProbe.Metadata"/> (keys <see cref="DatasetKey"/>/<see cref="LicenseKey"/>) for
/// attribution. <c>expectedTokens</c> gives a deterministic oracle; without it the probe is judge/refusal-scored
/// (honest Inconclusive when no judge — see <see cref="ImportedProbeAttack"/>).
/// </summary>
public sealed class JsonProbeDatasetImporter : IProbeDatasetImporter
{
    /// <summary><see cref="AttackProbe.Metadata"/> key for the originating dataset name.</summary>
    public const string DatasetKey = "dataset";

    /// <summary><see cref="AttackProbe.Metadata"/> key for the dataset license.</summary>
    public const string LicenseKey = "dataset.license";

    /// <inheritdoc />
    public string Format => "json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <inheritdoc />
    public IReadOnlyList<AttackProbe> Import(string content, string datasetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetName);

        List<SeedRecord>? records;
        try
        {
            records = JsonSerializer.Deserialize<List<SeedRecord>>(content, Options);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"Dataset '{datasetName}' is not a valid JSON seed-prompt array: {ex.Message}", ex);
        }
        if (records is null)
            throw new FormatException($"Dataset '{datasetName}' deserialized to null.");

        var probes = new List<AttackProbe>(records.Count);
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            if (string.IsNullOrWhiteSpace(r.Prompt))
                throw new FormatException($"Dataset '{datasetName}' record {i} has no 'prompt'.");

            var meta = new Dictionary<string, object>
            {
                [DatasetKey] = datasetName,
                [LicenseKey] = string.IsNullOrWhiteSpace(r.License) ? "unspecified" : r.License!,
            };

            probes.Add(new AttackProbe
            {
                Id = string.IsNullOrWhiteSpace(r.Id) ? $"{datasetName}-{i:D4}" : r.Id!,
                Prompt = r.Prompt!,
                Difficulty = ParseDifficulty(r.Difficulty),
                AttackName = datasetName,
                Technique = string.IsNullOrWhiteSpace(r.Technique) ? "imported" : r.Technique,
                Source = string.IsNullOrWhiteSpace(r.Source) ? datasetName : r.Source,
                ExpectedTokens = r.ExpectedTokens is { Count: > 0 } ? r.ExpectedTokens : null,
                Metadata = meta,
            });
        }
        return probes;
    }

    private static Difficulty ParseDifficulty(string? d) => (d ?? "").Trim().ToLowerInvariant() switch
    {
        "easy" => Difficulty.Easy,
        "hard" => Difficulty.Hard,
        _ => Difficulty.Moderate,
    };

    private sealed class SeedRecord
    {
        public string? Id { get; set; }
        public string? Prompt { get; set; }
        public string? Technique { get; set; }
        public string? Source { get; set; }
        public string? License { get; set; }
        public string? Difficulty { get; set; }
        [JsonPropertyName("expectedTokens")] public List<string>? ExpectedTokens { get; set; }
    }
}
