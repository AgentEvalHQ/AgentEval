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
    /// <remarks>
    /// <para>
    /// Line endings are normalized to <c>\n</c> before hashing, so the value identifies the prompt
    /// <i>text</i> rather than the checkout it was compiled from. The templates are C# raw string
    /// literals, which carry their source file's line terminators into the compiled string, and
    /// <c>.gitattributes</c> does not pin <c>*.cs</c> to LF — so the same commit yields CRLF prompts
    /// on a Windows checkout and LF prompts on Linux. Without normalization this fingerprint would
    /// report "prompts changed" for a run that merely moved between a developer machine and CI,
    /// which is noise, not drift.
    /// </para>
    /// <para>
    /// The consequence is worth stating plainly: the prompt <i>bytes</i> sent to the judge are
    /// platform-dependent today, and this fingerprint deliberately does not detect that. It answers
    /// "are these the same templates?", not "were these the same bytes on the wire?".
    /// </para>
    /// </remarks>
    internal static string ComputeJudgePromptFingerprint()
        => ManifestFingerprint.Hash(BuildCanonicalJudgePromptContent());

    /// <summary>
    /// The exact text <see cref="ComputeJudgePromptFingerprint"/> hashes, newline-normalized. Exposed
    /// so a test can assert the hashed input carries no carriage returns, which is what makes the
    /// fingerprint identical on a CRLF checkout and an LF one.
    /// </summary>
    internal static string BuildCanonicalJudgePromptContent()
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

        return NormalizeLineEndings(canonical);
    }

    /// <summary>Collapses CRLF to LF so a hash does not depend on the checkout's line endings.</summary>
    private static string NormalizeLineEndings(string text)
        => text.Replace("\r\n", "\n", StringComparison.Ordinal);

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
        IEnumerable<string>? selectedQuestionIds,
        string? datasetIdentifier = null,
        string? precomputedDatasetSha256 = null)
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
        // An embedded corpus has no file to stream, and its hash is computed from the resource text
        // instead. Preferred when supplied, so a pathless dataset is still pinned rather than
        // reported as unmeasured.
        string? datasetHash = precomputedDatasetSha256;
        if (datasetHash is null && !string.IsNullOrWhiteSpace(datasetPath) && File.Exists(datasetPath))
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
            DatasetIdentifier = datasetIdentifier,
            DatasetSha256 = datasetHash,
            DatasetSizeBytes = sizeBytes,
            DatasetQuestionCount = datasetQuestionCount,
            SelectedQuestionIdFingerprint = selectedQuestionIds is null
                ? null
                : ComputeSelectedIdFingerprint(selectedQuestionIds)
        };
    }
}
