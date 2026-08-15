// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.External.Models;

namespace AgentEval.Memory.External.TypedMemEval;

/// <summary>
/// The five memory mechanisms TypedMemEval isolates, one corpus each (ADR-026 §5).
/// </summary>
/// <remarks>
/// Each vertical exists because LongMemEval-S cannot measure it: prospective memory has no
/// questions there at all, episodic structure has no list-order or attribution questions,
/// derived answers are never separable from retrieval difficulty, and working-memory distance
/// and forgetting have no question types. Isolation per corpus is the family's reason to exist —
/// blending them would re-create the attribution problem where a wrong answer has three
/// candidate causes.
/// </remarks>
public enum TypedMemEvalVertical
{
    /// <summary>Due-later reminders, expiring validity, not-yet-true assertions.</summary>
    Prospective = 0,

    /// <summary>Assistant-stated answers, list-order, speaker attribution.</summary>
    Episodic = 1,

    /// <summary>Counts, sums, deltas, durations — answers that must be derived.</summary>
    Arithmetic = 2,

    /// <summary>Stable profile facts recalled at increasing session distance.</summary>
    WorkingMemory = 3,

    /// <summary>Invalidated facts, still-valid controls, never-known probes.</summary>
    Forgetting = 4
}

/// <summary>What the harness needs to know about one vertical before it runs.</summary>
public sealed class TypedMemEvalVerticalDescriptor
{
    /// <summary>The vertical.</summary>
    public required TypedMemEvalVertical Vertical { get; init; }

    /// <summary>Lowercase slug used in corpus ids, benchmark ids, and resource paths.</summary>
    public required string Slug { get; init; }

    /// <summary>Three-letter question-id abbreviation (ADR §1).</summary>
    public required string Abbreviation { get; init; }

    /// <summary>Display name used in <see cref="ExternalBenchmarkResult.BenchmarkName"/>.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Questions the shipped corpus holds.</summary>
    public required int QuestionCount { get; init; }

    /// <summary>
    /// Grounding this vertical requires. Validated before the first provider call, with the
    /// same fail-before-measuring behaviour as the LongMemEval temporal-grounding guard: a run
    /// that could not have measured what it claims should never produce a score.
    /// </summary>
    public required TemporalGroundingMode RequiredGrounding { get; init; }

    /// <summary>
    /// Whether the vertical needs session timestamps to reach the agent at all. True for
    /// Arithmetic, whose duration questions derive gold from timestamps — under a timestamp-free
    /// mode those questions are reported unrun rather than counted as failures (ADR §5.3).
    /// </summary>
    public required bool RequiresTimestamps { get; init; }

    /// <summary>Corpus identifier, revision included.</summary>
    public string CorpusId => $"agenteval-typedmemeval-{Slug}-v1";

    /// <summary>Benchmark identifier stamped into every result.</summary>
    public string BenchmarkId => $"typedmemeval-{Slug}";

    /// <summary>Dataset revision.</summary>
    public string Revision => "v1";
}

/// <summary>Descriptor lookup for the five verticals.</summary>
public static class TypedMemEvalVerticals
{
    private static readonly Dictionary<TypedMemEvalVertical, TypedMemEvalVerticalDescriptor> s_all = new()
    {
        [TypedMemEvalVertical.Prospective] = new()
        {
            Vertical = TypedMemEvalVertical.Prospective,
            Slug = "prospective",
            Abbreviation = "pro",
            DisplayName = "TypedMemEval-Prospective",
            QuestionCount = 50,
            // The vertical's whole premise is that dates are not printed anywhere, so the
            // timestamps have to arrive through the typed channel or nothing is being measured.
            RequiredGrounding = TemporalGroundingMode.TimestampsOnly,
            RequiresTimestamps = true
        },
        [TypedMemEvalVertical.Episodic] = new()
        {
            Vertical = TypedMemEvalVertical.Episodic,
            Slug = "episodic",
            Abbreviation = "epi",
            DisplayName = "TypedMemEval-Episodic",
            QuestionCount = 50,
            RequiredGrounding = TemporalGroundingMode.None,
            RequiresTimestamps = false
        },
        [TypedMemEvalVertical.Arithmetic] = new()
        {
            Vertical = TypedMemEvalVertical.Arithmetic,
            Slug = "arithmetic",
            Abbreviation = "ari",
            DisplayName = "TypedMemEval-Arithmetic",
            QuestionCount = 50,
            RequiredGrounding = TemporalGroundingMode.None,
            RequiresTimestamps = true
        },
        [TypedMemEvalVertical.WorkingMemory] = new()
        {
            Vertical = TypedMemEvalVertical.WorkingMemory,
            Slug = "workingmemory",
            Abbreviation = "wm",
            DisplayName = "TypedMemEval-WorkingMemory",
            QuestionCount = 48,
            RequiredGrounding = TemporalGroundingMode.None,
            RequiresTimestamps = false
        },
        [TypedMemEvalVertical.Forgetting] = new()
        {
            Vertical = TypedMemEvalVertical.Forgetting,
            Slug = "forgetting",
            Abbreviation = "for",
            DisplayName = "TypedMemEval-Forgetting",
            QuestionCount = 50,
            RequiredGrounding = TemporalGroundingMode.None,
            RequiresTimestamps = false
        }
    };

    /// <summary>Every vertical, in declaration order.</summary>
    public static IReadOnlyList<TypedMemEvalVerticalDescriptor> All { get; } =
        s_all.Values.OrderBy(d => d.Vertical).ToArray();

    /// <summary>The descriptor for one vertical.</summary>
    /// <exception cref="ArgumentOutOfRangeException">When the value is not a declared vertical.</exception>
    public static TypedMemEvalVerticalDescriptor For(TypedMemEvalVertical vertical)
        => s_all.TryGetValue(vertical, out var descriptor)
            ? descriptor
            : throw new ArgumentOutOfRangeException(nameof(vertical), vertical, "Unknown TypedMemEval vertical.");
}
