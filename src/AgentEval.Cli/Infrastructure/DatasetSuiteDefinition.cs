// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Core;
using AgentEval.DataLoaders;
using AgentEval.MAF;
using AgentEval.Models;
using AgentEval.Results.Adapters.Native;
using AgentEval.Results.Json;
using AgentEval.Results.Suites;

namespace AgentEval.Cli.Infrastructure;

/// <summary>Captures dataset/evaluator identity before execution; never derives a definition from measured outcomes.</summary>
internal static class DatasetSuiteDefinition
{
    // Assembly content binds embedded rubric/metric templates and parser/default semantics without serializing
    // their text or guessing a hand-maintained release string. This is deliberately conservative across builds.
    private static readonly Lazy<IReadOnlyDictionary<string, string>> Implementations = new(() => new[]
    {
        typeof(DatasetSuiteDefinition).Assembly, typeof(ChatClientEvaluator).Assembly, typeof(DatasetTestCase).Assembly,
        typeof(DatasetLoaderFactory).Assembly, typeof(MAFEvaluationHarness).Assembly, typeof(AefEvalWriter).Assembly,
    }.Distinct().ToDictionary(a => "implementation." + a.GetName().Name,
        a => SuiteDefinitionManifest.Hash(File.ReadAllBytes(a.Location)), StringComparer.Ordinal));

    internal static SuiteDefinitionManifest Create(string suiteRef, string version, byte[] dataset,
        IEnumerable<string> plannedIds, JsonObject evaluation)
    {
        var inputs = new JsonObject { ["dataset"] = SuiteDefinitionManifest.Hash(dataset) };
        foreach (var input in Implementations.Value) inputs[input.Key] = input.Value;
        var manifest = new JsonObject
        {
            ["schemaVersion"] = "1.0", ["kind"] = "dataset", ["suiteRef"] = suiteRef,
            ["definitionVersion"] = version, ["frozen"] = true, ["inputs"] = inputs,
            ["caseIds"] = new JsonArray(plannedIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["definition"] = evaluation.DeepClone(),
        };
        return new SuiteDefinitionReader().Read(AefJsonWriter.Document(manifest));
    }
}
