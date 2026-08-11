// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Security.Cryptography;
using AgentEval.Memory.External;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentEval.Memory.Tests;

/// <summary>
/// Dataset and judge-prompt fingerprints, and recovery of a provider's backend build identifier.
/// </summary>
public class LongMemEvalProvenanceTests
{
    // ── Ask 5: judge prompt fingerprint ───────────────────────────────────────

    /// <summary>
    /// The judge-prompt fingerprint as of this release. It is pinned deliberately: this assertion is
    /// the drift detector. Editing any judge template changes the hash and fails here, which is the
    /// signal that sealed bases recorded under the old prompts are no longer comparable — the thing
    /// that otherwise requires hand-diffing library source between releases.
    /// </summary>
    private const string ExpectedJudgePromptFingerprint =
        "cc06b7d368439206428559be7f29939c1a943aae59a07e0b8eb858456f4255bc";

    [Fact]
    public void JudgePromptFingerprint_IsStableAcrossCalls()
    {
        Assert.Equal(
            LongMemEvalProvenance.ComputeJudgePromptFingerprint(),
            LongMemEvalProvenance.ComputeJudgePromptFingerprint());
    }

    [Fact]
    public void JudgePromptFingerprint_MatchesThePinnedValue()
    {
        Assert.Equal(ExpectedJudgePromptFingerprint, LongMemEvalProvenance.ComputeJudgePromptFingerprint());
    }

    [Fact]
    public void JudgePromptFingerprint_CoversEveryTemplateFamily()
    {
        Assert.Equal(
            ["Standard", "Preference", "Temporal", "KnowledgeUpdate", "Abstention", "Predicate", "StructuredOutputSuffix"],
            LongMemEvalProvenance.TemplateNames);
    }

    [Fact]
    public void JudgePromptFingerprint_ChangesWhenATemplateChanges()
    {
        // Demonstrates sensitivity without editing shipped prompts: the same hashing over a modified
        // canonical body yields a different value, so a real template edit cannot slip through.
        var real = LongMemEvalProvenance.ComputeJudgePromptFingerprint();
        var mutated = Guardrails.ManifestFingerprint.Hash(
            LongMemEvalJudgePrompts.Standard("q", "a", "h") + " (edited)");

        Assert.NotEqual(real, mutated);
    }

    // ── Ask 5: dataset fingerprint ────────────────────────────────────────────

