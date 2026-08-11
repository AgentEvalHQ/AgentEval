// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Reflection;
using System.Security.Cryptography;
using AgentEval.Guardrails;
using AgentEval.Memory.External.Models;

namespace AgentEval.Memory.External.LongMemEval;

/// <summary>
/// Computes the fingerprints that make two LongMemEval runs provably comparable: the judge prompt
/// text, the dataset file, and the selected sample.
/// </summary>
/// <remarks>
/// Hashing reuses <see cref="ManifestFingerprint"/> — the same SHA-256-over-canonical-content
/// primitive the skill-manifest and MCP-tool drift gates use. Nothing here calls a model.
/// </remarks>
internal static class LongMemEvalProvenance
{
    /// <summary>Sentinel inputs, so the fingerprint depends on template text and nothing else.</summary>
    private const string QuestionSentinel = "<agenteval:question>";
    private const string GoldSentinel = "<agenteval:gold>";
    private const string HypothesisSentinel = "<agenteval:hypothesis>";
    private const string PredicateSentinel = "<agenteval:predicate>";

    /// <summary>The judge prompt families covered by <see cref="ComputeJudgePromptFingerprint"/>.</summary>
    internal static readonly string[] TemplateNames =
    [
        "Standard",
        "Preference",
        "Temporal",
        "KnowledgeUpdate",
        "Abstention",
        "Predicate",
        "StructuredOutputSuffix"
    ];

    /// <summary>
    /// SHA-256 over every judge prompt template, each rendered with fixed sentinels and labelled by
    /// name. Editing any template — or adding one to <see cref="TemplateNames"/> — changes the hash.
    /// </summary>
    internal static string ComputeJudgePromptFingerprint()
    {
        var canonical = string.Join(
            "\n\n",
            [
                "Standard" + LongMemEvalJudgePrompts.Standard(QuestionSentinel, GoldSentinel, HypothesisSentinel),
                "Preference" + LongMemEvalJudgePrompts.Preference(QuestionSentinel, GoldSentinel, HypothesisSentinel),
                "Temporal" + LongMemEvalJudgePrompts.Temporal(QuestionSentinel, GoldSentinel, HypothesisSentinel),
                "KnowledgeUpdate" + LongMemEvalJudgePrompts.KnowledgeUpdate(QuestionSentinel, GoldSentinel, HypothesisSentinel),
                "Abstention" + LongMemEvalJudgePrompts.Abstention(QuestionSentinel, GoldSentinel, HypothesisSentinel),
                "Predicate" + LongMemEvalJudgePrompts.Predicate(QuestionSentinel, PredicateSentinel, HypothesisSentinel),
                // Covers the structured-verdict contract as well, since changing it changes what the
                // judge is asked for even though the family templates are untouched.
                "StructuredOutputSuffix" + LongMemEvalStructuredVerdict.PromptSuffix
            ]);

        return ManifestFingerprint.Hash(canonical);
    }

    /// <summary>SHA-256 over the ordered selected question ids.</summary>
    internal static string ComputeSelectedIdFingerprint(IEnumerable<string> orderedQuestionIds)
        => ManifestFingerprint.Hash(string.Join("\n", orderedQuestionIds));

    /// <summary>
    /// SHA-256 over the dataset file's bytes, streamed so the ~277 MB S-mode file is not buffered.
    /// Returns null when the file cannot be read — a provenance failure must not fail a run that
    /// otherwise succeeded, and a null hash is honest about not having measured one.
    /// </summary>
    internal static string? TryComputeFileSha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Informational version of the assembly, or null when unattributed.</summary>
    internal static string? TryGetAgentEvalVersion()
        => typeof(LongMemEvalProvenance).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    /// <summary>
    /// Builds the provenance record for a run. <paramref name="datasetPath"/>,
    /// <paramref name="datasetQuestionCount"/> and <paramref name="selectedQuestionIds"/> are only
    /// consulted under <see cref="RunProvenanceMode.Full"/>.
    /// </summary>
    internal static BenchmarkRunProvenance? Capture(
        RunProvenanceMode mode,
        string? datasetPath,
        int? datasetQuestionCount,
        IEnumerable<string>? selectedQuestionIds)
    {
        if (mode == RunProvenanceMode.None)
            return null;

        var promptFingerprint = ComputeJudgePromptFingerprint();
        var version = TryGetAgentEvalVersion();

        if (mode == RunProvenanceMode.PromptsOnly)
        {
            return new BenchmarkRunProvenance
            {
                Mode = mode,
                JudgePromptFingerprint = promptFingerprint,
                JudgePromptTemplateNames = TemplateNames,
                AgentEvalVersion = version
            };
        }

        long? sizeBytes = null;
        string? datasetHash = null;
        if (!string.IsNullOrWhiteSpace(datasetPath) && File.Exists(datasetPath))
        {
            datasetHash = TryComputeFileSha256(datasetPath);
            try
            {
                sizeBytes = new FileInfo(datasetPath).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                sizeBytes = null;
            }
        }

        return new BenchmarkRunProvenance
        {
            Mode = mode,
            JudgePromptFingerprint = promptFingerprint,
            JudgePromptTemplateNames = TemplateNames,
            AgentEvalVersion = version,
            DatasetPath = datasetPath,
            DatasetSha256 = datasetHash,
            DatasetSizeBytes = sizeBytes,
            DatasetQuestionCount = datasetQuestionCount,
            SelectedQuestionIdFingerprint = selectedQuestionIds is null
                ? null
                : ComputeSelectedIdFingerprint(selectedQuestionIds)
        };
    }
}
