// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Text.Json;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Evals.Agentic.Quality;
using AgentEval.Prompts;
using AgentEval.Testing;
using Xunit;
using AgenticCalibration = AgentEval.Evals.Agentic.Calibration;
using Gdpr = AgentEval.Compliance.Gdpr;
using Eu = AgentEval.Compliance.EuAiAct;

namespace AgentEval.Tests.Prompts;

public sealed class CalibrationPromptTests
{
    [Fact]
    public async Task AgenticCalibration_LocalizedInstrument_MatchesEvaluationAndRetainsContentFreeRecords()
    {
        const string reply = """{"score":1,"reasoning":"Fixture"}""";
        using var client = new FakeChatClient(reply, reply, reply);
        var eval = new RelevanceEval(new ChatClientEvaluator(client));
        var definition = Assert.Single(BuiltInJudgePrompts.ForEval(eval)) with { Locale = "sv-SE", SystemText = "Private fixture rubric", PassAt = .95 };
        var options = new JudgePromptOptions { Locale = "sv-SE", Override = definition, CaptureContent = true };
        var entry = new AgenticCalibration.CalibrationEntry("fixture", "relevance", "Private question", "Private answer", "pass", .9, 1, "AI-authored fixture");
        var ordinary = await eval.EvaluateAsync(entry.ToEvalInput() with { JudgePrompts = options });
        var records = new List<AgenticCalibration.CalibrationCaseRecord>();
        var resolutions = 0;
        var runner = new AgenticCalibration.CalibrationRunner(_ => { resolutions++; return eval; }) { JudgePrompts = options };
        var report = await runner.RunAsync([new("fixture", [entry, entry with { ScenarioId = "repeat" }])],
            (record, _) => { records.Add(record); return Task.CompletedTask; }, null);
        Assert.Equal(1, resolutions);
        Assert.Equal(2, report.PerCategory["fixture"].EntryCount);
        Assert.All(records, record =>
        {
            var leaf = Assert.Single(record.Leaves);
            Assert.Equal(ordinary.Provenance.JudgePrompt!.Digest, leaf.JudgePrompt!.Digest);
            Assert.Equal(leaf.JudgePrompt.Digest, leaf.RubricDigest);
            Assert.Equal("sv-SE", leaf.JudgePrompt.EffectiveLocale);
            Assert.Null(leaf.JudgePrompt.Content);
            Assert.DoesNotContain("Private", JsonSerializer.Serialize(record));
        });
    }

