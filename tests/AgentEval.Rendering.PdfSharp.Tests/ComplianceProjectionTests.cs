// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
using System.Text.Json;
using AgentEval.Compliance.Gdpr.Articles;
using AgentEval.Compliance.Gdpr.Articles.Building;
using AgentEval.Compliance.Gdpr.Articles.Loading;
using AgentEval.Compliance.Gdpr.Reporting;
using AgentEval.Compliance.Gdpr.Reporting.Pdf;
using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.Output;
using AgentEval.Rendering.Pdf;
using AgentEval.Reporting;
using Microsoft.Extensions.DependencyInjection;

namespace AgentEval.Rendering.PdfSharp.Tests;

public sealed class ComplianceProjectionTests
{
    [Fact]
    public async Task Render_MissingSensitivityMetadata_WithholdsTextBeforeCustomProvider()
    {
        var capture = new PdfProviderTests.CaptureProvider();
        using var host = new ServiceCollection().AddSingleton(capture).AddAgentEvalPdfProvider<PdfProviderTests.CaptureProvider>("customer").BuildServiceProvider();
        var path = Path.Combine(Path.GetTempPath(), "pdf-privacy-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            var renderer = new GDPRPdfRenderer(pdfRenderers: host.GetRequiredService<IPdfRendererRegistry>());
            await renderer.RenderAsync(Evidence("custom.article", "custom.scenario", "PRIVATE-SCENARIO-TEXT"), path);
            var text = JsonSerializer.Serialize(capture.Last);
            Assert.DoesNotContain("PRIVATE-SCENARIO-TEXT", text);
            Assert.Contains("custom.scenario", text);
            Assert.Contains("metadata unavailable", text);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Projection_KnownSafeAndSensitiveScenarios_AppliesDeclaredPolicy()
    {
        var registry = new ArticlesRegistry(new ArticleScenarioYamlLoader(), new ArticleCompositeBuilder(new ScenarioToAtomicEval(new NeverJudge(), "none")));
        foreach (var sensitive in new[] { false, true })
        {
            var selected = registry.All.Keys.Select(registry.GetSpec).SelectMany(spec => spec.Scenarios.Select(s => (spec, scenario: s)))
                .First(pair => pair.scenario.Sensitive == sensitive);
            var key = registry.All.Keys.First(k => ReferenceEquals(registry.GetSpec(k), selected.spec));
            var report = new GDPRPdfRenderer(registry).CreateDocument(Evidence(key, selected.scenario.Id, "EVIDENCE-PRIVATE-MARKER"));
            var text = JsonSerializer.Serialize(report);
            if (sensitive)
            {
                Assert.Contains("REDACTED", text);
                Assert.DoesNotContain("EVIDENCE-PRIVATE-MARKER", text);
            }
            else
            {
                Assert.Contains("EVIDENCE-PRIVATE-MARKER", text);
                Assert.Contains("Input preview", text);
            }
        }
    }

    private static GdprComplianceEvidence Evidence(string article, string scenario, string evidence)
    {
        var time = DateTimeOffset.UtcNow;
        EvalResult Node(string key, IReadOnlyList<EvalResult>? children, string? text = null) => new(new(key, key, "compliance", "1"),
            new(0.9, null, "pass", true, 0.8, "none", null), new(null, text is null ? null : [new("test", "secret-reference", text)], null, children, "WeightedSum"),
            new("code", null, null, null, null, 0, false), time);
        var root = Node("gdpr", [Node(article, [Node(scenario, null, evidence)])]);
        var basic = new ComplianceEvidence("1.0", "GDPR", new(SubjectKind.Agent, "subject"), time,
            new("run", "sha256:" + new string('a', 64)), [], new(1, 1, 0, 0, "PASS"), new("test", null, "test", "none"));
        return new(basic, "smoke", [], root, new(0.9, "PASS", new Dictionary<string, GdprPillarSummary>(),
            new Dictionary<string, GdprArticleSummary> { [article] = new(0.9, "PASS", 1, 0, "none") }), [], [], "Disclaimer",
            new("none", new Dictionary<string, string>()));
    }

    private sealed class NeverJudge : IEvaluator
    {
        public Task<EvaluationResult> EvaluateAsync(string input, string output, IEnumerable<string> criteria, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Rendering must not evaluate anything.");
    }
}
