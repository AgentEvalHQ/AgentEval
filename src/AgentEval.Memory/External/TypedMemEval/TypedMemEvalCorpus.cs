// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;

namespace AgentEval.Memory.External.TypedMemEval;

/// <summary>
/// Access to the five embedded TypedMemEval corpora and their authoring metadata.
/// </summary>
/// <remarks>
/// <para>
/// Follows the time-grounded probe's pattern exactly: no dataset path, a versioned identifier,
/// and a newline-normalized SHA-256 over the shipped text. Newline normalization matters for the
/// same reason it did there — the file is embedded from a git checkout, and a hash that changed
/// when a run moved from a Windows machine to a Linux runner would report "different corpus" for
/// a corpus nobody touched.
/// </para>
/// <para>
/// Two resources per vertical. The corpus is a bare JSON array in LongMemEval's own shape, so the
/// existing loader reads it unchanged. The <c>.meta.json</c> sidecar carries authoring provenance —
/// calibration values and the V1–V6 probe records — and is deliberately <b>not</b> part of the
/// corpus hash: re-running the reference-model probes rewrites metadata, and if that moved the
/// corpus hash then every stored run would report a corpus change that never happened. The
/// metadata names the corpus hash it describes, so a stale pairing is detectable rather than silent.
/// </para>
/// </remarks>
public static class TypedMemEvalCorpus
{
    /// <summary>Reference retrieval budget the corpora were calibrated against, in sessions.</summary>
    public const int ReferenceBudgetSessions = 5;

    private static readonly Dictionary<string, string> s_cache = new(StringComparer.Ordinal);
    // A plain object rather than System.Threading.Lock: this assembly still targets net8.0, where
    // that type does not exist.
    private static readonly object s_gate = new();

    /// <summary>Reads a corpus exactly as shipped.</summary>
    public static string ReadJson(TypedMemEvalVertical vertical)
        => ReadResource($"{TypedMemEvalVerticals.For(vertical).CorpusId}.json");

    /// <summary>Reads a corpus's authoring metadata exactly as shipped.</summary>
    public static string ReadMetadataJson(TypedMemEvalVertical vertical)
        => ReadResource($"{TypedMemEvalVerticals.For(vertical).CorpusId}.meta.json");

    /// <summary>Newline-normalized SHA-256 over the corpus text.</summary>
    public static string Sha256(TypedMemEvalVertical vertical) => ComputeSha256(ReadJson(vertical));

    /// <summary>
    /// The hash of a given corpus text. Exposed so a test can feed the same corpus in both
    /// line-ending conventions and fix the property, rather than pinning a literal value that
    /// would itself differ between checkouts.
    /// </summary>
    internal static string ComputeSha256(string json)
    {
        var normalized = json.Replace("\r\n", "\n", StringComparison.Ordinal);
        // Not a security function: this identifies which corpus produced a number.
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    /// <summary>
    /// Loads a vertical's questions, applying the same selection rules any dataset gets.
    /// </summary>
    /// <remarks>
    /// Note what does <i>not</i> come back: <see cref="LongMemEvalEntry"/> has no member for the
    /// family's <c>typedmemeval</c> extension block, so deserialization drops it. That is the
    /// structural half of the answer-key guard — the block holding gold derivations, component
    /// indices, and pair arms cannot reach a formatted prompt because it never reaches the entry
    /// the formatter reads. <see cref="TypedMemEvalExtensions.Parse"/> reads it from the raw JSON
    /// on a separate path that the agent never touches.
    /// </remarks>
    public static IReadOnlyList<LongMemEvalEntry> Load(
        TypedMemEvalVertical vertical, ExternalBenchmarkOptions? options = null)
        => LongMemEvalDataLoader.LoadFromJson(ReadJson(vertical), options ?? new ExternalBenchmarkOptions());

    /// <summary>
    /// Options that run the Prospective vertical the way it is meant to be run: session
    /// timestamps delivered through the typed channel, and the harness's in-text dates removed.
    /// </summary>
    /// <remarks>
    /// Pair it with <see cref="ProspectiveControlOptions"/>. The difference between the two scores
    /// is the measurement — a system that honours timestamps scores the same in both, and one that
    /// was reading dates out of the text drops. Same corpus, same hash, two option sets; the
    /// <c>-control</c> suffix is a <see cref="ExternalBenchmarkOptions.DatasetMode"/> label, never
    /// a second corpus file.
    /// </remarks>
    public static TypedMemEvalOptions ProspectiveProbeOptions => new()
    {
        TemporalGrounding = TemporalGroundingMode.TimestampsOnly
    };

    /// <summary>The control arm for <see cref="ProspectiveProbeOptions"/>.</summary>
    public static TypedMemEvalOptions ProspectiveControlOptions => new()
    {
        TemporalGrounding = TemporalGroundingMode.TimestampsAndText,
        ControlArm = true
    };

    /// <summary>
    /// The corpus's calibrated BM25 mean coverage at <c>K_ref</c>, or null when metadata does not
    /// carry one. Read at run time so a result can print realised coverage against the lexical
    /// floor the corpus was actually tuned to.
    /// </summary>
    internal static double? CalibratedFloorMean(TypedMemEvalVertical vertical)
    {
        using var document = JsonDocument.Parse(ReadMetadataJson(vertical));
        return document.RootElement.TryGetProperty("coverage", out var coverage) &&
               coverage.TryGetProperty("mean_realised", out var mean) &&
               mean.ValueKind == JsonValueKind.Number
            ? mean.GetDouble()
            : null;
    }

    private static string ReadResource(string suffix)
    {
        lock (s_gate)
        {
            if (s_cache.TryGetValue(suffix, out var cached))
                return cached;
        }

        var assembly = typeof(TypedMemEvalCorpus).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(suffix, StringComparison.Ordinal))
            ?? throw new FileNotFoundException(
                $"Embedded TypedMemEval resource '{suffix}' was not found in {assembly.GetName().Name}.");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();

        lock (s_gate)
        {
            s_cache[suffix] = text;
        }
        return text;
    }
}