    [Fact]
    public void DatasetFingerprint_MatchesAnIndependentHashOfTheFile()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "DataLoading", "longmemeval-sampling-fixture.json");

        var computed = LongMemEvalProvenance.TryComputeFileSha256(path);
        var expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        Assert.Equal(expected, computed);
    }

    [Fact]
    public void DatasetFingerprint_UnreadableFile_IsNullNotAPlaceholder()
    {
        var missing = Path.Combine(AppContext.BaseDirectory, "does-not-exist.json");

        Assert.Null(LongMemEvalProvenance.TryComputeFileSha256(missing));
    }

    [Fact]
    public void SelectedIdFingerprint_DependsOnOrderAndMembership()
    {
        var a = LongMemEvalProvenance.ComputeSelectedIdFingerprint(["q1", "q2", "q3"]);
        var sameAgain = LongMemEvalProvenance.ComputeSelectedIdFingerprint(["q1", "q2", "q3"]);
        var reordered = LongMemEvalProvenance.ComputeSelectedIdFingerprint(["q3", "q2", "q1"]);
        var different = LongMemEvalProvenance.ComputeSelectedIdFingerprint(["q1", "q2", "q4"]);

        Assert.Equal(a, sameAgain);
        Assert.NotEqual(a, reordered);
        Assert.NotEqual(a, different);
    }

    [Fact]
    public void Capture_None_ReturnsNull()
    {
        Assert.Null(LongMemEvalProvenance.Capture(RunProvenanceMode.None, "path", 500, ["q1"]));
    }

    [Fact]
    public void Capture_PromptsOnly_OmitsDatasetFieldsRatherThanGuessingThem()
    {
        var provenance = LongMemEvalProvenance.Capture(RunProvenanceMode.PromptsOnly, "path", 500, ["q1"]);

        Assert.NotNull(provenance);
        Assert.NotNull(provenance!.JudgePromptFingerprint);
        // Unmeasured is null, never a plausible-looking value.
        Assert.Null(provenance.DatasetSha256);
        Assert.Null(provenance.DatasetPath);
        Assert.Null(provenance.DatasetSizeBytes);
        Assert.Null(provenance.DatasetQuestionCount);
        Assert.Null(provenance.SelectedQuestionIdFingerprint);
    }

    [Fact]
    public void Capture_Full_RecordsDatasetAndSelection()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "DataLoading", "longmemeval-sampling-fixture.json");

        var provenance = LongMemEvalProvenance.Capture(RunProvenanceMode.Full, path, 500, ["q1", "q2"]);

        Assert.NotNull(provenance);
        Assert.Equal(RunProvenanceMode.Full, provenance!.Mode);
        Assert.Equal(path, provenance.DatasetPath);
        Assert.Equal(LongMemEvalProvenance.TryComputeFileSha256(path), provenance.DatasetSha256);
        Assert.Equal(new FileInfo(path).Length, provenance.DatasetSizeBytes);
        Assert.Equal(500, provenance.DatasetQuestionCount);
        Assert.Equal(
            LongMemEvalProvenance.ComputeSelectedIdFingerprint(["q1", "q2"]),
            provenance.SelectedQuestionIdFingerprint);
    }

    // ── Ask 6: provider system_fingerprint ────────────────────────────────────

    [Fact]
    public void SystemFingerprint_ReadFromAdditionalProperties()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes"))
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["system_fingerprint"] = "fp_abc123" }
        };

        Assert.Equal("fp_abc123", ProviderFingerprint.FromChatResponse(response));
    }

    [Fact]
    public void SystemFingerprint_ReadFromRawRepresentationWhenNotInProperties()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes"))
        {
            // Stands in for the provider's own completion object, which does carry the value.
            RawRepresentation = new FakeCompletion("fp_from_raw")
        };

        Assert.Equal("fp_from_raw", ProviderFingerprint.FromChatResponse(response));
    }

    [Fact]
    public void SystemFingerprint_AbsentIsNullNotAPlaceholder()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes"));

        Assert.Null(ProviderFingerprint.FromChatResponse(response));
    }

    [Fact]
    public void SystemFingerprint_BlankIsTreatedAsAbsent()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes"))
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { ["system_fingerprint"] = "   " }
        };

        Assert.Null(ProviderFingerprint.FromChatResponse(response));
    }

    [Fact]
    public void SystemFingerprint_IsLengthBounded()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes"))
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["system_fingerprint"] = new string('f', 1000)
            }
        };

        Assert.Equal(ProviderFingerprint.MaximumLength, ProviderFingerprint.FromChatResponse(response)!.Length);
    }

    [Fact]
    public void SystemFingerprint_ThrowingRawRepresentationDoesNotFailTheRun()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "yes"))
        {
            RawRepresentation = new ThrowingCompletion()
        };

        Assert.Null(ProviderFingerprint.FromChatResponse(response));
    }

    [Fact]
    public void SystemFingerprint_FromAgentResponseProperties()
    {
        var properties = new Dictionary<string, object?> { ["system_fingerprint"] = "fp_agent" };

        Assert.Equal("fp_agent", ProviderFingerprint.FromAgentResponse(properties));
        Assert.Null(ProviderFingerprint.FromAgentResponse(null));
    }

    private sealed class FakeCompletion(string fingerprint)
    {
        public string SystemFingerprint { get; } = fingerprint;
    }

    private sealed class ThrowingCompletion
    {
        public string SystemFingerprint => throw new InvalidOperationException("provider blew up");
    }
}
