// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;

namespace AgentEval.Memory.DataLoading;

/// <summary>
/// Loads benchmark scenario definitions from embedded JSON resources (or external paths).
/// Resolves preset inheritance: Standard extends Quick, Full extends Standard.
/// </summary>
public static class ScenarioLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Loads a scenario definition from embedded resources.
    /// </summary>
    /// <param name="categoryName">Scenario file name without extension (e.g., "basic-retention").</param>
    public static ScenarioDefinition Load(string categoryName)
    {
        ArgumentNullException.ThrowIfNull(categoryName);

        var json = LoadJsonFromEmbeddedResource(categoryName)
            ?? LoadJsonFromExternalPath(categoryName);

        if (json == null)
            throw new FileNotFoundException(
                $"Scenario '{categoryName}' not found in embedded resources or external paths.");

        return JsonSerializer.Deserialize<ScenarioDefinition>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize scenario '{categoryName}'");
    }

    /// <summary>
    /// Resolves a specific preset by merging inherited facts/queries.
    /// "standard" extends "quick": gets Quick's facts + Standard's additional facts.
    /// "full" extends "standard": gets Quick's + Standard's + Full's facts.
    /// </summary>
    public static ResolvedPreset ResolvePreset(ScenarioDefinition scenario, string presetName)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(presetName);

        var normalizedName = presetName.ToLowerInvariant();

        if (!scenario.Presets.TryGetValue(normalizedName, out var preset))
        {
            // Fall back to "quick" if the requested preset doesn't exist
            if (!scenario.Presets.TryGetValue("quick", out preset))
                throw new InvalidOperationException(
                    $"Scenario '{scenario.Name}' has no preset '{normalizedName}' and no 'quick' fallback.");
        }

        // Resolve inheritance chain
        var facts = new List<FactDefinition>();
        var noise = new List<string>();
        var queries = new List<QueryDefinition>();
        ContextPressureConfig? contextPressure = null;

        ResolveChain(scenario, preset, facts, noise, queries, ref contextPressure);

        // Use scenario-level context pressure if preset doesn't override
        contextPressure ??= scenario.ContextPressure;

        return new ResolvedPreset
        {
            Facts = facts,
            NoiseBetweenFacts = noise,
            Queries = queries,
            ContextPressure = contextPressure
        };
    }

    /// <summary>
    /// Lists available scenario names from embedded resources.
    /// </summary>
    public static IReadOnlyList<string> ListAvailable()
    {
        var assembly = typeof(ScenarioLoader).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(n => n.Contains("scenarios") && n.EndsWith(".json"))
            .Select(n => Path.GetFileNameWithoutExtension(n))
            .ToList();
    }

    // --- Optional external path support (for custom scenarios) ---

    /// <summary>
    /// External scenario directory path. Set this to load scenarios from filesystem
    /// in addition to embedded resources. Thread-safe via volatile.
    /// </summary>
    public static volatile string? ExternalScenarioPath;

    // --- Private helpers ---

    private static void ResolveChain(
        ScenarioDefinition scenario,
        PresetDefinition preset,
        List<FactDefinition> facts,
        List<string> noise,
        List<QueryDefinition> queries,
        ref ContextPressureConfig? contextPressure)
    {
        // Resolve parent first (recursively)
        if (preset.Extends != null &&
            scenario.Presets.TryGetValue(preset.Extends.ToLowerInvariant(), out var parent))
        {
            ResolveChain(scenario, parent, facts, noise, queries, ref contextPressure);
        }

        // Then add this preset's contributions
        if (preset.Facts != null) facts.AddRange(preset.Facts);
        if (preset.NoiseBetweenFacts != null) noise.AddRange(preset.NoiseBetweenFacts);
        if (preset.Queries != null) queries.AddRange(preset.Queries);
        if (preset.ContextPressure != null) contextPressure = preset.ContextPressure;
    }

    private static string? LoadJsonFromEmbeddedResource(string categoryName)
    {
        var assembly = typeof(ScenarioLoader).Assembly;
        var names = assembly.GetManifestResourceNames();
        var resourceName = names.FirstOrDefault(n => n.EndsWith($"{categoryName}.json") && n.Contains("scenarios"));

        if (resourceName == null) return null;

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string? LoadJsonFromExternalPath(string categoryName)
    {
        if (ExternalScenarioPath == null) return null;

        var path = Path.Combine(ExternalScenarioPath, $"{categoryName}.json");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
}
