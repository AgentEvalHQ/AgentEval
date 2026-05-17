// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentEval.Evals;
using AgentEval.Output;
using Json.Schema;

namespace AgentEval.Evals.Agentic.Reporting;

/// <summary>
/// Generates an <see cref="AgenticBenchmarkResult"/> document from a completed agentic benchmark run,
/// validates it against the embedded <c>agentic-result.schema.json</c>, and persists it under
/// <c>.agenteval/benchmarks/agentic/{subject}/{ts}/agentic-result.json</c> when the store is
/// filesystem-backed.
/// </summary>
/// <remarks>
/// This reporter does NOT call <see cref="IOutputStore.SaveComplianceEvidenceAsync"/> — the agentic
/// benchmark result is an evaluation artifact, not a compliance evidence document.
/// The audit trail is maintained through the normal <c>WriteScenarioResultAsync</c> /
/// <c>CompleteRunAsync</c> run lifecycle, which the caller is responsible for completing
/// before invoking <see cref="SaveReportAsync"/>.
/// </remarks>
public sealed class AgenticBenchmarkReporter
{
    /// <summary>
    /// Required disclaimer that must appear in every generated agentic benchmark result document
    /// and Markdown report.
    /// </summary>
    public const string Disclaimer =
        "This evaluation result reflects an AI agent's behavior on the configured benchmark scenarios. " +
        "It is not a compliance attestation, certification, or production-ready quality guarantee. " +
        "Use it as one input alongside human review, monitoring, and domain testing.";

    private readonly AgenticSummaryBuilder _summaryBuilder;
    private readonly AgenticCriticalFindingExtractor _criticalExtractor;

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Initialises a new <see cref="AgenticBenchmarkReporter"/>.</summary>
    public AgenticBenchmarkReporter()
    {
        _summaryBuilder = new AgenticSummaryBuilder();
        _criticalExtractor = new AgenticCriticalFindingExtractor();
    }

    /// <summary>
    /// Builds and persists an <see cref="AgenticBenchmarkResult"/> document from
    /// <paramref name="compositeTree"/>, validates it against the embedded JSON schema,
    /// and returns the in-memory result record.
    /// </summary>
    /// <param name="store">Output store used to look up the source run manifest and write the result file.</param>
    /// <param name="subject">Identity of the agent or workflow that was evaluated.</param>
    /// <param name="sourceRunId">Run ID of the benchmark run that produced <paramref name="compositeTree"/>.</param>
    /// <param name="compositeTree">The top-level composite <see cref="EvalResult"/> from the runner.</param>
    /// <param name="options">Optional report options; defaults are used when null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The fully populated <see cref="AgenticBenchmarkResult"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when the source run manifest cannot be found, or when the generated JSON fails schema validation.
    /// </exception>
    public async Task<AgenticBenchmarkResult> SaveReportAsync(
        IOutputStore store,
        SubjectIdentity subject,
        string sourceRunId,
        EvalResult compositeTree,
        AgenticReportOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRunId);
        ArgumentNullException.ThrowIfNull(compositeTree);
        options ??= new AgenticReportOptions();

        var sourceManifest = await store.GetRunManifestAsync(sourceRunId, ct)
            ?? throw new InvalidOperationException($"Source run '{sourceRunId}' not found in the output store.");

        var summary = _summaryBuilder.Build(compositeTree);
        var critical = _criticalExtractor.Find(compositeTree);
        var recs = AgenticRecommendationExtractor.Build(compositeTree);
        var generatedAt = DateTimeOffset.UtcNow;

        var result = new AgenticBenchmarkResult(
            Preset: options.Preset,
            Subject: subject,
            SourceRunId: sourceRunId,
            SourceManifestHash: sourceManifest.ContentHash,
            GeneratedAt: generatedAt,
            CompositeTree: compositeTree,
            Summary: summary,
            CriticalFindings: critical,
            Recommendations: recs,
            Disclaimer: Disclaimer,
            Attestation: new AgenticAttestation(
                AgentEvalVersion: typeof(AgenticBenchmarkReporter).Assembly.GetName().Version?.ToString() ?? "0.0.0",
                JudgeMode: options.JudgeMode,
                PromptVersions: options.PromptVersions ?? new Dictionary<string, string>
                {
                    ["agentic-judge-system"] = "v1",
                    ["task-completion-criterion"] = "v1"
                }));

        await WriteAgenticResultFileAsync(store, subject, generatedAt, result, ct);
        return result;
    }

    // ── File persistence ───────────────────────────────────────────────────────

    private static async Task WriteAgenticResultFileAsync(
        IOutputStore store,
        SubjectIdentity subject,
        DateTimeOffset ts,
        AgenticBenchmarkResult result,
        CancellationToken ct)
    {
        // Validate against the embedded agentic-result.schema.json BEFORE persisting.
        // This catches shape regressions even on non-filesystem stores; throws if the
        // schema check fails so a malformed result never reaches disk.
        ValidateAgainstAgenticResultSchema(result);

        if (store is FileSystemOutputStore fsStore)
        {
            var root = fsStore.WorkspaceRoot
                ?? throw new InvalidOperationException("FileSystemOutputStore has no workspace root.");
            var sanitizedSubject = SanitizeForPath(subject.Name);
            var tsString = ts.ToString("yyyy-MM-dd_HH-mm-ss");
            var dir = Path.Combine(root, "benchmarks", "agentic", sanitizedSubject, tsString);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "agentic-result.json");

            await using var stream = File.Create(path);
            await JsonSerializer.SerializeAsync(stream, result, s_jsonOpts, ct);
        }
        // For non-filesystem stores (InMemory, Null), the result is held in memory by the
        // caller's returned AgenticBenchmarkResult value. Tests that want to inspect the
        // JSON serialise it themselves.
    }

    // ── Schema validation ──────────────────────────────────────────────────────

    private static JsonSchema? s_cachedSchema;

    private static void ValidateAgainstAgenticResultSchema(AgenticBenchmarkResult result)
    {
        var schema = LoadAgenticResultSchema();
        var json = JsonSerializer.Serialize(result, s_jsonOpts);
        var node = JsonNode.Parse(json);
        var evalOptions = new EvaluationOptions { OutputFormat = OutputFormat.List };
        var evalResult = schema.Evaluate(node, evalOptions);
        if (!evalResult.IsValid)
        {
            var errors = string.Join("; ", evalResult.Details
                .Where(d => !d.IsValid)
                .SelectMany(d => d.Errors?.Select(e => $"{d.EvaluationPath} {e.Key}={e.Value}") ?? Array.Empty<string>()));
            throw new InvalidOperationException($"agentic-result.json failed schema validation: {errors}");
        }
    }

    private static JsonSchema LoadAgenticResultSchema()
    {
        if (s_cachedSchema is not null) return s_cachedSchema;
        var asm = typeof(AgenticBenchmarkReporter).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .Single(n => n.EndsWith(".agentic-result.schema.json", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        s_cachedSchema = JsonSchema.FromText(reader.ReadToEnd());
        return s_cachedSchema;
    }

    // ── Path helpers ───────────────────────────────────────────────────────────

    private static string SanitizeForPath(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\' }).ToArray();
        var s = string.Concat(name.Select(c => invalid.Contains(c) ? '-' : c));
        return s.Trim('.', ' ');
    }
}
