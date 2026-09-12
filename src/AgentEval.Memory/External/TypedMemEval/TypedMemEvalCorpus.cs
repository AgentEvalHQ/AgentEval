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
        if (!document.RootElement.TryGetProperty("coverage", out var coverage) ||
            !coverage.TryGetProperty("per_question", out var perQuestion) ||
            perQuestion.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Averaged over gold-bearing questions only. The stamped mean includes never-known probes,
        // whose coverage is vacuously 1.0 because they have nothing to miss — 30% of the Forgetting
        // corpus — and a floor inflated by those would flatter every system measured against it.
        var goldBearing = TypedMemEvalExtensions.Parse(ReadJson(vertical))
            .Where(kv => kv.Value.GoldSessionIndices.Count > 0)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);

        var values = perQuestion.EnumerateObject()
            .Where(p => goldBearing.Contains(p.Name) && p.Value.ValueKind == JsonValueKind.Number)
            .Select(p => p.Value.GetDouble())
            .ToArray();

        return values.Length > 0 ? values.Average() : null;
    }

    /// <summary>
    /// What a system that GUESSES on every closed-choice question would score, summed from the
    /// floors the corpus itself declares. Null when this vertical declares none.
    /// </summary>
    /// <param name="vertical">The vertical.</param>
    /// <returns>Declared-floor count, question count, and the summed guessing score.</returns>
    /// <remarks>
    /// <para>
    /// 🔴 <b>This exists because <c>Correct</c> is not interpretable without it.</b> 125 of the
    /// family's 565 questions are closed-choice and say so in their own extension
    /// (<c>chance_floor</c>), and the concentration is extreme: <b>Procedural declares one on all 80</b>,
    /// summing to <b>27.2</b>. A reader told "Correct 35 of 80" reads 44%; luck alone supplies 27 of
    /// those 35. Conjunction 12.5 of 65, Semantic 5.0 of 50, and the other seven verticals declare
    /// none at all — so this cannot be applied as a blanket correction, which is exactly why it is
    /// reported per vertical rather than folded into a score.
    /// </para>
    /// <para>
    /// ⚠ <b>It is an UPPER bound on free score, not an expectation.</b> It assumes the system
    /// answers every one of those questions. A system that abstains scores <i>below</i> it without
    /// being worse, because abstention is not a wrong answer here — see
    /// <see cref="TypedMemEvalOutcomeCounts.Abstained"/>. Read it as "at most this much was free",
    /// never as "subtract this".
    /// </para>
    /// <para>
    /// This is deliberately NOT an <c>AgentEval.Evals.Meta.ChanceFloor</c>. That type models an
    /// <i>arm's declared draw budget</i> (k, with provenance) and is applied at an admission door;
    /// this is a per-question property of a corpus, aggregated. And it is emphatically not
    /// <see cref="CalibratedFloorMean"/>, which is BM25 retrieval coverage — a competent baseline,
    /// not a null model. Conflating the two would let "beat word matching" masquerade as "cleared
    /// chance", which is a far stronger claim.
    /// </para>
    /// </remarks>
    internal static (int Declared, int Total, double Guessing)? GuessingBaseline(
        TypedMemEvalVertical vertical)
    {
        using var document = JsonDocument.Parse(ReadJson(vertical));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        int declared = 0, total = 0;
        double guessing = 0;
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            total++;
            if (!entry.TryGetProperty("typedmemeval", out var extension))
            {
                continue;
            }

            // A question may declare a floor at more than one depth (a shape-level default and a
            // per-component override). Take the LARGEST: it is the most generous to chance, so the
            // baseline never understates how much score was free.
            var largest = LargestDeclaredFloor(extension);
            if (largest is not { } floor)
            {
                continue;
            }

            declared++;
            guessing += floor;
        }

        return declared == 0 ? null : (declared, total, guessing);
    }

    /// <summary>The largest <c>chance_floor</c> declared anywhere beneath <paramref name="node"/>.</summary>
    /// <param name="node">A question's extension block.</param>
    /// <returns>The floor, or null when none is declared.</returns>
    /// <remarks>
    /// ⚠ <b>Internal so it can be tested DIRECTLY.</b> No shipped corpus declares two floors on
    /// one question — measured: 0 of 565 — so this rule is a no-op on real data and an ablation
    /// through <see cref="GuessingBaseline"/> cannot make it fail. A guard no test can exercise is
    /// a guard nobody has checked, so the test constructs the nested case itself.
    /// </remarks>
    internal static double? LargestDeclaredFloor(JsonElement node)
    {
        double? best = null;
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    if (property.NameEquals("chance_floor") &&
                        property.Value.ValueKind == JsonValueKind.Number)
                    {
                        var value = property.Value.GetDouble();
                        best = best is { } b && b >= value ? b : value;
                        continue;
                    }

                    if (LargestDeclaredFloor(property.Value) is { } nested)
                    {
                        best = best is { } b && b >= nested ? b : nested;
                    }
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                {
                    if (LargestDeclaredFloor(item) is { } nested)
                    {
                        best = best is { } b && b >= nested ? b : nested;
                    }
                }
                break;
        }

        return best;
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
