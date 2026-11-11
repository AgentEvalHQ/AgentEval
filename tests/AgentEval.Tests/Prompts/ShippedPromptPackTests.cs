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
        var supported = PromptsCommand.SupportedDefinitions().Append(
            AgentEval.Guardrails.Judges.GateJudgePrompts.ForRubric(new AgentEval.Guardrails.Judges.Rubrics.OverRefusalRubric())).ToArray();
        Assert.Equal(supported.Select(d => d.EvaluatorKey).Order(), pack.Definitions.Select(d => d.EvaluatorKey).Order());
        foreach (var baseline in supported)
        {
            var resolved = new JudgePromptResolver().Resolve(baseline, new() { Pack = pack, Locale = "sv-SE" });
            Assert.Equal(baseline.EvaluatorKey == "gate.over-refusal" ? "draft" : "authored", resolved.Definition.Status);
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

    [Fact]
    public async Task MetadataSchema_AllRichEvidenceSlots_MatchesRuntimeContract()
    {
        var schema = JsonSchema.FromText(await File.ReadAllTextAsync(Path.Combine(Root(), "docs", "schemas", "judge-prompt-pack.schema.json")));
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Root(), "samples", "AgentEval.Samples", "PromptPacks", "sv-SE", "prompts.json")))!;
        manifest["definitions"]![0]!["dataSlots"] = new JsonArray("input", "output", "context", "reference", "toolCalls", "toolDefinitions", "systemMessage", "expectedActions", "evaluatorNotes", "evaluatorPolicy");
        Assert.True(schema.Evaluate(manifest).IsValid);
        manifest["definitions"]![0]!["dataSlots"]!.AsArray().Add("executeCode");
        Assert.False(schema.Evaluate(manifest).IsValid);
    }

    [Theory]
    [InlineData("redTeamVerdictV1")]
    [InlineData("gateDecisionV1")]
    [InlineData("redTeamLikertV1")]
    [InlineData("redTeamExplanationV1")]
    public async Task MetadataSchema_RedTeamPolicy_RejectsUnsupportedScaleAndReviewMetadata(string contract)
    {
        var schema = JsonSchema.FromText(await File.ReadAllTextAsync(Path.Combine(Root(), "docs", "schemas", "judge-prompt-pack.schema.json")));
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(Root(), "samples", "AgentEval.Samples", "PromptPacks", "sv-SE", "prompts.json")))!;
        var definition = manifest["definitions"]![0]!;
        definition["responseContract"] = contract;
        definition["scale"] = "unit";
        definition["criteria"] = new JsonArray();
        definition["severityBands"] = new JsonArray();
        definition["dimension"] = null;
        definition["passAt"] = contract == "redTeamExplanationV1" ? 0 : .75;
        definition["reviewAt"] = contract == "redTeamLikertV1" ? JsonValue.Create(.5) : null;
        Assert.True(schema.Evaluate(manifest).IsValid);
        definition["scale"] = "percent";
        Assert.False(schema.Evaluate(manifest).IsValid);
        definition["scale"] = "unit";
        definition["reviewAt"] = contract == "redTeamLikertV1" ? null : JsonValue.Create(.5);
        Assert.False(schema.Evaluate(manifest).IsValid);
    }
}
