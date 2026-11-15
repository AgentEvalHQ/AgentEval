// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.
using AgentEval.Core.Reporting;
using AgentEval.Evals;
using AgentEval.Reporting;
using AgentEval.Rendering.Pdf;

namespace AgentEval.Evals.Agentic.Reporting.Pdf;

/// <summary>Renders rich agentic reports through an explicitly registered PDF provider.</summary>
public sealed class AgenticPdfRenderer(IPdfRendererRegistry? pdfRenderers = null, string? providerId = null)
{
    /// <summary>Preserves the original construction signature. Rendering requires an explicitly supplied registry.</summary>
    public AgenticPdfRenderer() : this(null, null) { }

    /// <summary>Explicitly renders a PDF through the selected provider and atomically writes the destination.</summary>
    public Task RenderAsync(AgenticBenchmarkResult result, string outputPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ct.ThrowIfCancellationRequested();
        return PdfReportFiles.RenderAsync(PdfReportFiles.RequireProvider(pdfRenderers, providerId), CreateDocument(result), outputPath, cancellationToken: ct);
    }

    /// <summary>Projects the recorded family summary, evaluator details, methodology and custody facts.</summary>
    public ReportDocument CreateDocument(AgenticBenchmarkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var generic = EvalReportProjection.Create(result.CompositeTree, new(result.Subject,
            Title: "Agentic Benchmark — " + FormatPresetTitle(result.Preset), RunId: result.SourceRunId,
            GeneratedAt: result.GeneratedAt, AuditHash: result.SourceManifestHash, AgentEvalVersion: result.Attestation.AgentEvalVersion));
        var sections = new List<ReportSection>
        {
            new(generic.Title, [$"Subject: {result.Subject.Name} ({result.Subject.Kind})", $"Run ID: {result.SourceRunId}",
                $"Generated: {result.GeneratedAt:O}", $"OVERALL: {result.Summary.OverallStatus} (score {result.Summary.OverallScoreText()})", result.Disclaimer]),
            new("Executive Summary", tables: [new(["Category", "Score", "Status", "Critical fails"],
                result.Summary.PerCategory.Select(p => new[] { p.Key, ScoreFormatting.Percentage(p.Value.Score), p.Value.Status, string.Join(", ", p.Value.CriticalFails) }))]),
            new("Per-evaluator", tables: [new(["Evaluator", "Score", "Status", "Severity", "Confidence"],
                result.Summary.PerEvaluator.Select(p => new[] { p.Key, ScoreFormatting.Percentage(p.Value.Score), p.Value.Status, p.Value.Severity, p.Value.Confidence?.ToString("P0") ?? "n/a" }))]),
            new("Critical findings", result.CriticalFindings.Select(f => $"{f.Metric.Key}: {ScoreFormatting.Percentage(f.Score.MeasuredValue())}; severity {f.Score.Severity}")),
            new("Recommendations", result.Recommendations),
        };
        sections.AddRange(generic.Sections.Skip(1));
        var audit = new List<string> { $"Source run ID: {result.SourceRunId}", $"Manifest hash: {result.SourceManifestHash}",
            "Schema version: 1.0", $"AgentEval version: {result.Attestation.AgentEvalVersion}", $"Judge mode: {result.Attestation.JudgeMode}" };
        audit.AddRange(result.Attestation.PromptVersions.Select(p => $"{p.Key}: {p.Value}"));
        sections.Add(new("Appendix A: Audit Chain", audit));
        sections.Add(Methodology(result));
        return new(generic.Title, sections, result.GeneratedAt, result.Subject.Name, "AgentEval");
    }

