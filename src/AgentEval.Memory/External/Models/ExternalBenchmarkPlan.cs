// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AgentEval.Memory.External.LongMemEval;

namespace AgentEval.Memory.External.Models;

/// <summary>Content-free snapshot of selected questions and applied options, captured before execution.</summary>
public sealed class ExternalBenchmarkPlan
{
    private readonly JsonObject _configuration;

    /// <summary>Selected question identifiers in execution order.</summary>
    public IReadOnlyList<string> QuestionIds { get; }
    /// <summary>SHA-256 of the serialized selected entries, including history and expected answers.</summary>
    public string SelectedInputsDigest { get; }
    /// <summary>Optional source corpus digest, including typed judge extension data when present.</summary>
    public string? CorpusDigest { get; }
    /// <summary>Detached applied configuration; excludes local dataset paths.</summary>
    public JsonObject Configuration => (JsonObject)_configuration.DeepClone();

    [JsonConstructor]
    internal ExternalBenchmarkPlan(IReadOnlyList<string> questionIds, string selectedInputsDigest, string? corpusDigest, JsonObject configuration)
    {
        QuestionIds = Array.AsReadOnly(questionIds.ToArray());
        SelectedInputsDigest = selectedInputsDigest;
        CorpusDigest = corpusDigest;
        _configuration = (JsonObject)configuration.DeepClone();
    }

    internal ExternalBenchmarkPlan(ExternalBenchmarkOptions options, IReadOnlyList<LongMemEvalEntry> entries, string? corpusJson = null)
    {
        _configuration = JsonSerializer.SerializeToNode(options)!.AsObject();
        _configuration.Remove(nameof(ExternalBenchmarkOptions.DatasetPath));
        QuestionIds = Array.AsReadOnly(entries.Select(e => e.QuestionId).ToArray());
        using var hash = SHA256.Create();
        using (var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write))
        {
            JsonSerializer.Serialize(stream, entries);
            stream.FlushFinalBlock();
            SelectedInputsDigest = "sha256:" + Convert.ToHexString(hash.Hash!).ToLowerInvariant();
        }
        CorpusDigest = corpusJson is null ? null : "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(corpusJson))).ToLowerInvariant();
    }
}
