// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Benchmarks;
using AgentEval.Memory.Evaluators;
using AgentEval.Results.Json;
using AgentEval.Results.Suites;

namespace AgentEval.Cli.Infrastructure;

/// <summary>Captures the shipped memory generator and policy without claiming its runtime-generated content is frozen.</summary>
internal static class MemorySuiteDefinition
{
    internal static SuiteDefinitionManifest Create(string preset, MemoryBenchmark benchmark, string? judgeModel, string? judgeProvider)
    {
        var inputs = new JsonObject();
        foreach (var type in new[] { typeof(MemorySuiteDefinition), typeof(MemoryBenchmarkRunner),
            typeof(AgentEval.Evals.CompositeEval), typeof(AgentEval.Evals.IEval),
            typeof(SuiteDefinitionManifest), typeof(AgentEval.Results.Adapters.Native.AefCompositeRunWriter) })
            inputs["implementation." + type.Assembly.GetName().Name] = SuiteDefinitionManifest.Hash(File.ReadAllBytes(type.Assembly.Location));
        var assembly = typeof(MemoryBenchmarkRunner).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n =>
            n.Contains(".scenarios.", StringComparison.Ordinal) || n.Contains(".corpus.", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            inputs["resource." + name] = SuiteDefinitionManifest.Hash(buffer.ToArray());
        }
        return new SuiteDefinitionReader().Read(AefJsonWriter.Document(new JsonObject
        {
            ["schemaVersion"] = "1.0", ["kind"] = "composite", ["suiteRef"] = "bench:memory/" + preset,
            ["definitionVersion"] = "memory-generator-1", ["frozen"] = false, ["inputs"] = inputs,
            ["caseIds"] = new JsonArray("memory." + preset.ToLowerInvariant()),
            ["definition"] = new JsonObject
            {
                ["producer"] = "memory", ["benchmark"] = benchmark.Name,
                ["resolvedPreset"] = benchmark.PresetResolutionKey ?? benchmark.Name,
                ["targetTokensOverride"] = benchmark.TargetTokensOverride,
                ["overflowCallsOverride"] = benchmark.OverflowCallsOverride,
                ["categories"] = new JsonArray(benchmark.Categories.Select(c => (JsonNode?)new JsonObject
                {
                    ["name"] = c.Name, ["scenarioType"] = c.ScenarioType.ToString(), ["weight"] = c.Weight,
                }).ToArray()),
                ["contentPolicy"] = "embedded-scenarios-and-corpora;code-generated-fallbacks",
                ["clockPolicy"] = "runtime-utc-for-unanchored-temporal-inputs",
                ["judgeModel"] = judgeModel, ["judgeProvider"] = judgeProvider,
                ["judgeSampling"] = "provider-default", ["aggregation"] = "WeightedSum",
                ["passThreshold"] = 0.7, ["warnThreshold"] = 0.5, ["trialsPerCase"] = 1,
            },
        }));
    }
}
