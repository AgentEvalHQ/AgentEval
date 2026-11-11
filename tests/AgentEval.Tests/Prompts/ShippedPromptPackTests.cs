// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json.Nodes;
using AgentEval.Cli.Commands;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Quality;
using AgentEval.Prompts;
using AgentEval.Testing;
using Json.Schema;
using Xunit;

namespace AgentEval.Tests.Prompts;

public sealed class ShippedPromptPackTests
{
    private static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AgentEval.sln"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public async Task SwedishSample_SchemaAndDescriptors_AgreeWithoutClaimingCalibration()
    {
        var root = Root();
        var path = Path.Combine(root, "samples", "AgentEval.Samples", "PromptPacks", "sv-SE", "prompts.json");
        var schema = JsonSchema.FromText(await File.ReadAllTextAsync(Path.Combine(root, "docs", "schemas", "judge-prompt-pack.schema.json")));
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        Assert.True(schema.Evaluate(manifest).IsValid);
        var pack = await new JudgePromptPackLoader().LoadAsync(path);
        var supported = PromptsCommand.SupportedDefinitions();
        Assert.Equal(supported.Select(d => d.EvaluatorKey).Order(), pack.Definitions.Select(d => d.EvaluatorKey).Order());
        foreach (var baseline in supported)
        {
            var resolved = new JudgePromptResolver().Resolve(baseline, new() { Pack = pack, Locale = "sv-SE" });
            Assert.Equal("authored", resolved.Definition.Status);
            Assert.Equal("sv-SE", resolved.Provenance.EffectiveLocale);
            Assert.Contains("ä", resolved.Definition.SystemText);
        }
        manifest["definitions"]![0]!["scale"] = "unit";
        Assert.False(schema.Evaluate(manifest).IsValid); // CriteriaV1 cannot silently change numeric interpretation.
    }

    [Fact]
    public async Task SwedishSample_RubricReplyWithStableIds_UsesLocalizedPolicyAndEvidence()
    {
        var pack = await new JudgePromptPackLoader().LoadAsync(Path.Combine(Root(), "samples", "AgentEval.Samples", "PromptPacks", "sv-SE", "prompts.json"));
        var client = new FakeChatClient("""{"score":0.85,"reasoning":"Kort men relevant.","criteria_results":[{"id":"criterion-1","met":true,"explanation":"Besvarar frågan."},{"id":"criterion-2","met":true,"explanation":"Relevant."},{"id":"criterion-3","met":true,"explanation":"Inga sidospår."}]}""");
        var result = await new RelevanceEval(new ChatClientEvaluator(client).WithPrompts(new() { Pack = pack, Locale = "sv-SE" }))
            .EvaluateAsync(new EvalInput("När öppnar butiken?", "Klockan nio."));
        Assert.True(result.Score.Passed);
        Assert.Equal(.85, result.Score.Value);
        Assert.Equal("sv-SE", result.Provenance.JudgePrompt!.EffectiveLocale);
        Assert.Contains("FRÅGA", client.ReceivedMessages.Single().Last().Text);
    }
}
