// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors

using AgentEval.Memory.External.Models;

namespace AgentEval.Memory.External.TypedMemEval;

/// <summary>
/// Turns adapter-supplied evidence references into realised gold coverage and an attribution.
/// </summary>
/// <remarks>
/// <para>
/// Uses the evidence channel that already exists — <see cref="EvidenceCaptureMode.References"/> and
/// the <c>agenteval.question_evidence.v1</c> envelope. No new capability interface: the channel,
/// its bounds, and its validation are shipped and consumed, and adding a second way to say the
/// same thing would only create two things to keep in sync.
/// </para>
/// <para>
/// What this computes is <b>reference-level presence</b>, and the naming says so. A gold session id
/// appearing in the answer context does not prove the gold value survived the memory system's own
/// summarization — many systems store compressed representations — so the number is necessary, not
/// sufficient. Where the envelope carries content, the check upgrades to a content-level match and
/// the report records which level was used.
/// </para>
/// </remarks>
internal static class TypedMemEvalCoverage
{
    /// <summary>What the evidence channel could be made to say about one question.</summary>
    internal readonly record struct Result(
        double? Coverage,
        TypedMemEvalCoverageSource Source,
        TypedMemEvalEvidenceAttribution Attribution,
        TypedMemEvalAttributionLevel Level,
        IReadOnlyList<TypedMemEvalComponentCoverage>? Components);

    /// <summary>The honest answer when no telemetry arrived: nothing observed, nothing guessed.</summary>
    internal static Result Unobserved { get; } = new(
        null,
        TypedMemEvalCoverageSource.None,
        TypedMemEvalEvidenceAttribution.Unobserved,
        TypedMemEvalAttributionLevel.None,
        null);