    private static ReportSection Methodology(AgenticBenchmarkResult result)
    {
        var paragraphs = new List<string>();
        paragraphs.Add("Appendix B: Methodology");
        paragraphs.Add($"Preset: {FormatPresetTitle(result.Preset)} Preset");
        paragraphs.Add(GetPresetDescription(result.Preset));
        paragraphs.Add("Judge Mode");
        paragraphs.Add(GetJudgeModeDescription(result.Attestation.JudgeMode));
        paragraphs.Add("Evaluation Categories");
        paragraphs.Add("The agentic benchmark evaluates agent behavior across up to seven categories:\n" +
                "  system-outcome — Did the agent achieve the intended task goal?\n" +
                "  agentic-process — Were agentic reasoning steps, tool calls, and planning appropriate?\n" +
                "  rag — Retrieval-Augmented Generation quality: relevance, grounding, and citation accuracy.\n" +
                "  safety-security — Did the agent avoid unsafe, harmful, or policy-violating outputs?\n" +
                "  operational — Latency, cost, retry, and reliability characteristics.\n" +
                "  judge-quality — Meta-evaluation of the judge's own scoring consistency.\n" +
                "  agentic — Top-level composite rollup (present in flat presets).\n\n" +
                "Overall score is the weighted composite across all active categories and evaluators. " +
                "Pass/warn/fail thresholds are preset-specific (see preset description above).");
        paragraphs.Add("Severity Tiers");
        paragraphs.Add("critical — the failure represents a significant, potentially harmful deviation " +
                "from expected agent behavior (e.g. safety violations, complete task failure).\n" +
                "high — important gap requiring remediation before production deployment.\n" +
                "medium — notable gap; acceptable for early-stage evaluation but should be addressed.\n" +
                "low — minor issue; recommend improvement but does not block deployment.\n" +
                "none — informational; no behavioral impact.");
        paragraphs.Add("Prompt Provenance");
        paragraphs.Add("Evaluator prompt files are AgentEval's own text, under AgentEval's MIT license. About half " +
                "are modelled on the evaluator concepts (name, inputs and scoring dimensions) of the Azure AI " +
                "Evaluation SDK (azure-sdk-for-python); none reproduces upstream prompt text. Each prompt " +
                "file's header records its lineage and how it differs from the upstream evaluator. Each " +
                "judge-backed check sends its file as the judge's system prompt and reads the reply on the " +
                "file's own scale and bands; the versions below are the files each check sent.");
        paragraphs.Add("Prompt Versions");
        foreach (var kv in result.Attestation.PromptVersions)
        paragraphs.Add($"  {kv.Key}: {kv.Value}");
        paragraphs.Add("Disclaimer");
        paragraphs.Add(result.Disclaimer);
        return new("Appendix B: Methodology", paragraphs.Skip(1));
    }

    private static string FormatPresetTitle(string preset)
    {
        if (string.IsNullOrEmpty(preset)) return preset;
        // Convert kebab-case to Title Case: "agentic-execution" → "Agentic-Execution"
        return string.Join("-", preset.Split('-')
            .Select(part => part.Length > 0
                ? char.ToUpperInvariant(part[0]) + part[1..]
                : part));
    }

    private static string GetPresetDescription(string preset) => preset.ToLowerInvariant() switch
    {
        "agentic-execution" =>
            "The Agentic-Execution preset is the standard agentic benchmark, covering the six core " +
            "evaluation dimensions: system-outcome, agentic-process, RAG quality, safety-security, " +
            "operational, and judge-quality. It is the recommended preset for development-phase " +
            "behavioral regression testing. Pass threshold is 0.80 (80%).",
        "tool-call-accuracy" =>
            "The Tool-Call-Accuracy preset focuses on the precision and recall of tool invocations: " +
            "whether the agent calls the correct tools with correctly-formed parameters, handles " +
            "errors gracefully, and avoids spurious or missing tool calls. Pass threshold is 0.85.",
        "safety" =>
            "The Safety preset is focused exclusively on safety-security category evaluators. " +
            "It provides a fast, targeted safety gate for CI pipelines. Pass threshold is 0.90.",
        "rag-quality" =>
            "The RAG-Quality preset targets retrieval-augmented generation evaluators: context " +
            "relevance, groundedness, answer relevance, and citation accuracy. Pass threshold is 0.85.",
        "judge-quality" =>
            "The Judge-Quality preset is a meta-evaluation preset that assesses the internal " +
            "consistency and calibration of the LLM judge itself. Pass threshold is 0.85.",
        _ =>
            $"The {FormatPresetTitle(preset)} preset covers the configured evaluator set for agentic " +
            "benchmark assessment. Refer to the benchmark configuration for preset-specific thresholds " +
            "and aggregation rules."
    };

    internal static string GetJudgeModeDescription(string judgeMode) => judgeMode.ToLowerInvariant() switch
    {
        "none" =>
            "No judge: every evaluator in this preset is computed in code from the answer, trace or telemetry; no " +
            "language model graded anything.",
        "single" or "mode-a" or "mode-b" or "real" or "llm" =>
            "Single LLM judge mode: each scenario is evaluated by a single live language model call " +
            "configured via the benchmark's judge pipeline. Results reflect genuine model behavior " +
            "against each scenario's evaluation criteria.",
        "panel" =>
            "Panel judge mode: multiple independent LLM calls are made per scenario and their verdicts " +
            "are aggregated. Provides higher reliability at increased cost.",
        "adjudicated" =>
            "Adjudicated judge mode: a panel of judges is convened and disagreements are resolved by a " +
            "dedicated adjudicator model. Provides the highest fidelity evaluation.",
        _ =>
            $"Judge mode: {judgeMode}. Refer to benchmark configuration for details on the evaluation pipeline."
    };
}
