// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentEval.Evals;
using AgentEval.EuAiActBenchmark.Articles;
using AgentEval.Output;
using Json.Schema;

namespace AgentEval.EuAiActBenchmark.Reporting;

/// <summary>
/// Generates an EU AI Act compliance evidence document from a completed benchmark run,
/// persists the plan-01 <see cref="ComplianceEvidence"/> via the output store's
/// audit-chain validation, and writes a sibling <c>eu-ai-act-evidence.json</c> file
/// when the store is filesystem-backed.
/// </summary>
public sealed class EuAiActComplianceReporter
{
    /// <summary>The regulation string used in all compliance evidence documents.</summary>
    public const string Regulation = "EU-AI-Act";

    /// <summary>
    /// Required disclaimer that must appear in every generated evidence document and Markdown report.
    /// </summary>
    public const string Disclaimer =
        "This benchmark evaluates AI-agent dialog behavior against EU AI Act articles " +
        "(Regulation (EU) 2024/1689). " +
        "It does not assess your organization's risk classification, conformity assessment, " +
        "technical documentation, post-market monitoring, registration in the EU database, " +
        "incident reporting workflow, or any other organizational/architectural control. " +
        "A passing run does not constitute legal compliance attestation under the AI Act. " +
        "Use this evidence as one input into a larger compliance program.";

    private readonly SummaryBuilder _summaryBuilder;
    private readonly CriticalFindingExtractor _criticalExtractor;
    private readonly RecommendationExtractor _recExtractor;
    private readonly EuAiActArticlesRegistry _articles;

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Initialises a new <see cref="EuAiActComplianceReporter"/>.</summary>
    /// <param name="articles">Registry used to look up article metadata for evidence controls.</param>
    public EuAiActComplianceReporter(EuAiActArticlesRegistry articles)
    {
        _articles = articles ?? throw new ArgumentNullException(nameof(articles));
        _summaryBuilder = new SummaryBuilder(articles);
        _criticalExtractor = new CriticalFindingExtractor();
        _recExtractor = new RecommendationExtractor();
    }

    /// <summary>
    /// Builds and persists an <see cref="EuAiActComplianceEvidence"/> document from
    /// <paramref name="compositeTree"/>, then returns the in-memory evidence record.
    /// </summary>
    /// <param name="store">Output store that receives the audit-chained compliance evidence.</param>
    /// <param name="subject">Identity of the agent or workflow that was evaluated.</param>
    /// <param name="sourceRunId">Run ID of the benchmark run that produced <paramref name="compositeTree"/>.</param>
    /// <param name="compositeTree">The top-level composite <see cref="EvalResult"/> from the runner.</param>
    /// <param name="options">Optional report options; defaults are used when null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The fully populated <see cref="EuAiActComplianceEvidence"/>.</returns>
    public async Task<EuAiActComplianceEvidence> SaveReportAsync(
        IOutputStore store,
        SubjectIdentity subject,
        string sourceRunId,
        EvalResult compositeTree,
        EuAiActReportOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRunId);
        ArgumentNullException.ThrowIfNull(compositeTree);
        options ??= new EuAiActReportOptions();

        var sourceManifest = await store.GetRunManifestAsync(sourceRunId, ct)
            ?? throw new InvalidOperationException($"Source run {sourceRunId} not found.");

        var summary = _summaryBuilder.Build(compositeTree);
        var critical = _criticalExtractor.Find(compositeTree);
        var recs = _recExtractor.Build(compositeTree);

        // 1) Standard plan-01 evidence (audit-chain validated by the store).
        var baseEvidence = new ComplianceEvidence(
            SchemaVersion: "1.0",
            Regulation: Regulation,
            Subject: subject,
            GeneratedAt: DateTimeOffset.UtcNow,
            SourceRun: new SourceRunRef(sourceManifest.Run.RunId, sourceManifest.ContentHash),
            Controls: BuildEvidenceControls(summary),
            Summary: new EvidenceSummary(
                ControlsTotal: summary.PerArticle.Count,
                Passed: summary.PerArticle.Count(p => p.Value.Status == "PASS"),
                Warnings: summary.PerArticle.Count(p => p.Value.Status == "WARN"),
                Failed: summary.PerArticle.Count(p => p.Value.Status == "FAIL"),
                OverallStatus: summary.OverallStatus),
            Attestation: new Attestation(
                AgentEvalVersion: typeof(EuAiActComplianceReporter).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                ConfigurationId: null,
                Evaluator: "AgentEval.EuAiActBenchmark",
                EvaluatorModel: "internal"));