    /// <summary>
    /// Computes coverage for one question.
    /// </summary>
    /// <param name="envelope">Adapter-supplied evidence, or null when none was captured.</param>
    /// <param name="extension">The question's family block, carrying gold indices and session ids.</param>
    /// <param name="goldTexts">
    /// Gold session index to the text of its answer-bearing turn, used only for the optional
    /// content-level upgrade.
    /// </param>
    internal static Result Compute(
        QuestionEvidenceEnvelope? envelope,
        TypedMemEvalExtension extension,
        IReadOnlyDictionary<int, string>? goldTexts = null)
    {
        if (envelope is null)
            return Unobserved;

        // Answer context is what the model actually saw; retrieval is only an upper bound on it.
        // Preferring one and naming which was used keeps a generous adapter and a precise one from
        // producing the same-looking number.
        var (references, source) = envelope.AnswerContext.Count > 0
            ? (envelope.AnswerContext, TypedMemEvalCoverageSource.AnswerContext)
            : envelope.Retrieved.Count > 0
                ? (envelope.Retrieved, TypedMemEvalCoverageSource.Retrieved)
                : ((IReadOnlyList<EvidenceReference>)[], TypedMemEvalCoverageSource.None);

        var referenced = references
            .Select(r => r.SourceSessionId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.Ordinal);

        // An adapter that supplied references but no session identifiers has not said nothing was
        // retrieved — it has withheld the thing coverage is computed from. Reporting zero there
        // would be a fabricated measurement, so it stays unobserved.
        if (references.Count > 0 && referenced.Count == 0)
            return Unobserved;

        // A never-known probe has no gold to cover. Coverage is vacuously complete; the question is
        // whether the system invents something, not whether it retrieved anything.
        if (extension.GoldSessionIndices.Count == 0)
        {
            return new Result(
                1.0, source, TypedMemEvalEvidenceAttribution.EvidencePresent,
                TypedMemEvalAttributionLevel.Reference, null);
        }

        // An empty envelope IS an observation: the adapter reported, and what it reported is that
        // nothing was surfaced. That is coverage zero, not unknown coverage — the same zero-versus-
        // null distinction the rest of this file keeps.
        if (referenced.Count == 0)
        {
            return new Result(
                0.0, source, TypedMemEvalEvidenceAttribution.EvidenceAbsent,
                TypedMemEvalAttributionLevel.Reference,
                extension.GoldComponents?
                    .Select(c => new TypedMemEvalComponentCoverage
                    {
                        Kind = c.Kind,
                        SessionIndex = c.SessionIndex,
                        Present = false
                    })
                    .ToArray());
        }

        var contentBySession = references
            .Where(r => !string.IsNullOrEmpty(r.SourceSessionId) && !string.IsNullOrEmpty(r.Content))
            .GroupBy(r => r.SourceSessionId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => string.Join(" ", g.Select(r => r.Content)), StringComparer.Ordinal);

        // Presence is computed once per gold session and memoised, so the component list and the
        // coverage fraction cannot disagree about the same session.
        var presence = new Dictionary<int, bool>();
        var contentCheckedAll = true;

        bool Present(int sessionIndex)
        {
            if (presence.TryGetValue(sessionIndex, out var cached))
                return cached;

            bool result;
            if (sessionIndex < 0 || sessionIndex >= extension.SessionIds.Count)
            {
                result = false;
            }
            else
            {
                var sessionId = extension.SessionIds[sessionIndex];
                if (!referenced.Contains(sessionId))
                {
                    result = false;
                }
                else if (goldTexts is not null &&
                         goldTexts.TryGetValue(sessionIndex, out var goldText) &&
                         contentBySession.TryGetValue(sessionId, out var surfaced))
                {
                    // Content-level: the reference is present AND the gold text survived into what
                    // was surfaced.
                    result = Contains(surfaced, goldText);
                }
                else
                {
                    // Reference-level only for this component, so the run as a whole cannot claim
                    // the stronger level. Claiming Content because *some* component carried content
                    // would advertise an observation the weakest link never made.
                    contentCheckedAll = false;
                    result = true;
                }
            }

            presence[sessionIndex] = result;
            return result;
        }

        var present = extension.GoldSessionIndices.Count(Present);
        var coverage = (double)present / extension.GoldSessionIndices.Count;
        var level = contentCheckedAll
            ? TypedMemEvalAttributionLevel.Content
            : TypedMemEvalAttributionLevel.Reference;

        // Components are labelled where the corpus labels them (Forgetting's statement and
        // invalidation, Arithmetic's inputs), because "retrieved the statement but not the
        // invalidation" is a first-class diagnostic rather than a footnote on a fraction.
        IReadOnlyList<TypedMemEvalComponentCoverage>? components = null;
        if (extension.GoldComponents is { Count: > 0 } declared)
        {
            components = declared
                .Select(c => new TypedMemEvalComponentCoverage
                {
                    Kind = c.Kind,
                    SessionIndex = c.SessionIndex,
                    Present = Present(c.SessionIndex)
                })
                .ToArray();
        }
        else if (extension.Derivation is { Inputs.Count: > 0 } derivation)
        {
            components = derivation.Inputs
                .Select(i => new TypedMemEvalComponentCoverage
                {
                    Kind = "input",
                    SessionIndex = i.SessionIndex,
                    Present = Present(i.SessionIndex)
                })
                .ToArray();
        }

        var attribution = present == extension.GoldSessionIndices.Count
            ? TypedMemEvalEvidenceAttribution.EvidencePresent
            : TypedMemEvalEvidenceAttribution.EvidenceAbsent;

        return new Result(coverage, source, attribution, level, components);
    }

    /// <summary>
    /// Whether surfaced text carries the gold statement, compared on a distinctive-word overlap
    /// rather than a substring match, because a memory store that paraphrases while preserving the
    /// fact has still preserved the fact.
    /// </summary>
    private static bool Contains(string surfaced, string goldText)
    {
        if (string.IsNullOrWhiteSpace(goldText))
            return true;
        if (surfaced.Contains(goldText, StringComparison.OrdinalIgnoreCase))
            return true;

        var goldWords = Distinctive(goldText);
        if (goldWords.Count == 0)
            return true;
        var surfacedWords = Distinctive(surfaced);
        var shared = goldWords.Count(w => surfacedWords.Contains(w));
        return (double)shared / goldWords.Count >= 0.6;
    }

    private static HashSet<string> Distinctive(string text)
        => text
            .Split(
                [' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '(', ')', '"', '\''],
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3)
            .Select(w => w.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
}
