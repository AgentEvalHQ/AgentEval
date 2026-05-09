// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.GdprBenchmark.Articles.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AgentEval.GdprBenchmark.Articles.Loading;

/// <summary>Loads GDPR article scenario YAML files into typed <see cref="ArticleSpec"/> records.</summary>
public sealed class ArticleScenarioYamlLoader
{
    private readonly IDeserializer _yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>Loads a single article YAML file from <paramref name="path"/>.</summary>
    public async Task<ArticleSpec> LoadAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var text = await File.ReadAllTextAsync(path, ct);
        var dto = _yaml.Deserialize<ArticleDto>(text)
            ?? throw new InvalidOperationException($"YAML at {path} deserialised to null.");
        return dto.ToSpec();
    }

    /// <summary>
    /// Loads all <c>*.yaml</c> files found (recursively) under <paramref name="directory"/>.
    /// Returns an empty list if the directory does not exist.
    /// </summary>
    public async Task<IReadOnlyList<ArticleSpec>> LoadAllAsync(string directory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
            return Array.Empty<ArticleSpec>();

        var files = Directory.EnumerateFiles(directory, "*.yaml", SearchOption.AllDirectories);
        var tasks = files.Select(f => LoadAsync(f, ct));
        return await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Loads all article YAMLs embedded as resources in <paramref name="assembly"/>.
    /// Resource names are matched with <c>EndsWith(".yaml")</c>; resources whose name contains
    /// <c>"test-fixture"</c> are excluded by default so the validator/registry only see real articles.
    /// </summary>
    public async Task<IReadOnlyList<ArticleSpec>> LoadAllFromAssemblyAsync(System.Reflection.Assembly assembly, bool includeTestFixtures = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var names = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));
        if (!includeTestFixtures)
            names = names.Where(n => !n.Contains("test-fixture", StringComparison.OrdinalIgnoreCase));

        var results = new List<ArticleSpec>();
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"Embedded YAML resource '{name}' could not be opened.");
            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync(ct);
            var dto = _yaml.Deserialize<ArticleDto>(text)
                ?? throw new InvalidOperationException($"Embedded YAML resource '{name}' deserialised to null.");
            results.Add(dto.ToSpec());
        }
        return results;
    }

    // ── Internal YAML DTOs ────────────────────────────────────────────────────
    // YamlDotNet serializes to mutable POCOs reliably across all TFMs.
    // We map to the public immutable records after deserialization.

    private sealed class ArticleDto
    {
        public MetadataDto? Metadata { get; set; }
        public List<ScenarioDto>? Scenarios { get; set; }

        public ArticleSpec ToSpec()
        {
            var meta = (Metadata ?? new MetadataDto()).ToRecord();
            var scenarios = (Scenarios ?? [])
                .Select(s => s.ToRecord())
                .ToList()
                .AsReadOnly();
            return new ArticleSpec(meta, scenarios);
        }
    }

    private sealed class MetadataDto
    {
        public string Article { get; set; } = string.Empty;
        public string Pillar { get; set; } = string.Empty;
        public string ControlId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty;
        public double PassThreshold { get; set; }
        public double WarnThreshold { get; set; }
        public double PillarWeight { get; set; }
        public string Aggregation { get; set; } = "weighted_sum";
        public string? Description { get; set; }

        public ArticleMetadata ToRecord() =>
            new(Article, Pillar, ControlId, Title, Severity,
                PassThreshold, WarnThreshold, PillarWeight, Aggregation, Description);
    }

    private sealed class ScenarioDto
    {
        public string Id { get; set; } = string.Empty;
        public string Pattern { get; set; } = string.Empty;
        public double Weight { get; set; }
        public string Input { get; set; } = string.Empty;
        public List<string>? EvaluationCriteria { get; set; }
        public string Granularity { get; set; } = string.Empty;
        public bool Sensitive { get; set; }
        public string? ExpectedBehavior { get; set; }
        public List<string>? Tags { get; set; }

        public ScenarioSpec ToRecord() =>
            new(Id, Pattern, Weight, Input,
                (EvaluationCriteria ?? []).AsReadOnly(),
                Granularity, Sensitive, ExpectedBehavior,
                (Tags ?? []).AsReadOnly());
    }
}
