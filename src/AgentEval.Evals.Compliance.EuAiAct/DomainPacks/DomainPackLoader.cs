// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Reflection;
using AgentEval.Evals;
using AgentEval.EuAiActBenchmark.Articles.Building;
using AgentEval.EuAiActBenchmark.Articles.Loading;

namespace AgentEval.EuAiActBenchmark.DomainPacks;

/// <summary>
/// Shared helper that loads domain-pack scenario extensions from embedded YAML resources
/// and groups them by <c>control_id</c>.
/// </summary>
internal static class DomainPackLoader
{
    /// <summary>
    /// Loads all YAML resources from <paramref name="assembly"/> whose names match
    /// <paramref name="resourceFilter"/>, deserialises each as an article patch, and
    /// groups the resulting <see cref="EvalComponent"/> instances by
    /// <c>ArticleMetadata.ControlId</c>.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<EvalComponent>> Load(
        Assembly assembly,
        Func<string, bool> resourceFilter,
        ScenarioToAtomicEval scenarioBuilder)
    {
        var loader = new ArticleScenarioYamlLoader();

        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => resourceFilter(n)
                     && n.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var grouped = new Dictionary<string, List<EvalComponent>>();
        foreach (var resourceName in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Could not open embedded resource '{resourceName}'.");
            using var reader = new StreamReader(stream);
            var yaml = reader.ReadToEnd();

            var spec = loader.LoadFromYamlText(yaml, sourceName: resourceName);

            if (!grouped.TryGetValue(spec.Metadata.ControlId, out var list))
            {
                list = new List<EvalComponent>();
                grouped[spec.Metadata.ControlId] = list;
            }

            foreach (var scenario in spec.Scenarios)
            {
                var eval = scenarioBuilder.Build(spec.Metadata, scenario);
                list.Add(new EvalComponent(eval, scenario.Weight, Required: true));
            }
        }

        return grouped.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<EvalComponent>)kv.Value);
    }
}
