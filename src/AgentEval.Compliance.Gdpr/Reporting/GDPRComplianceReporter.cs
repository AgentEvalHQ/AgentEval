// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentEval.Evals;
using AgentEval.Compliance.Gdpr.Articles;
using AgentEval.Output;
using Json.Schema;

namespace AgentEval.Compliance.Gdpr.Reporting;

/// <summary>
/// Generates a GDPR compliance evidence document from a completed benchmark run,
/// persists the plan-01 <see cref="ComplianceEvidence"/> via the output store's
/// audit-chain validation, and writes a sibling <c>gdpr-evidence.json</c> file
/// when the store is filesystem-backed.
/// </summary>
public sealed class GDPRComplianceReporter
{
    /// <summary>The regulation string used in all compliance evidence documents.</summary>
    public const string Regulation = "GDPR";

    /// <summary>
    /// Required disclaimer that must appear in every generated evidence document and Markdown report.
    /// </summary>
    public const string Disclaimer =
        "This benchmark evaluates AI-agent dialog behavior against GDPR articles. " +
        "It is a first-line screening tool, not a legal compliance attestation. " +
        "A passing run does not establish GDPR compliance; full compliance requires DPO sign-off, " +
        "organizational audits, and architectural verification beyond agent dialog. " +
        "Use this evidence as one input into a larger compliance program.";

    private readonly SummaryBuilder _summaryBuilder;
    private readonly CriticalFindingExtractor _criticalExtractor;
    private readonly RecommendationExtractor _recExtractor;
    private readonly ArticlesRegistry _articles;

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Initialises a new <see cref="GDPRComplianceReporter"/>.</summary>
    /// <param name="articles">Registry used to look up article metadata for evidence controls.</param>
    public GDPRComplianceReporter(ArticlesRegistry articles)
    {
        _articles = articles ?? throw new ArgumentNullException(nameof(articles));
        _summaryBuilder = new SummaryBuilder(articles);
        _criticalExtractor = new CriticalFindingExtractor();
        _recExtractor = new RecommendationExtractor();
    }

    /// <summary>
    /// Builds and persists a <see cref="GdprComplianceEvidence"/> document from
    /// <paramref name="compositeTree"/>, then returns the in-memory evidence record.
    /// </summary>
    /// <param name="store">Output store that receives the audit-chained compliance evidence.</param>
    /// <param name="subject">Identity of the agent or workflow that was evaluated.</param>
    /// <param name="sourceRunId">Run ID of the benchmark run that produced <paramref name="compositeTree"/>.</param>
    /// <param name="compositeTree">The top-level composite <see cref="EvalResult"/> from the runner.</param>
    /// <param name="options">Optional report options; defaults are used when null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The fully populated <see cref="GdprComplianceEvidence"/>.</returns>
    public async Task<GdprComplianceEvidence> SaveReportAsync(
        IOutputStore store,
        SubjectIdentity subject,
        string sourceRunId,
        EvalResult compositeTree,
        GdprReportOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRunId);
        ArgumentNullException.ThrowIfNull(compositeTree);
        options ??= new GdprReportOptions();

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
                AgentEvalVersion: typeof(GDPRComplianceReporter).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                ConfigurationId: null,
                Evaluator: "AgentEval.Compliance.Gdpr",
                // EvaluatorModel records the actual judge deployment name when the JudgeFactory
                // resolved an Azure OpenAI judge; "stub" when the operator opted into the stub
                // via AGENTEVAL_ALLOW_STUB_JUDGE; "internal" only as a legacy fallback when no
                // JudgeModel was provided (kept for backward compat with callers pre-pass-3).
                EvaluatorModel: options.JudgeModel ?? "internal"));

        await store.SaveComplianceEvidenceAsync(Regulation, subject, baseEvidence, ct);

        // 2) GDPR-specific wrapper evidence.
        var gdprEvidence = new GdprComplianceEvidence(
            Base: baseEvidence,
            Preset: options.Preset,
            DomainPacks: options.DomainPacks ?? Array.Empty<string>(),
            CompositeTree: compositeTree,
            Summary: summary,
            CriticalFindings: critical,
            Recommendations: recs,
            Disclaimer: Disclaimer,
            GdprAttestation: new GdprAttestation(
                JudgeMode: options.JudgeMode,
                PromptVersions: options.PromptVersions ?? new Dictionary<string, string>
                {
                    ["gdpr-judge-system"] = "v1",
                    ["per-criterion"] = "v1"
                }));

        await WriteGdprEvidenceFileAsync(store, subject, baseEvidence.GeneratedAt, gdprEvidence, ct);
        return gdprEvidence;
    }

    private async Task WriteGdprEvidenceFileAsync(
        IOutputStore store,
        SubjectIdentity subject,
        DateTimeOffset ts,
        GdprComplianceEvidence evidence,
        CancellationToken ct)
    {
        // Validate against the embedded gdpr-evidence.schema.json BEFORE persisting.
        // (Plan 03 G4.3 acceptance: "A fixture gdpr-evidence.json validates against this schema.")
        // This catches shape regressions even on non-filesystem stores; throws if the schema check fails
        // so a malformed wrapper never reaches disk.
        ValidateAgainstGdprEvidenceSchema(evidence);

        // Re-derive the compliance/{regulation}/{subject}/{ts}/ folder used by SaveComplianceEvidenceAsync.
        // Uses the same sanitization rule as FileSystemLayout.Sanitize.
        if (store is FileSystemOutputStore fsStore)
        {
            var root = fsStore.WorkspaceRoot
                ?? throw new InvalidOperationException("FileSystemOutputStore has no workspace root.");
            var sanitizedSubject = SanitizeForPath(subject.Name);
            var tsString = ts.ToString("yyyy-MM-dd_HH-mm-ss");
            var dir = Path.Combine(root, "compliance", Regulation, sanitizedSubject, tsString);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "gdpr-evidence.json");

            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, evidence, s_jsonOpts, ct);
        }
        // For non-filesystem stores (InMemory, Null), the wrapper is held in memory by the caller's
        // returned GdprComplianceEvidence value. Tests that want to inspect the JSON serialise it themselves.
    }

    private static JsonSchema? s_cachedGdprSchema;

    private static void ValidateAgainstGdprEvidenceSchema(GdprComplianceEvidence evidence)
    {
        var schema = LoadGdprEvidenceSchema();
        var json = JsonSerializer.Serialize(evidence, s_jsonOpts);
        var node = JsonNode.Parse(json);
        var result = schema.Evaluate(node, new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List });
        if (!result.IsValid)
        {
            var errors = string.Join("; ", result.Details
                .Where(d => !d.IsValid)
                .SelectMany(d => d.Errors?.Select(e => $"{d.EvaluationPath} {e.Key}={e.Value}") ?? Array.Empty<string>()));
            throw new InvalidOperationException($"gdpr-evidence.json failed schema validation: {errors}");
        }
    }

    private static JsonSchema LoadGdprEvidenceSchema()
    {
        if (s_cachedGdprSchema is not null) return s_cachedGdprSchema;
        var asm = typeof(GDPRComplianceReporter).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(".gdpr-evidence.schema.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        s_cachedGdprSchema = JsonSchema.FromText(reader.ReadToEnd());
        return s_cachedGdprSchema;
    }

    private static string SanitizeForPath(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\' }).ToArray();
        var s = string.Concat(name.Select(c => invalid.Contains(c) ? '-' : c));
        return s.Trim('.', ' ');
    }

    private IReadOnlyList<EvidenceControl> BuildEvidenceControls(GdprSummary summary)
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
