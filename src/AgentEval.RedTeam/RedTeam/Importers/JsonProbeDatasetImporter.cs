// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using System.Text.Json;
using System.Text.Json.Nodes;
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

    private readonly string _promptField;

    /// <summary>Creates a JSON importer. <paramref name="promptField"/> is the object key holding the prompt text —
    /// the default <c>"prompt"</c> uses the native AgentEval seed schema; pass a custom key (e.g.
    /// <c>"test_case_prompt"</c> for CyberSecEval) to import a dataset whose prompt lives under a different key.</summary>
    public JsonProbeDatasetImporter(string promptField = "prompt")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptField);
        _promptField = promptField;
    }

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

        // Custom prompt field (e.g. CyberSecEval's "test_case_prompt"): read it loosely via JsonNode so any
        // array-of-objects dataset works, without forcing the native AgentEval seed schema.
        if (!string.Equals(_promptField, "prompt", StringComparison.OrdinalIgnoreCase))
            return ImportWithField(content, datasetName);

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

    // Loose import: a JSON array of objects, reading the prompt from the configured custom field.
    private IReadOnlyList<AttackProbe> ImportWithField(string content, string datasetName)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(content); }
        catch (JsonException ex)
        {
            throw new FormatException($"Dataset '{datasetName}' is not valid JSON: {ex.Message}", ex);
        }
        if (root is not JsonArray array)
            throw new FormatException($"Dataset '{datasetName}' must be a JSON array of objects.");

        var meta = new Dictionary<string, object>
        {
            [DatasetKey] = datasetName,
            [LicenseKey] = "unspecified",
        };

        var probes = new List<AttackProbe>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            var prompt = array[i]?[_promptField]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(prompt)) continue;       // object missing the field — skip
            probes.Add(new AttackProbe
            {
                Id = $"{datasetName}-{i:D4}",
                Prompt = prompt,
                Difficulty = Difficulty.Hard,
                AttackName = datasetName,
                Technique = "imported",
                Source = datasetName,
                Metadata = meta,
            });
        }
        if (probes.Count == 0)
            throw new FormatException($"Dataset '{datasetName}' has no objects with a non-empty '{_promptField}' field.");
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
