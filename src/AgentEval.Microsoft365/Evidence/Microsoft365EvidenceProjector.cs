// SPDX-License-Identifier: MIT
// Copyright (c) 2026 AgentEval Contributors
// Licensed under the MIT License.

using System.Collections.Immutable;

namespace AgentEval.Microsoft365.Evidence;

/// <summary>
/// Projects a <see cref="Microsoft365TurnEvidence"/> document into the JSON document stored under the AEF run's
/// content capture, under a content policy. It never flattens diagnostics into answer text.
/// </summary>
public static class Microsoft365EvidenceProjector
{
    /// <summary>
    /// Renders the document as the JSON text stored in <c>AefNativeContent.Document</c> when content capture is on:
    /// always the <see cref="Microsoft365EvidenceJson"/> form (schema <see cref="Microsoft365Evidence.SchemaVersion"/>
    /// inside, never null), with <paramref name="policy"/> applied field by field. Only a
    /// <see cref="FieldState.Present"/> value is ever removed; it becomes <see cref="FieldState.Redacted"/> and keeps
    /// its source pointers and derivation. Every other field state (missing, null, invalid), every availability,
    /// validation, coverage and scope, and every count stays as observed, so a reader can tell "removed by policy"
    /// from "never sent".
    /// <list type="bullet">
    /// <item><see cref="EvidenceContentPolicy.FullEvidence"/>: the whole document.</item>
    /// <item><see cref="EvidenceContentPolicy.SelectedContent"/> (the allow-list of the design's data policy: answer
    /// text, citation display names and URLs, counts and states): the payloads are redacted — tool execution arguments
    /// and results, proposed approval arguments (raw text and parsed), retrieval query and filter text, retrieved hit
    /// URLs, passage text and resource metadata.</item>
    /// <item><see cref="EvidenceContentPolicy.MetadataOnly"/>: the payloads, and also the answer's raw and display
    /// text and the citation sources' URLs and display names.</item>
    /// </list>
    /// Kept under every policy, as metadata: tool, plugin and skill names, provider and local ids, statuses, outcomes,
    /// timings, citation markers (marker characters, reference keys and positions, not text), capture references and
    /// issue messages (which carry no tenant content by contract).
    /// </summary>
    /// <param name="evidence">The evidence document.</param>
    /// <param name="policy">The content policy of the run.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="policy"/> is not a defined policy.</exception>
    public static string ToAefDocument(Microsoft365TurnEvidence evidence, EvidenceContentPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var projected = policy switch
        {
            EvidenceContentPolicy.FullEvidence => evidence,
            EvidenceContentPolicy.SelectedContent => WithoutPayloads(evidence),
            EvidenceContentPolicy.MetadataOnly => WithoutAnswerAndCitationContent(WithoutPayloads(evidence)),
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown evidence content policy."),
        };

        return Microsoft365EvidenceJson.Serialize(projected);
    }

    /// <summary>Redacts the payload fields that no policy below <see cref="EvidenceContentPolicy.FullEvidence"/> retains.</summary>
    private static Microsoft365TurnEvidence WithoutPayloads(Microsoft365TurnEvidence evidence) => evidence with
    {
        ToolExecutions = Map(evidence.ToolExecutions, execution => execution with
        {
            Arguments = Redact(execution.Arguments),
            Result = Redact(execution.Result),
        }),
        ApprovalIntents = Map(evidence.ApprovalIntents, intent => intent with
        {
            ProposedArgumentsRawText = Redact(intent.ProposedArgumentsRawText),
            ProposedArguments = Redact(intent.ProposedArguments),
        }),
        RetrievalExecutions = Map(evidence.RetrievalExecutions, retrieval => retrieval with
        {
            Queries = Map(retrieval.Queries, query => query with
            {
                QueryText = Redact(query.QueryText),
                FilterExpression = Redact(query.FilterExpression),
            }),
            Hits = Map(retrieval.Hits, hit => hit with
            {
                WebUrl = Redact(hit.WebUrl),
                ResourceMetadata = Redact(hit.ResourceMetadata),
                Passages = Map(hit.Passages, passage => passage with { Text = Redact(passage.Text) }),
            }),
        }),
    };

    /// <summary>Redacts the answer text and the citation sources' URLs and display names (<see cref="EvidenceContentPolicy.MetadataOnly"/>).</summary>
    private static Microsoft365TurnEvidence WithoutAnswerAndCitationContent(Microsoft365TurnEvidence evidence) => evidence with
    {
        Answer = evidence.Answer is null ? null : evidence.Answer with
        {
            RawText = Redact(evidence.Answer.RawText),
            DisplayText = Redact(evidence.Answer.DisplayText),
        },
        CitationSources = Map(evidence.CitationSources, source => source with
        {
            Url = Redact(source.Url),
            DisplayName = Redact(source.DisplayName),
        }),
    };

    /// <summary>Projects every item of a set; availability, validation, coverage, scope, sources and issues are kept.</summary>
    private static EvidenceSet<T> Map<T>(EvidenceSet<T> set, Func<T, T> project) =>
        set is null || set.Items.IsDefaultOrEmpty ? set! : set with { Items = set.Items.Select(project).ToImmutableArray() };

    /// <summary>A present value becomes <see cref="FieldState.Redacted"/> (sources and derivation kept); any other state is returned as observed.</summary>
    private static Observed<T> Redact<T>(Observed<T> observed) =>
        observed is { State: FieldState.Present } ? observed with { State = FieldState.Redacted, Value = default } : observed;
}
