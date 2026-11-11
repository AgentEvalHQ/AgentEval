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
            string[] supported = ["SupplyChain", "InsecureOutput", "InferenceAPIAbuse", "DataPoisoning", "Misinformation", "ExcessiveAgency"];
            var names = preset is null || preset == "supported" ? supported
                : supported.Where(n => string.Equals(n, preset, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (names.Length == 0)
                throw new NotSupportedException($"Expected: a supported red-team judge graph. Actual: preset '{preset}'. Suggestions: select supported or {string.Join(", ", supported)}. Because: unsupported detectors cannot silently keep English rules.");
            return names.SelectMany(n => GraderFactory.CapturePromptDefinitions(Attack.ByName(n)!, new() { JudgeClient = client }))
                .DistinctBy(d => d.EvaluatorKey).OrderBy(d => d.EvaluatorKey, StringComparer.Ordinal).ToArray();
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
