// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

namespace AgentEval.Memory.External.Models;

/// <summary>
/// How abstention questions are treated when a subset is sampled.
/// </summary>
/// <remarks>
/// <para>
/// Abstention questions ask whether the agent RECOGNISES a question as unanswerable, which is a
/// different capability from recalling a fact. Stratification is over question <i>type</i>, and
/// abstention is orthogonal to type — an abstention question carries the same
/// <c>question_type</c> as an ordinary one and is distinguished only by the <c>_abs</c> suffix on
/// its id. Proportional sampling across types therefore says nothing about how many abstention
/// questions a sample contains, and with a fixed seed a sample can contain none at all, run after
/// run, without that ever being visible in the configuration.
/// </para>
/// </remarks>
public enum AbstentionSamplingPolicy
{
    /// <summary>
    /// No abstention-specific handling: whatever the type-stratified draw happens to yield.
    /// This is the historical behaviour and the default.
    /// </summary>
    AsSampled = 0,

    /// <summary>Remove every abstention question from the pool before sampling.</summary>
    Exclude = 1,

    /// <summary>Sample only abstention questions — a dedicated meta-memory run.</summary>
    Only = 2,

    /// <summary>
    /// Allocate a requested share of the budget to abstention questions and the remainder to
    /// ordinary ones, each stratified within itself. See
    /// <see cref="ExternalBenchmarkOptions.AbstentionTargetProportion"/>.
    /// </summary>
    TargetProportion = 3
}

/// <summary>
/// How much run provenance is captured onto <see cref="ExternalBenchmarkResult.Provenance"/>.
/// </summary>
public enum RunProvenanceMode
{
    /// <summary>Capture nothing; <see cref="ExternalBenchmarkResult.Provenance"/> stays null. Default.</summary>
    None = 0,

    /// <summary>
    /// Capture the judge-prompt fingerprint and the AgentEval version. Costs no I/O — the prompts are
    /// hashed from the templates already in memory.
    /// </summary>
    PromptsOnly = 1,

    /// <summary>
    /// Everything in <see cref="PromptsOnly"/>, plus a SHA-256 over the dataset file and the id
    /// fingerprint of the selected sample. Reads the dataset a second time, which is a real cost on
    /// the ~277 MB S-mode file.
    /// </summary>
    Full = 2
}