    [Fact]
    public async Task AgenticCalibration_LaterMissingInstrument_RejectsBeforeFirstJudgeCall()
    {
        using var client = new FakeChatClient();
        var judge = new ChatClientEvaluator(client);
        var relevance = new RelevanceEval(judge);
        var coherence = new CoherenceEval(judge);
        var definition = Assert.Single(BuiltInJudgePrompts.ForEval(relevance)) with { Locale = "sv-SE" };
        var runner = new AgenticCalibration.CalibrationRunner(key => key == "relevance" ? relevance : coherence)
        { JudgePrompts = new() { Locale = "sv-SE", Pack = new([definition]) } };
        var entry = new AgenticCalibration.CalibrationEntry("fixture", "relevance", "question", "answer", "pass", .9, 1, "AI-authored fixture");
        await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync([
            new("first", [entry]), new("later", [entry with { EvaluatorKey = "coherence" }]) ]));
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task AgenticCalibration_UndispatchedCase_RemainsInCaseRecords()
    {
        var entry = new AgenticCalibration.CalibrationEntry("fixture", "unknown", "question", "answer", "pass", .9, 1, "AI-authored fixture");
        var records = new List<AgenticCalibration.CalibrationCaseRecord>();
        var report = await new AgenticCalibration.CalibrationRunner(_ => null).RunAsync([new("fixture", [entry])],
            (record, _) => { records.Add(record); return Task.CompletedTask; }, null);
        Assert.Equal(1, report.PerCategory["fixture"].SkippedUnknownKey);
        var record = Assert.Single(records);
        Assert.Null(record.Value);
        Assert.Null(record.Passed);
        Assert.Contains("No evaluator dispatched", record.Error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComplianceCalibration_ProductionCase_RetainsCanonicalInstrumentAndLocale(bool eu)
    {
        using var client = new FakeChatClient();
        var fixture = CreateCompliance(eu, new ChatClientEvaluator(client));
        var definition = fixture.Definition with { Locale = "sv-SE", SystemText = "Private fixture rubric", PassAt = .93 };
        client.WithResponse(JsonSerializer.Serialize(new { overallScore = 100, summary = "Fixture", improvements = Array.Empty<string>(),
            criteriaResults = definition.Criteria.Select(c => new { id = c.Id, met = true, explanation = "Fixture" }) }));
        var options = new JudgePromptOptions { Locale = "sv-SE", Override = definition, CaptureContent = true };
        var outcome = await fixture.Run(options, true, false);
        var prompt = Assert.Single(outcome.Instruments);
        Assert.Equal(definition.EvaluatorKey, prompt.EvaluatorKey);
        Assert.Equal(new JudgePromptResolver().Resolve(fixture.Definition, options).Provenance.Digest, prompt.Digest);
        Assert.Equal("sv-SE", prompt.EffectiveLocale);
        Assert.Null(prompt.Content);
        Assert.Equal(1, outcome.Production);
        Assert.Equal(0, outcome.Surrogate);
        Assert.Equal(1, client.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComplianceCalibration_StrictProduction_RejectsLaterSurrogateBeforeAnyCall(bool eu)
    {
        using var client = new FakeChatClient();
        var fixture = CreateCompliance(eu, new ChatClientEvaluator(client));
        var error = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Run(null, true, true));
        Assert.Contains("surrogate", error.Message);
        Assert.Equal(0, client.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ComplianceCalibration_LegacyMixedCases_DisclosesSurrogatesAndUnknownArticles(bool eu)
    {
        using var client = new FakeChatClient("""{"overallScore":90,"summary":"Fixture","criteriaResults":[],"improvements":[]}""");
        var outcome = await CreateCompliance(eu, new ChatClientEvaluator(client)).Run(null, false, true);
        Assert.Equal(1, outcome.Production);
        Assert.Equal(1, outcome.Surrogate);
        Assert.Equal(1, outcome.Unknown);
        Assert.True(outcome.NotMeasured >= 1);
    }

    private sealed record ComplianceOutcome(IReadOnlyList<JudgePromptProvenance> Instruments, int Production, int Surrogate, int Unknown, int NotMeasured);
    private sealed record ComplianceFixture(JudgePromptDefinition Definition, Func<JudgePromptOptions?, bool, bool, Task<ComplianceOutcome>> Run);

    private static ComplianceFixture CreateCompliance(bool eu, IEvaluator judge)
    {
        if (eu)
        {
            var registry = new Eu.Articles.EuAiActArticlesRegistry(new Eu.Articles.Loading.ArticleScenarioYamlLoader(),
                new Eu.Articles.Building.ArticleCompositeBuilder(new Eu.Articles.Building.ScenarioToAtomicEval(judge)));
            var spec = registry.GetSpec(registry.All.Keys.First());
            var entry = new Eu.Calibration.CalibrationEntry(spec.Scenarios[0].Id.ToUpperInvariant(), spec.Metadata.ControlId,
                "question", "answer", "pass", .9, 1, "AI-authored fixture");
            var descriptor = Assert.Single(BuiltInJudgePrompts.ForEval(new Eu.Calibration.CalibrationRunner(registry, judge).CreateEvaluator(entry)!));
            Assert.Equal(JsonSerializer.Serialize(BuiltInJudgePrompts.ForEval(registry.All[spec.Metadata.ControlId])
                .Single(d => d.EvaluatorKey == spec.Scenarios[0].Id)), JsonSerializer.Serialize(descriptor));
            return new(descriptor, async (options, strict, mixed) =>
            {
                var entries = mixed ? new[] { entry, entry with { ScenarioId = "surrogate-fixture" }, entry with { ArticleControlId = "unknown" } } : [entry];
                var report = await new Eu.Calibration.CalibrationRunner(registry, judge)
                { JudgePrompts = options, RequireProductionScenarios = strict }.RunAsync([new("fixture", entries)]);
                var p = report.PerPillar["fixture"];
                return new(report.Instruments, p.ProductionEntries, p.SurrogateEntries, p.UnknownArticleEntries, p.NotMeasured);
            });
        }
        else
        {
            var registry = new Gdpr.Articles.ArticlesRegistry(new Gdpr.Articles.Loading.ArticleScenarioYamlLoader(),
                new Gdpr.Articles.Building.ArticleCompositeBuilder(new Gdpr.Articles.Building.ScenarioToAtomicEval(judge)));
            var spec = registry.GetSpec(registry.All.Keys.First());
            var entry = new Gdpr.Calibration.CalibrationEntry(spec.Scenarios[0].Id.ToUpperInvariant(), spec.Metadata.ControlId,
                "question", "answer", "pass", .9, 1, "AI-authored fixture");
            var descriptor = Assert.Single(BuiltInJudgePrompts.ForEval(new Gdpr.Calibration.CalibrationRunner(registry, judge).CreateEvaluator(entry)!));
            Assert.Equal(JsonSerializer.Serialize(BuiltInJudgePrompts.ForEval(registry.All[spec.Metadata.ControlId])
                .Single(d => d.EvaluatorKey == spec.Scenarios[0].Id)), JsonSerializer.Serialize(descriptor));
            return new(descriptor, async (options, strict, mixed) =>
            {
                var entries = mixed ? new[] { entry, entry with { ScenarioId = "surrogate-fixture" }, entry with { ArticleControlId = "unknown" } } : [entry];
                var report = await new Gdpr.Calibration.CalibrationRunner(registry, judge)
                { JudgePrompts = options, RequireProductionScenarios = strict }.RunAsync([new("fixture", entries)]);
                var p = report.PerPillar["fixture"];
                return new(report.Instruments, p.ProductionEntries, p.SurrogateEntries, p.UnknownArticleEntries, p.NotMeasured);
            });
        }
    }
}
