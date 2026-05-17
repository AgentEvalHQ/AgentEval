// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using AgentEval.Core;
using AgentEval.Evals;
using AgentEval.EuAiActBenchmark.Articles;
using AgentEval.EuAiActBenchmark.Articles.Building;
using AgentEval.EuAiActBenchmark.Articles.Loading;
using AgentEval.EuAiActBenchmark.Articles.Validation;
using AgentEval.EuAiActBenchmark.Reporting;
using AgentEval.Output;

QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

if (args.Length > 0 && args[0] == "smoke-load")
{
    // YAML smoke test — load all embedded YAMLs through the loader + validator,
    // build the registry, and print one line per article.
    var loader = new ArticleScenarioYamlLoader();
    var validator = new ArticleYamlValidator();
    var stub = new StubEvaluator();
    var scenarioBuilder = new ScenarioToAtomicEval(stub, judgeModel: "smoke");
    var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);

    var asm = typeof(EuAiActArticlesRegistry).Assembly;
    var specs = await loader.LoadAllFromAssemblyAsync(asm);
    Console.WriteLine($"Loaded {specs.Count} article spec(s) from assembly.");

    var anyInvalid = false;
    foreach (var spec in specs.OrderBy(s => s.Metadata.ControlId, StringComparer.Ordinal))
    {
        var v = validator.Validate(spec);
        var status = v.IsValid ? "OK" : "FAIL";
        Console.WriteLine($"  [{status}] {spec.Metadata.ControlId,-45} ({spec.Scenarios.Count} scenarios, pillar={spec.Metadata.Pillar})");
        if (!v.IsValid)
        {
            Console.WriteLine($"        errors: {v.ErrorsJoined}");
            anyInvalid = true;
        }
    }

    var registry = new EuAiActArticlesRegistry(loader, articleBuilder);
    Console.WriteLine($"Registry built with {registry.All.Count} composite(s).");

    // Verify the top-level presets construct without throwing (catches missing IDs early).
    var standard = AgentEval.Benchmarks.EuAiActBenchmark.Standard(registry);
    var smoke    = AgentEval.Benchmarks.EuAiActBenchmark.Smoke(registry);
    var audit    = AgentEval.Benchmarks.EuAiActBenchmark.AuditGrade(registry);
    Console.WriteLine($"Standard preset: {standard.Components.Count} components.");
    Console.WriteLine($"Smoke preset:    {smoke.Components.Count} components.");
    Console.WriteLine($"AuditGrade:      {audit.Components.Count} components.");

    return anyInvalid ? 1 : 0;
}

if (args.Length > 0 && args[0] == "smoke-run")
{
    // End-to-end smoke run: stub judge, InMemory store, Smoke preset, build evidence + Markdown.
    var loader = new ArticleScenarioYamlLoader();
    var stub = new StubEvaluator();
    var scenarioBuilder = new ScenarioToAtomicEval(stub, judgeModel: "smoke");
    var articleBuilder = new ArticleCompositeBuilder(scenarioBuilder);
    var registry = new EuAiActArticlesRegistry(loader, articleBuilder);

    var benchmark = AgentEval.Benchmarks.EuAiActBenchmark.Smoke(registry);

    var store = new InMemoryOutputStore();
    store.Initialize("smoke-sln");
    var subject = new SubjectIdentity(SubjectKind.Agent, "smoke-subject");
    await store.EnsureSolutionAsync();
    await store.EnsureSubjectAsync(subject);

    var runner = new EuAiActBenchmarkRunner();
    var (runId, result) = await runner.RunAsync(
        store, subject, benchmark,
        new EvalInput(Query: "What is your purpose?", Response: "I'm an AI assistant. I can help with task automation."));

    var reporter = new EuAiActComplianceReporter(registry);
    var evidence = await reporter.SaveReportAsync(store, subject, runId, result, new EuAiActReportOptions(Preset: "smoke"));

    var md = new MarkdownRenderer().Render(evidence);
    Console.WriteLine($"Smoke run complete. RunId={runId}");
    Console.WriteLine($"  Overall: {evidence.Summary.OverallStatus} (score {evidence.Summary.OverallScore:P0})");
    Console.WriteLine($"  Articles: {evidence.Summary.PerArticle.Count}, critical findings: {evidence.CriticalFindings.Count}, recs: {evidence.Recommendations.Count}");
    Console.WriteLine($"  Markdown report: {md.Length} chars");
    return 0;
}

Console.WriteLine("AgentEval.EuAiActBenchmark — run via `agenteval bench eu-ai-act`.");
Console.WriteLine("Smoke tests:");
Console.WriteLine("  dotnet run -- smoke-load   (validate YAMLs)");
Console.WriteLine("  dotnet run -- smoke-run    (end-to-end with stub judge)");
return 0;

internal sealed class StubEvaluator : IEvaluator
{
    public Task<EvaluationResult> EvaluateAsync(
        string input, string output, IEnumerable<string> criteria,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new EvaluationResult { OverallScore = 75, Summary = "stub" });
}
