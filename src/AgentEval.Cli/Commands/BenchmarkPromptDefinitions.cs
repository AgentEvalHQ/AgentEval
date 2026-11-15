// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Prompts;
using AgentEval.RedTeam;
using Microsoft.Extensions.AI;
using Gdpr = AgentEval.Compliance.Gdpr.Articles;
using Eu = AgentEval.Compliance.EuAiAct.Articles;

namespace AgentEval.Cli.Commands;

/// <summary>Offline descriptors from the same factories and family fallback prompts as benchmark execution.</summary>
internal static class BenchmarkPromptDefinitions
{
    internal static IReadOnlyList<JudgePromptDefinition> ForPreset(string family, string? preset)
    {
        BenchListCommand.AnchorAssemblies();
        using var client = new NoNetworkChatClient();
        if (family == "redteam")
        {
            var control = AgentEval.Guardrails.Judges.GateJudgePrompts.ForRubric(new AgentEval.Guardrails.Judges.Rubrics.OverRefusalRubric());
            if (preset == "benign-controls") return [control];
            var explanation = AgentEval.RedTeam.Evaluators.RedTeamJudgePrompts.ForExplanation();
            if (preset == "explanation") return [explanation];
            var supported = Attack.AvailableNames.Concat(Attack.OptInNames).ToArray();
            var fallback = string.Equals(preset, "fallback", StringComparison.OrdinalIgnoreCase)
                || preset?.StartsWith("fallback:", StringComparison.OrdinalIgnoreCase) == true;
            var attackPreset = fallback && preset!.Contains(':') ? preset[(preset.IndexOf(':') + 1)..] : preset;
            var union = preset is null or "supported";
            var fallbackUnion = string.Equals(preset, "fallback", StringComparison.OrdinalIgnoreCase);
            var names = union || fallbackUnion ? supported
                : supported.Where(n => string.Equals(n, attackPreset, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (names.Length == 0)
                throw new NotSupportedException($"Expected: a supported red-team judge graph. Actual: preset '{preset}'. Suggestions: select supported, fallback, fallback:<attack>, explanation, benign-controls, or {string.Join(", ", supported)}. Because: unsupported detectors cannot silently keep English rules.");
            var leaves = names.SelectMany(n => GraderFactory.CapturePromptDefinitions(Attack.ByName(n)!,
                new() { JudgeClient = client, Mode = fallback ? JudgeMode.Fallback : JudgeMode.Primary }));
            if (union) leaves = leaves.Concat(names.SelectMany(n => GraderFactory.CapturePromptDefinitions(Attack.ByName(n)!,
                new() { JudgeClient = client, Mode = JudgeMode.Fallback })));
            if (union || fallbackUnion) leaves = leaves.Append(control).Append(explanation);
            var definitions = leaves
                .DistinctBy(d => d.EvaluatorKey).OrderBy(d => d.EvaluatorKey, StringComparer.Ordinal).ToArray();
            if (definitions.Length == 0)
                throw new NotSupportedException("Expected: a prompt-based graph for authoring. Actual: the selected graph is code-only. Suggestions: use the supported union to author semantic judges; structural checks need no translated pack. Because: no model instructions exist in this graph to replace.");
            return definitions;
        }
        var judge = JudgeFactory.CreateFamily(client, family);
        if (family == "agentic")
            return BuiltInJudgePrompts.ForEval(BenchAgenticCommand.ResolvePreset(preset ?? "agentic-execution", judge, subjectId: "prompt-export"));
        if (family == "gdpr")
        {
            var builder = new Gdpr.Building.ScenarioToAtomicEval(judge);
            var registry = new Gdpr.ArticlesRegistry(new Gdpr.Loading.ArticleScenarioYamlLoader(), new Gdpr.Building.ArticleCompositeBuilder(builder));
            return BuiltInJudgePrompts.ForEval(BenchCommand.ResolvePreset(preset ?? "standard", registry, builder));
        }
        var euBuilder = new Eu.Building.ScenarioToAtomicEval(judge);
        var euRegistry = new Eu.EuAiActArticlesRegistry(new Eu.Loading.ArticleScenarioYamlLoader(), new Eu.Building.ArticleCompositeBuilder(euBuilder));
        return BuiltInJudgePrompts.ForEval(BenchEuAiActCommand.ResolvePreset(preset ?? "standard", euRegistry, euBuilder));
    }

    // A descriptor must never create a provider client, depend on credentials or call a model.
    internal sealed class NoNetworkChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Prompt authoring never invokes a judge.");
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Prompt authoring never invokes a judge.");
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
