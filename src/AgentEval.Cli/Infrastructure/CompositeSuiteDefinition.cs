// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text;
using System.Text.Json.Nodes;
using AgentEval.Evals;
using AgentEval.Results.Json;
using AgentEval.Results.Suites;

namespace AgentEval.Cli.Infrastructure;

/// <summary>Captures supported configured evaluator graphs before execution. Unknown instruments stay unclassified.</summary>
internal static class CompositeSuiteDefinition
{
    internal static SuiteDefinitionManifest? TryCreate(string suiteRef, CompositeEval benchmark, EvalInput input, int runs)
    {
        var inputs = new JsonObject();
        var cases = new HashSet<string>(StringComparer.Ordinal);
        void Implementation(Type type)
        {
            var assembly = type.Assembly;
            var name = "implementation." + assembly.GetName().Name;
            if (!inputs.ContainsKey(name)) inputs[name] = SuiteDefinitionManifest.Hash(File.ReadAllBytes(assembly.Location));
        }
        JsonObject? Capture(IEval eval, bool emitted, int depth)
        {
            if (depth > EvalTreeLimits.MaxTreeWalkDepth) return null;
            Implementation(eval.GetType());
            if (emitted) cases.Add(eval.Key);
            var node = new JsonObject { ["key"] = eval.Key, ["version"] = eval.Version, ["type"] = eval.GetType().FullName };
            switch (eval)
            {
                case IEvalDefinitionWrapper wrapper:
                    if (wrapper.Key != wrapper.DefinitionInner.Key) return null;
                    var wrapped = Capture(wrapper.DefinitionInner, emitted, depth + 1);
                    if (wrapped is null) return null;
                    node["inner"] = wrapped;
                    return node;
                case AgentScenarioEval scenario:
                    var inner = Capture(scenario.Inner, emitted, depth + 1);
                    if (inner is null) return null;
                    node["scenarioInput"] = scenario.ScenarioInputDigest;
                    node["inner"] = inner;
                    return node;
                case AtomicLlmEval atomic:
                    var declaration = atomic.CaptureDefinition();
                    if (!declaration.PromptIdentified) return null;
                    Implementation(declaration.EvaluatorType);
                    node["rubric"] = declaration.RubricDigest;
                    node["scorePolicy"] = declaration.ScorePolicyDigest;
                    node["threshold"] = declaration.PassThreshold;
                    node["severity"] = declaration.FailureSeverity;
                    node["judgeModel"] = declaration.JudgeModel;
                    node["judgeProvider"] = declaration.JudgeProvider;
                    node["toolData"] = declaration.ToolData.ToString();
                    node["evaluatorType"] = declaration.EvaluatorType.FullName;
                    return node;
                case CompositeEval composite:
                    // These strategies have no configurable instance state. Custom strategies need an explicit declaration.
                    var strategy = composite.Aggregation.GetType();
                    if (strategy != typeof(WeightedSumAggregation) && strategy != typeof(WeightedMedianAggregation)
                        && strategy != typeof(MinAggregation) && strategy != typeof(MajorityVoteAggregation)
                        && strategy != typeof(CapByWorstAggregation)) return null;
                    Implementation(strategy);
                    node["aggregation"] = strategy.FullName;
                    node["threshold"] = composite.Threshold;
                    node["minimumMeasuredShare"] = composite.MinimumMeasuredShare;
                    node["severityCapsThreshold"] = composite.SeverityCapsThreshold;
                    var components = new JsonArray();
                    foreach (var component in composite.Components)
                    {
                        var child = Capture(component.Eval, true, depth + 1);
                        if (child is null) return null;
                        components.Add(new JsonObject
                        {
                            ["weight"] = component.Weight, ["required"] = component.Required,
                            ["onFailure"] = component.OnFailure.ToString(), ["eval"] = child,
                        });
                    }
                    node["components"] = components;
                    return node;
                case IEvalConfigurationSource configured:
                    node["configuration"] = configured.CaptureConfiguration().DeepClone();
                    return node;
                default: return null;
            }
        }
        var graph = Capture(benchmark, false, 0);
        if (graph is null || cases.Count == 0) return null;
        Implementation(typeof(CompositeSuiteDefinition));
        Implementation(typeof(IEval));
        Implementation(typeof(SuiteDefinitionManifest));
        Implementation(typeof(AgentEval.Results.Adapters.Native.AefCompositeRunWriter));
        inputs["query"] = SuiteDefinitionManifest.Hash(Encoding.UTF8.GetBytes(input.Query));
        // Context influences the judge; responses belong to the subject evidence, not the suite definition.
        inputs["context"] = SuiteDefinitionManifest.Hash(Encoding.UTF8.GetBytes(input.Context ?? ""));
        inputs["groundTruth"] = SuiteDefinitionManifest.Hash(Encoding.UTF8.GetBytes(input.GroundTruth ?? ""));
        inputs["inputDeclarations"] = SuiteDefinitionManifest.Hash(AefJsonWriter.Document(
            System.Text.Json.JsonSerializer.SerializeToNode(new
            {
                input.SystemMessage, input.ExpectedActions, input.ToolDefinitions,
            })!.AsObject()));
        return new SuiteDefinitionReader().Read(AefJsonWriter.Document(new JsonObject
        {
            ["schemaVersion"] = "1.0", ["kind"] = "composite", ["suiteRef"] = suiteRef,
            ["definitionVersion"] = "composite-definition-1", ["frozen"] = true, ["inputs"] = inputs,
            ["caseIds"] = new JsonArray(cases.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["definition"] = new JsonObject
            {
                ["eval"] = graph, ["trialsPerCase"] = runs,
                ["trialAggregation"] = runs > 1 ? "majority_vote" : "none",
            },
        }));
    }
}