        await store.SaveComplianceEvidenceAsync(Regulation, subject, baseEvidence, ct);

        // 2) EU AI Act-specific wrapper evidence.
        var euAiActEvidence = new EuAiActComplianceEvidence(
            Base: baseEvidence,
            Preset: options.Preset,
            CompositeTree: compositeTree,
            Summary: summary,
            CriticalFindings: critical,
            Recommendations: recs,
            Disclaimer: Disclaimer,
            EuAiActAttestation: new EuAiActAttestation(
                JudgeMode: options.JudgeMode,
                PromptVersions: options.PromptVersions ?? new Dictionary<string, string>
                {
                    ["eu-ai-act-judge-system"] = "v1",
                    ["per-criterion"] = "v1"
                }));

        await WriteEuAiActEvidenceFileAsync(store, subject, baseEvidence.GeneratedAt, euAiActEvidence, ct);
        return euAiActEvidence;
    }

    private async Task WriteEuAiActEvidenceFileAsync(
        IOutputStore store,
        SubjectIdentity subject,
        DateTimeOffset ts,
        EuAiActComplianceEvidence evidence,
        CancellationToken ct)
    {
        // Validate against the embedded eu-ai-act-evidence.schema.json BEFORE persisting.
        // This catches shape regressions even on non-filesystem stores; throws if the schema check fails
        // so a malformed wrapper never reaches disk.
        ValidateAgainstEuAiActEvidenceSchema(evidence);

        // Re-derive the compliance/{regulation}/{subject}/{ts}/ folder used by SaveComplianceEvidenceAsync.
        // Uses the same sanitization rule as FileSystemLayout.Sanitize.
        if (store is FileSystemOutputStore fsStore)
        {
            var root = fsStore.WorkspaceRoot
                ?? throw new InvalidOperationException("FileSystemOutputStore has no workspace root.");
            var sanitizedSubject = SanitizeForPath(subject.Name);
            var tsString = ts.ToString("yyyy-MM-dd_HH-mm-ss");
            var dir = Path.Combine(root, "compliance", "EU-AI-Act", sanitizedSubject, tsString);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "eu-ai-act-evidence.json");

            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, evidence, s_jsonOpts, ct);
        }
        // For non-filesystem stores (InMemory, Null), the wrapper is held in memory by the caller's
        // returned EuAiActComplianceEvidence value. Tests that want to inspect the JSON serialise it themselves.
    }

    private static JsonSchema? s_cachedEuAiActSchema;

    private static void ValidateAgainstEuAiActEvidenceSchema(EuAiActComplianceEvidence evidence)
    {
        var schema = LoadEuAiActEvidenceSchema();
        var json = JsonSerializer.Serialize(evidence, s_jsonOpts);
        var node = JsonNode.Parse(json);
        var result = schema.Evaluate(node, new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List });
        if (!result.IsValid)
        {
            var errors = string.Join("; ", result.Details
                .Where(d => !d.IsValid)
                .SelectMany(d => d.Errors?.Select(e => $"{d.EvaluationPath} {e.Key}={e.Value}") ?? Array.Empty<string>()));
            throw new InvalidOperationException($"eu-ai-act-evidence.json failed schema validation: {errors}");
        }
    }

    private static JsonSchema LoadEuAiActEvidenceSchema()
    {
        if (s_cachedEuAiActSchema is not null) return s_cachedEuAiActSchema;
        var asm = typeof(EuAiActComplianceReporter).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(".eu-ai-act-evidence.schema.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        s_cachedEuAiActSchema = JsonSchema.FromText(reader.ReadToEnd());
        return s_cachedEuAiActSchema;
    }

    private static string SanitizeForPath(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\' }).ToArray();
        var s = string.Concat(name.Select(c => invalid.Contains(c) ? '-' : c));
        return s.Trim('.', ' ');
    }

    private IReadOnlyList<EvidenceControl> BuildEvidenceControls(EuAiActSummary summary)
    {
        return summary.PerArticle.Select(kv =>
        {
            var spec = _articles.GetSpec(kv.Key);
            return new EvidenceControl(
                Id: kv.Key,
                Title: spec.Metadata.Title,
                Status: kv.Value.Status,
                PassRate: kv.Value.Score,
                ScenarioRefs: spec.Scenarios.Select(s => s.Id).ToList(),
                Notes: null);
        }).ToList();
    }
}
